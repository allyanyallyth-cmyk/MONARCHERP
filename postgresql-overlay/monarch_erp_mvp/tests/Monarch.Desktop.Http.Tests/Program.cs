using System.Net;
using System.Net.Http;
using System.Text;
using Monarch.Core.Abstractions;
using Monarch.Core.Remote;

var companyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
var branchId = Guid.Parse("20000000-0000-0000-0000-000000000002");
var tenant = new TenantScope(companyId, branchId, Guid.Empty);
var accountId = Guid.Parse("30000000-0000-0000-0000-000000000003");
var journalId = Guid.Parse("40000000-0000-0000-0000-000000000004");
var requests = new List<string>();
var handler = new ContractHandler((request, count) =>
{
    requests.Add(request.Method + " " + request.RequestUri);
    Check(request.Headers.Authorization?.Scheme == "Bearer" &&
          request.Headers.Authorization.Parameter == "smoke-access-token", "bearer access token attached");
    Check(request.Headers.GetValues("X-Monarch-Company-Id").Single() == companyId.ToString("D"), "company selection header attached");
    Check(request.Headers.GetValues("X-Monarch-Branch-Id").Single() == branchId.ToString("D"), "branch selection header attached");

    if (request.RequestUri!.AbsolutePath.EndsWith("/accounts", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        return Json(HttpStatusCode.OK, "[{\"accountId\":\"30000000-0000-0000-0000-000000000003\",\"accountCode\":\"1000\",\"accountName\":\"Cash\",\"accountType\":\"Asset\",\"normalSide\":\"Debit\"}]");
    if (request.RequestUri.AbsolutePath.EndsWith("/journals", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        return Json(HttpStatusCode.OK, """[{"journalId":"40000000-0000-0000-0000-000000000004","journalNumber":17,"fiscalYear":2026,"journalDate":"2026-11-27","description":"Smoke journal","currencyCode":"SAR","status":"POSTED","totalDebitMinor":10000}]""");
    if (request.RequestUri.AbsolutePath.EndsWith("/accounts", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        return Json(HttpStatusCode.Created, "{\"accountId\":\"30000000-0000-0000-0000-000000000003\"}");
    if (request.RequestUri.AbsolutePath.EndsWith("/journals", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        Check(body.Contains("\"journalDate\":\"2026-11-27\"", StringComparison.Ordinal), "journal date serialized as ISO yyyy-MM-dd");
        Check(body.Contains("\"debitMinor\":10000", StringComparison.Ordinal), "journal amount serialized as integer minor units");
        return Json(HttpStatusCode.OK, "{\"journalId\":\"40000000-0000-0000-0000-000000000004\",\"journalNumber\":17,\"fiscalYear\":2026,\"journalDate\":\"2026-11-27\",\"currencyCode\":\"SAR\",\"status\":\"Draft\"}");
    }
    if (request.RequestUri.AbsolutePath.EndsWith("/journals/" + journalId.ToString("D"), StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, "{\"journal\":{\"journalId\":\"40000000-0000-0000-0000-000000000004\",\"journalNumber\":17,\"fiscalYear\":2026,\"journalDate\":\"2026-11-27\",\"currencyCode\":\"SAR\",\"status\":\"Draft\"},\"description\":\"Smoke journal\",\"createdBy\":\"50000000-0000-0000-0000-000000000005\",\"postedBy\":null,\"reversesJournalId\":null,\"lines\":[{\"lineNumber\":1,\"accountId\":\"30000000-0000-0000-0000-000000000003\",\"description\":\"Debit\",\"debitMinor\":10000,\"creditMinor\":0}]}");
    if (request.RequestUri.AbsolutePath.EndsWith("/post", StringComparison.Ordinal))
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    if (request.RequestUri.AbsolutePath.EndsWith("/trial-balance", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, "[{\"accountId\":\"30000000-0000-0000-0000-000000000003\",\"accountCode\":\"1000\",\"accountName\":\"Cash\",\"debitMinor\":10000,\"creditMinor\":0}]");
    return Json(HttpStatusCode.NotFound, "{}");
});
var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/api/v1/general-ledger/") };
var store = new HttpGeneralLedgerStore(http, new StaticTokenProvider());

var accounts = await store.GetAccountsAsync(tenant);
Check(accounts.Count == 1 && accounts[0].AccountId == accountId, "account list DTO mapping");
var journals = await store.GetJournalsAsync(tenant);
Check(journals.Count == 1 && journals[0].JournalId == journalId && journals[0].TotalDebitMinor == 10000,
    "journal list DTO mapping and aggregate amount");
await store.CreateAccountAsync(tenant, new GeneralLedgerAccount(accountId, "1000", "Cash", "Asset", "Debit"));
var lines = new[] { new GeneralLedgerLineInput(accountId, 10000, 0, "Debit"), new GeneralLedgerLineInput(accountId, 0, 10000, "Credit") };
var draft = await store.CreateDraftAsync(tenant, journalId, 2026, new DateOnly(2026, 11, 27), "Smoke journal", "SAR", lines);
Check(draft.JournalDate == new DateOnly(2026, 11, 27) && draft.JournalNumber == 17, "journal response and DateOnly mapping");
var detail = await store.GetJournalAsync(tenant, journalId);
Check(detail != null && detail.Lines.Count == 1 && detail.Lines[0].DebitMinor == 10000, "journal detail mapping");
await store.PostAsync(tenant, journalId);
var trial = await store.GetTrialBalanceAsync(tenant, new DateOnly(2026, 11, 27), "SAR");
Check(trial.Count == 1 && trial[0].DebitMinor == 10000, "trial-balance DTO mapping");
Check(requests.Count == 7, "all seven HTTP operations executed exactly once");

var forbiddenHandler = new ContractHandler((_, _) => Json(HttpStatusCode.Forbidden, "{\"title\":\"Forbidden\"}"));
var forbiddenStore = new HttpGeneralLedgerStore(
    new HttpClient(forbiddenHandler) { BaseAddress = new Uri("https://localhost/api/v1/general-ledger/") },
    new StaticTokenProvider());
await ExpectFailure(() => forbiddenStore.GetAccountsAsync(tenant), typeof(UnauthorizedAccessException), "403 is fail-closed");

var unavailableCount = 0;
var unavailableStore = new HttpGeneralLedgerStore(
    new HttpClient(new ContractHandler((_, _) => { unavailableCount++; return Json(HttpStatusCode.ServiceUnavailable, "{}"); }))
        { BaseAddress = new Uri("https://localhost/api/v1/general-ledger/") },
    new StaticTokenProvider());
await ExpectFailure(() => unavailableStore.CreateDraftAsync(tenant, journalId, 2026,
    new DateOnly(2026, 11, 27), "Unknown outcome", "SAR", lines), typeof(HttpRequestException),
    "5xx write outcome is surfaced without automatic retry");
Check(unavailableCount == 1, "write is not automatically retried");

ExpectFailureSync(() => new HttpGeneralLedgerStore(
    new HttpClient(new ContractHandler((_, _) => Json(HttpStatusCode.OK, "[]")))
        { BaseAddress = new Uri("http://example.com/api/v1/general-ledger/") },
    new StaticTokenProvider()), typeof(ArgumentException), "remote plaintext HTTP rejected");
ExpectFailureSync(() => new HttpGeneralLedgerStore(
    new HttpClient(new ContractHandler((_, _) => Json(HttpStatusCode.OK, "[]")))
        { BaseAddress = new Uri("https://localhost/api/v1/general-ledger") },
    new StaticTokenProvider()), typeof(ArgumentException), "base address without trailing slash rejected");

var discoveredBranchId = Guid.Parse("60000000-0000-0000-0000-000000000006");
var discoveryPaths = new List<string>();
var discoveryHandler = new ContractHandler((request, _) =>
{
    discoveryPaths.Add(request.RequestUri!.AbsolutePath);
    Check(request.Method == HttpMethod.Get, "tenant discovery uses GET only");
    Check(request.Headers.Authorization?.Scheme == "Bearer" &&
          request.Headers.Authorization.Parameter == "smoke-access-token", "tenant discovery bearer token attached");
    if (request.RequestUri.AbsolutePath.EndsWith("/tenant-context/companies", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, "[{\"companyId\":\"10000000-0000-0000-0000-000000000001\",\"companyCode\":\"ACME\",\"legalName\":\"ACME Holdings\"}]");
    if (request.RequestUri.AbsolutePath.EndsWith("/tenant-context/companies/" + companyId.ToString("D") + "/branches", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, "[{\"branchId\":\"60000000-0000-0000-0000-000000000006\",\"branchCode\":\"HQ\",\"branchName\":\"Head Office\"}]");
    return Json(HttpStatusCode.NotFound, "{}");
});
var discoveryClient = new HttpTenantDiscoveryClient(
    new HttpClient(discoveryHandler) { BaseAddress = new Uri("https://localhost/api/v1/tenant-context/") },
    new StaticTokenProvider());
var discoveredCompanies = await discoveryClient.ListCompaniesAsync();
Check(discoveredCompanies.Count == 1 && discoveredCompanies[0].CompanyId == companyId &&
      discoveredCompanies[0].LegalName == "ACME Holdings", "tenant company discovery DTO mapping");
var discoveredBranches = await discoveryClient.ListBranchesAsync(companyId);
Check(discoveredBranches.Count == 1 && discoveredBranches[0].BranchId == discoveredBranchId &&
      discoveredBranches[0].BranchCode == "HQ", "tenant branch discovery DTO mapping");
Check(discoveryPaths.Count == 2 && discoveryPaths[1].EndsWith("/companies/" + companyId.ToString("D") + "/branches", StringComparison.Ordinal),
    "tenant discovery uses the selected company in the branch path");
await ExpectFailure(() => discoveryClient.ListBranchesAsync(Guid.Empty), typeof(ArgumentException),
    "empty company selection rejected before HTTP");

var discoveryUnauthorized = new HttpTenantDiscoveryClient(
    new HttpClient(new ContractHandler((_, _) => Json(HttpStatusCode.Unauthorized, "{}")))
        { BaseAddress = new Uri("https://localhost/api/v1/tenant-context/") },
    new StaticTokenProvider());
await ExpectFailure(() => discoveryUnauthorized.ListCompaniesAsync(), typeof(LedgerAuthenticationException),
    "tenant discovery 401 requires re-authentication");
var discoveryForbidden = new HttpTenantDiscoveryClient(
    new HttpClient(new ContractHandler((_, _) => Json(HttpStatusCode.Forbidden, "{}")))
        { BaseAddress = new Uri("https://localhost/api/v1/tenant-context/") },
    new StaticTokenProvider());
await ExpectFailure(() => discoveryForbidden.ListCompaniesAsync(), typeof(UnauthorizedAccessException),
    "tenant discovery 403 remains an authorization denial");
ExpectFailureSync(() => new HttpTenantDiscoveryClient(
    new HttpClient(new ContractHandler((_, _) => Json(HttpStatusCode.OK, "[]")))
        { BaseAddress = new Uri("http://example.com/api/v1/tenant-context/") },
    new StaticTokenProvider()), typeof(ArgumentException), "remote plaintext tenant discovery rejected");
ExpectFailureSync(() => new HttpTenantDiscoveryClient(
    new HttpClient(new ContractHandler((_, _) => Json(HttpStatusCode.OK, "[]")))
        { BaseAddress = new Uri("https://localhost/api/v1/tenant-context/"), Timeout = Timeout.InfiniteTimeSpan },
    new StaticTokenProvider()), typeof(ArgumentException), "unbounded tenant discovery timeout rejected");

Console.WriteLine("PASS: central GL and tenant discovery HTTP adapter contract smoke tests.");

static HttpResponseMessage Json(HttpStatusCode status, string body) =>
    new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

static async Task ExpectFailure(Func<Task> action, Type type, string name)
{
    try { await action(); }
    catch (Exception exception) when (type.IsInstanceOfType(exception)) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}

static void ExpectFailureSync(Action action, Type type, string name)
{
    try { action(); }
    catch (Exception exception) when (type.IsInstanceOfType(exception)) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}

sealed class StaticTokenProvider : IAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("smoke-access-token");
}

sealed class ContractHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _response;
    private int _count;
    public ContractHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response) => _response = response;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_response(request, Interlocked.Increment(ref _count)));
}
