using Monarch.Desktop.Authentication;

var valid = new MsalDesktopAuthenticationOptions(
    "https://login.example.com/tenant/v2.0",
    "11111111-2222-3333-4444-555555555555",
    new[] { "api://monarch-erp/access_as_user", "api://monarch-erp/access_as_user" });
Check(valid.Authority == "https://login.example.com/tenant/v2.0", "HTTPS authority normalized");
Check(valid.Scopes.Count == 1, "duplicate scopes removed");
Check(valid.Scopes[0] == "api://monarch-erp/access_as_user", "API scope retained");

ExpectArgument(() => new MsalDesktopAuthenticationOptions(
    "http://login.example.com/tenant", "11111111-2222-3333-4444-555555555555", new[] { "api://monarch/access" }),
    "plaintext authority rejected");
ExpectArgument(() => new MsalDesktopAuthenticationOptions(
    "https://user:pass@login.example.com/tenant", "11111111-2222-3333-4444-555555555555", new[] { "api://monarch/access" }),
    "authority user info rejected");
ExpectArgument(() => new MsalDesktopAuthenticationOptions(
    "https://login.example.com/tenant?prompt=none", "11111111-2222-3333-4444-555555555555", new[] { "api://monarch/access" }),
    "authority query rejected");
ExpectArgument(() => new MsalDesktopAuthenticationOptions(
    "https://login.example.com/tenant", "not-a-client-id", new[] { "api://monarch/access" }),
    "non-GUID client ID rejected");
ExpectArgument(() => new MsalDesktopAuthenticationOptions(
    "https://login.example.com/tenant", "11111111-2222-3333-4444-555555555555", Array.Empty<string>()),
    "empty API scopes rejected");
ExpectArgument(() => new MsalDesktopAuthenticationOptions(
    "https://login.example.com/tenant", "11111111-2222-3333-4444-555555555555", new[] { "offline_access" }),
    "caller-supplied offline_access rejected");

Console.WriteLine("PASS: desktop OIDC configuration validation.");
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
}
static void ExpectArgument(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("FAIL: " + message);
}
