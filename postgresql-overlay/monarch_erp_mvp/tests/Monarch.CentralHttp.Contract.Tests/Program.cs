using System.Net;
using System.Net.Http;
using Monarch.Core.Abstractions;
using Monarch.Core.Remote;

var companyId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
var branchId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
var tenant = new TenantScope(companyId, branchId, Guid.Empty);
var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("[]")
});
using var http = new HttpClient(handler)
{
    BaseAddress = new Uri("https://api.example.test/api/v1/general-ledger/"),
    Timeout = TimeSpan.FromSeconds(30)
};
var tokens = new FixedTokenProvider("contract-test-access-token");
var ledger = new HttpGeneralLedgerStore(http, tokens);

var accounts = await ledger.GetAccountsAsync(tenant);
Assert(accounts.Count == 0, "accounts list accepts an empty JSON array");
AssertRequest(handler.Last, "accounts?includeInactive=false", companyId, branchId);

var journals = await ledger.GetJournalsAsync(tenant, limit: 125);
Assert(journals.Count == 0, "journal list accepts an empty JSON array");
AssertRequest(handler.Last, "journals?limit=125", companyId, branchId);

var trialBalance = await ledger.GetTrialBalanceAsync(
    tenant, new DateOnly(2026, 11, 28), "SAR");
Assert(trialBalance.Count == 0, "trial balance accepts an empty JSON array");
AssertRequest(handler.Last, "reports/trial-balance?asOfDate=2026-11-28&currencyCode=SAR", companyId, branchId);

handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
var missingJournal = await ledger.GetJournalAsync(
    tenant, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
Assert(missingJournal is null, "journal detail 404 maps to null without leaking an exception");
AssertRequest(handler.Last,
    "journals/cccccccc-cccc-4ccc-8ccc-cccccccccccc", companyId, branchId);

var requestCount = handler.Requests.Count;
var invalidTenantRejected = false;
try
{
    await ledger.GetAccountsAsync(new TenantScope(Guid.Empty, branchId, Guid.Empty));
}
catch (ArgumentException)
{
    invalidTenantRejected = true;
}
Assert(invalidTenantRejected, "empty company identifier is rejected before transport");
Assert(handler.Requests.Count == requestCount, "invalid tenant selection sends no HTTP request");

Console.WriteLine("PASS: central HTTP adapter contract checks (14 assertions).");

static void AssertRequest(
    RequestSnapshot? request, string expectedPath, Guid companyId, Guid branchId)
{
    Assert(request is not null, "HTTP request was recorded");
    Assert(request!.PathAndQuery == "/" + expectedPath, "relative API route is exact");
    Assert(request.Authorization == "Bearer contract-test-access-token", "bearer access token is attached");
    Assert(request.CompanyId == companyId.ToString("D"), "company tenant selector is attached");
    Assert(request.BranchId == branchId.ToString("D"), "branch tenant selector is attached");
    Assert(!request.HasUserIdHeader, "desktop does not send a user identity header");
}

static void Assert(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

sealed class FixedTokenProvider(string token) : IAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(token);
}

sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = responder;
    public List<RequestSnapshot> Requests { get; } = new();
    public RequestSnapshot? Last => Requests.Count == 0 ? null : Requests[^1];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RequestSnapshot(
            request.RequestUri!.PathAndQuery,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("X-Monarch-Company-Id", out var company) ? company.Single() : null,
            request.Headers.TryGetValues("X-Monarch-Branch-Id", out var branch) ? branch.Single() : null,
            request.Headers.Contains("X-Monarch-User-Id")));
        return Task.FromResult(Responder(request));
    }
}

sealed record RequestSnapshot(
    string PathAndQuery,
    string? Authorization,
    string? CompanyId,
    string? BranchId,
    bool HasUserIdHeader);
