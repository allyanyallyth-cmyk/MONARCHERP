using System;
using System.Collections.Generic;
using System.Globalization;
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
/// HTTP implementation of the GL store for a central-mode desktop client.
/// The supplied HttpClient is owned/configured by the composition root and is not disposed here.
/// This adapter deliberately performs no automatic retries: a timed-out write has an unknown
/// outcome and must be reconciled by reading server state before the user retries.
/// </summary>
public sealed class HttpGeneralLedgerStore : IGeneralLedgerStore
{
    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;
    private readonly JsonSerializerSettings _json = new JsonSerializerSettings
    {
        DateFormatHandling = DateFormatHandling.IsoDateFormat
    };

    public HttpGeneralLedgerStore(HttpClient http, IAccessTokenProvider tokens)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        if (_http.BaseAddress == null || !_http.BaseAddress.IsAbsoluteUri)
            throw new ArgumentException("The central API client requires an absolute BaseAddress.", nameof(http));
        if (_http.BaseAddress.Scheme != Uri.UriSchemeHttps &&
            !(_http.BaseAddress.Scheme == Uri.UriSchemeHttp && _http.BaseAddress.IsLoopback))
            throw new ArgumentException("Central API transport requires HTTPS; HTTP is allowed only for loopback tests.", nameof(http));
        if (_http.BaseAddress.UserInfo.Length != 0 || _http.BaseAddress.Query.Length != 0 ||
            _http.BaseAddress.Fragment.Length != 0 || !_http.BaseAddress.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("The central API BaseAddress must end in '/' and must not contain credentials, query, or fragment.", nameof(http));
    }

    public async Task CreateAccountAsync(TenantScope tenant, GeneralLedgerAccount account,
        CancellationToken cancellationToken = default)
    {
        if (account == null) throw new ArgumentNullException(nameof(account));
        var payload = new
        {
            accountId = account.AccountId,
            accountCode = account.AccountCode,
            accountName = account.AccountName,
            accountType = account.AccountType,
            normalSide = account.NormalSide
        };
        using (var response = await SendAsync(HttpMethod.Post, "accounts", tenant, payload, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created)
                throw new HttpRequestException("The API returned an unexpected status for account creation.");
        }
    }

