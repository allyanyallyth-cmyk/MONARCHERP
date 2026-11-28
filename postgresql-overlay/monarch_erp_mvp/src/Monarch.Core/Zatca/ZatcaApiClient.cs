using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Monarch.Core.Zatca;

public interface IZatcaCredentialProvider
{
    ZatcaApiCredentials GetCredentials(Guid companyId, Guid egsUnitId);
}

public sealed class ZatcaApiOptions
{
    public Uri BaseAddress { get; set; } = new Uri("https://gw-fatoora.zatca.gov.sa/e-invoicing/developer-portal/");
    public string TokenPath { get; set; } = "connect/token";
    public string ClearancePath { get; set; } = "invoices/clearance/single";
    public string ReportingPath { get; set; } = "invoices/reporting/single";
    public string AcceptVersion { get; set; } = "V2";
    public string Language { get; set; } = "en";
    public static ZatcaApiOptions Sandbox() => new ZatcaApiOptions();
    public static ZatcaApiOptions Production() => new ZatcaApiOptions { BaseAddress = new Uri("https://gw-fatoora.zatca.gov.sa/e-invoicing/core/") };
}

/// <summary>FATOORA OAuth2 Basic client and Clearance/Reporting API transport.</summary>
public sealed class ZatcaApiClient
{
    private readonly HttpClient _http;
    private readonly ZatcaApiOptions _options;
    private readonly IZatcaCredentialProvider _credentials;

    public ZatcaApiClient(HttpClient http, ZatcaApiOptions options, IZatcaCredentialProvider credentials)
    { _http = http ?? throw new ArgumentNullException(nameof(http)); _options = options ?? throw new ArgumentNullException(nameof(options)); _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials)); }

    public async Task<ZatcaApiResult> SubmitAsync(Guid companyId, Guid egsUnitId, ZatcaInvoiceKind kind,
        string uuid, string invoiceHashBase64, string invoiceXml, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceXml) || string.IsNullOrWhiteSpace(invoiceHashBase64) || string.IsNullOrWhiteSpace(uuid)) throw new ArgumentException("UUID, invoice hash and XML are required.");
        var standard = kind == ZatcaInvoiceKind.StandardTaxInvoice || kind == ZatcaInvoiceKind.CreditNote || kind == ZatcaInvoiceKind.DebitNote;
        var token = await GetAccessTokenAsync(companyId, egsUnitId, cancellationToken).ConfigureAwait(false);
        var path = standard ? _options.ClearancePath : _options.ReportingPath;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Accept-Version", _options.AcceptVersion);
        request.Headers.TryAddWithoutValidation("Accept-Language", _options.Language);
        if (standard) request.Headers.TryAddWithoutValidation("Clearance-Status", "1");
        request.Content = new StringContent("{\"invoiceHash\":\"" + Escape(invoiceHashBase64) + "\",\"uuid\":\"" + Escape(uuid) + "\",\"invoice\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(invoiceXml)) + "\"}", Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsed = Deserialize<ApiInvoiceResponse>(raw);
        var validationPassed = string.Equals(parsed?.ValidationResults?.Status, "PASS", StringComparison.OrdinalIgnoreCase);
        var accepted = response.IsSuccessStatusCode && parsed != null &&
            (standard
                ? !string.IsNullOrWhiteSpace(parsed.ClearedInvoice)
                : (validationPassed || string.Equals(parsed.Status, "REPORTED", StringComparison.OrdinalIgnoreCase)));
        var validationFailed = string.Equals(parsed?.ValidationResults?.Status, "ERROR", StringComparison.OrdinalIgnoreCase) ||
            (parsed?.ValidationResults?.ErrorMessages != null && parsed.ValidationResults.ErrorMessages.Length > 0);
        var status = accepted ? (standard ? "Cleared" : "Reported") : validationFailed ? "Rejected" : "Failed";
        string? clearedXml = null;
        if (!string.IsNullOrWhiteSpace(parsed?.ClearedInvoice))
        {
            try { clearedXml = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.ClearedInvoice)); }
            catch (FormatException) { status = "Failed"; accepted = false; }
        }
        var errors = BuildErrors(parsed);
        return new ZatcaApiResult(response.IsSuccessStatusCode, accepted, status, clearedXml,
            parsed?.QrCode, errors, raw);
    }

    private async Task<string> GetAccessTokenAsync(Guid companyId, Guid egsUnitId, CancellationToken cancellationToken)
    {
        var pair = _credentials.GetCredentials(companyId, egsUnitId);
        if (string.IsNullOrWhiteSpace(pair.ClientId) || string.IsNullOrWhiteSpace(pair.ClientSecret)) throw new InvalidOperationException("ZATCA onboarding credentials are not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseAddress, _options.TokenPath));
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.ClientId + ":" + pair.ClientSecret));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("ZATCA OAuth token request failed: HTTP " + (int)response.StatusCode + ".");
        var token = Deserialize<TokenResponse>(raw)?.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("ZATCA OAuth response did not contain an access_token.");
        return token;
    }

    private static string? BuildErrors(ApiInvoiceResponse? response)
    {
        if (response == null) return "Unrecognized ZATCA response.";
        var result = new StringBuilder();
        Append(result, response.ValidationResults?.ErrorMessages); Append(result, response.ValidationResults?.WarningMessages);
        return result.Length == 0 ? null : result.ToString();
    }
    private static void Append(StringBuilder target, ApiMessage[]? messages)
    {
        if (messages == null) return;
        foreach (var item in messages) { if (target.Length > 0) target.Append(" | "); target.Append(item.Message ?? item.Code ?? "ZATCA validation message"); }
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    private static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)); return (T?)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        catch (Exception) { return null; }
    }

    [DataContract] private sealed class TokenResponse { [DataMember(Name = "access_token")] public string? AccessToken { get; set; } }
    [DataContract] private sealed class ApiInvoiceResponse
    {
        [DataMember(Name = "status")] public string? Status { get; set; }
        [DataMember(Name = "clearedInvoice")] public string? ClearedInvoice { get; set; }
        [DataMember(Name = "qrCode")] public string? QrCode { get; set; }
        [DataMember(Name = "validationResults")] public ValidationResults? ValidationResults { get; set; }
    }
    [DataContract] private sealed class ValidationResults
    {
        [DataMember(Name = "status")] public string? Status { get; set; }
        [DataMember(Name = "errorMessages")] public ApiMessage[]? ErrorMessages { get; set; }
        [DataMember(Name = "warningMessages")] public ApiMessage[]? WarningMessages { get; set; }
    }
    [DataContract] private sealed class ApiMessage
    {
        [DataMember(Name = "code")] public string? Code { get; set; }
        [DataMember(Name = "message")] public string? Message { get; set; }
    }
}
