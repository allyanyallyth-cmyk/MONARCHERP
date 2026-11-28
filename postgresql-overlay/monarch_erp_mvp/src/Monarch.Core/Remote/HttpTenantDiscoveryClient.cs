using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Monarch.Core.Abstractions;

namespace Monarch.Core.Remote;

/// <summary>
/// Authenticated HTTP client for OIDC tenant discovery. It does not cache tenant grants
/// and does not treat discovery results as authorization; PostgreSQL rechecks every operation.
/// The supplied HttpClient is owned by the composition root and is not disposed here.
/// </summary>
public sealed class HttpTenantDiscoveryClient : ITenantDiscoveryClient
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;

    public HttpTenantDiscoveryClient(HttpClient http, IAccessTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        var address = _http.BaseAddress;
        if (address == null || !address.IsAbsoluteUri)
            throw new ArgumentException("The tenant discovery client requires an absolute BaseAddress.", nameof(http));
        if (address.Scheme != Uri.UriSchemeHttps &&
            !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
            throw new ArgumentException("Tenant discovery requires HTTPS; HTTP is allowed only for loopback tests.", nameof(http));
        if (address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0 ||
            !address.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("The tenant discovery BaseAddress must end in '/' and must not contain credentials, query, or fragment.", nameof(http));
        if (_http.Timeout <= TimeSpan.Zero || _http.Timeout == Timeout.InfiniteTimeSpan ||
            _http.Timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("The tenant discovery HttpClient must use a finite timeout no longer than two minutes.", nameof(http));
    }

    public async Task<IReadOnlyList<TenantCompany>> ListCompaniesAsync(CancellationToken cancellationToken = default)
    {
        using (var response = await SendAsync("companies", cancellationToken).ConfigureAwait(false))
        {
            EnsureSuccess(response);
            var json = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            var rows = Deserialize<List<CompanyWire>>(json);
            if (rows.Count > 5000)
                throw new HttpRequestException("The central API returned too many company records.");
            var ids = new HashSet<Guid>();
            var result = new List<TenantCompany>(rows.Count);
            foreach (var row in rows)
            {
                if (row.CompanyId == Guid.Empty || !ids.Add(row.CompanyId) ||
                    string.IsNullOrWhiteSpace(row.CompanyCode) || row.CompanyCode.Length > 64 ||
                    string.IsNullOrWhiteSpace(row.LegalName) || row.LegalName.Length > 256)
                    throw new HttpRequestException("The central API returned invalid or duplicate company data.");
                result.Add(new TenantCompany(row.CompanyId, row.CompanyCode, row.LegalName));
            }
            return result;
        }
    }

    public async Task<IReadOnlyList<TenantBranch>> ListBranchesAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("A valid company selection is required.", nameof(companyId));
        using (var response = await SendAsync("companies/" + companyId.ToString("D") + "/branches", cancellationToken).ConfigureAwait(false))
        {
            EnsureSuccess(response);
            var json = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            var rows = Deserialize<List<BranchWire>>(json);
            if (rows.Count > 5000)
                throw new HttpRequestException("The central API returned too many branch records.");
            var ids = new HashSet<Guid>();
            var result = new List<TenantBranch>(rows.Count);
            foreach (var row in rows)
            {
                if (row.BranchId == Guid.Empty || !ids.Add(row.BranchId) ||
                    string.IsNullOrWhiteSpace(row.BranchCode) || row.BranchCode.Length > 64 ||
                    string.IsNullOrWhiteSpace(row.BranchName) || row.BranchName.Length > 256)
                    throw new HttpRequestException("The central API returned invalid or duplicate branch data.");
                result.Add(new TenantBranch(row.BranchId, row.BranchCode, row.BranchName));
            }
            return result;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("/", StringComparison.Ordinal) ||
            relativePath.Contains("://") || relativePath.Contains("\\") || relativePath.Contains(".."))
            throw new ArgumentException("Only fixed relative tenant discovery paths are allowed.", nameof(relativePath));
        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.IndexOfAny(new[] { '\\r', '\\n', ' ' }) >= 0)
            throw new UnauthorizedAccessException("The access-token provider did not return a valid bearer token.");
        using (var request = new HttpRequestMessage(HttpMethod.Get, relativePath))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LedgerAuthenticationException("The central API rejected the access token. Re-authentication is required.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("The server denied access to the selected tenant context.");
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new ArgumentException("The central API rejected the tenant discovery request.");
        throw new HttpRequestException("The central tenant discovery request failed with HTTP " + (int)response.StatusCode + ".");
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content == null)
            throw new HttpRequestException("The central API returned an empty tenant discovery response.");
        if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > MaximumResponseBytes)
            throw new HttpRequestException("The central API tenant discovery response exceeded the size limit.");
        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumResponseBytes)
                    throw new HttpRequestException("The central API tenant discovery response exceeded the size limit.");
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            var value = JsonConvert.DeserializeObject<T>(json);
            if (value == null) throw new HttpRequestException("The central API returned an empty tenant discovery payload.");
            return value;
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("The central API returned invalid tenant discovery JSON.", exception);
        }
    }

    private sealed class CompanyWire
    {
        [JsonProperty("companyId")] public Guid CompanyId { get; set; }
        [JsonProperty("companyCode")] public string CompanyCode { get; set; }
        [JsonProperty("legalName")] public string LegalName { get; set; }
    }

    private sealed class BranchWire
    {
        [JsonProperty("branchId")] public Guid BranchId { get; set; }
        [JsonProperty("branchCode")] public string BranchCode { get; set; }
        [JsonProperty("branchName")] public string BranchName { get; set; }
    }
}