    public async Task<IReadOnlyList<GeneralLedgerAccount>> GetAccountsAsync(TenantScope tenant,
        bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        using (var response = await SendAsync(HttpMethod.Get,
            "accounts?includeInactive=" + (includeInactive ? "true" : "false"), tenant, null, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var rows = JsonConvert.DeserializeObject<List<AccountWire>>(json, _json);
            if (rows == null) throw new HttpRequestException("The API returned an empty account response.");
            return rows.Select(row => new GeneralLedgerAccount(
                row.AccountId, row.AccountCode, row.AccountName, row.AccountType, row.NormalSide)).ToArray();
        }
    }

    public async Task<GeneralLedgerJournal> CreateDraftAsync(TenantScope tenant, Guid journalId,
        int fiscalYear, DateOnly journalDate, string description, string currencyCode,
        IReadOnlyCollection<GeneralLedgerLineInput> lines, Guid? reversesJournalId = null,
        CancellationToken cancellationToken = default)
    {
        if (lines == null) throw new ArgumentNullException(nameof(lines));
        var payload = new
        {
            journalId,
            fiscalYear,
            journalDate = journalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            description,
            currencyCode,
            lines = lines.Select(line => new
            {
                accountId = line.AccountId,
                debitMinor = line.DebitMinor,
                creditMinor = line.CreditMinor,
                description = line.Description
            }).ToArray(),
            reversesJournalId
        };
        using (var response = await SendAsync(HttpMethod.Post, "journals", tenant, payload, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return MapJournal(ParseObject(json));
        }
    }

    public async Task<GeneralLedgerJournalDetail?> GetJournalAsync(TenantScope tenant, Guid journalId,
        CancellationToken cancellationToken = default)
    {
        using (var response = await SendAsync(HttpMethod.Get, "journals/" + journalId.ToString("D"),
            tenant, null, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var root = ParseObject(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var journal = MapJournal((JObject)root["journal"]);
            var lines = ((JArray)root["lines"] ?? new JArray()).Select(token =>
            {
                var line = (JObject)token;
                return new GeneralLedgerJournalLine(
                    Required<int>(line, "lineNumber"), Required<Guid>(line, "accountId"),
                    (string)line["description"], Required<long>(line, "debitMinor"),
                    Required<long>(line, "creditMinor"));
            }).ToArray();
            return new GeneralLedgerJournalDetail(journal,
                Required<string>(root, "description"), Required<Guid>(root, "createdBy"),
                NullableGuid(root["postedBy"]), NullableGuid(root["reversesJournalId"]), lines);
        }
    }

    public async Task PostAsync(TenantScope tenant, Guid journalId,
        CancellationToken cancellationToken = default)
    {
        using (var response = await SendAsync(HttpMethod.Post,
            "journals/" + journalId.ToString("D") + "/post", tenant, null, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new HttpRequestException("The API returned an unexpected status for journal posting.");
        }
    }

    public async Task<IReadOnlyList<GeneralLedgerTrialBalanceRow>> GetTrialBalanceAsync(TenantScope tenant,
        DateOnly asOfDate, string currencyCode, CancellationToken cancellationToken = default)
    {
        var path = "reports/trial-balance?asOfDate=" +
            Uri.EscapeDataString(asOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) +
            "&currencyCode=" + Uri.EscapeDataString(currencyCode ?? string.Empty);
        using (var response = await SendAsync(HttpMethod.Get, path, tenant, null, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var rows = JsonConvert.DeserializeObject<List<TrialBalanceWire>>(json, _json);
            if (rows == null) throw new HttpRequestException("The API returned an empty trial-balance response.");
            return rows.Select(row => new GeneralLedgerTrialBalanceRow(
                row.AccountId, row.AccountCode, row.AccountName, row.DebitMinor, row.CreditMinor)).ToArray();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath,
        TenantScope tenant, object payload, CancellationToken cancellationToken)
    {
        if (tenant == null) throw new ArgumentNullException(nameof(tenant));
        if (tenant.CompanyId == Guid.Empty || tenant.BranchId == Guid.Empty)
            throw new ArgumentException("A valid company and branch selection is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("/", StringComparison.Ordinal) ||
            relativePath.Contains("://") || relativePath.Contains("\\"))
            throw new ArgumentException("Only relative General Ledger API paths are allowed.", nameof(relativePath));

        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token) || token.IndexOfAny(new[] { '\r', '\n', ' ' }) >= 0)
            throw new UnauthorizedAccessException("The access-token provider did not return a valid bearer token.");

        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Monarch-Company-Id", tenant.CompanyId.ToString("D"));
        request.Headers.Add("X-Monarch-Branch-Id", tenant.BranchId.ToString("D"));
        if (payload != null)
            request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var safeDetail = ExtractProblemTitle(body);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LedgerAuthenticationException("The central API rejected the access token. Re-authentication is required.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("The server denied the selected tenant or operation.");
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new ArgumentException(safeDetail ?? "The central API rejected the request.");
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException(safeDetail ?? "The central API reported an operation conflict.");
        throw new HttpRequestException("The central API request failed with HTTP " + (int)response.StatusCode + ".");
    }

    private static string ExtractProblemTitle(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return (string)JObject.Parse(body)["title"]; }
        catch (JsonException) { return null; }
    }

    private static JObject ParseObject(string json)
    {
        var value = JObject.Parse(json);
        return value ?? throw new HttpRequestException("The central API returned an invalid JSON object.");
    }

    private static GeneralLedgerJournal MapJournal(JObject value)
    {
        if (value == null) throw new HttpRequestException("The central API omitted journal data.");
        var dateText = Required<string>(value, "journalDate");
        var date = DateOnly.ParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new GeneralLedgerJournal(Required<Guid>(value, "journalId"),
            Required<long>(value, "journalNumber"), Required<int>(value, "fiscalYear"),
            date, Required<string>(value, "currencyCode"), Required<string>(value, "status"));
    }

    private static T Required<T>(JObject value, string name)
    {
        var token = value[name];
        if (token == null || token.Type == JTokenType.Null)
            throw new HttpRequestException("The central API response omitted required field '" + name + "'.");
        return token.Value<T>();
    }

    private static Guid? NullableGuid(JToken token) =>
        token == null || token.Type == JTokenType.Null ? (Guid?)null : token.Value<Guid>();

    private sealed class AccountWire
    {
        public Guid AccountId { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public string AccountType { get; set; }
        public string NormalSide { get; set; }
    }

    private sealed class TrialBalanceWire
    {
        public Guid AccountId { get; set; }
        public string AccountCode { get; set; }
        public string AccountName { get; set; }
        public long DebitMinor { get; set; }
        public long CreditMinor { get; set; }
    }
}

public sealed class LedgerAuthenticationException : Exception
{
    public LedgerAuthenticationException(string message) : base(message) { }
}
