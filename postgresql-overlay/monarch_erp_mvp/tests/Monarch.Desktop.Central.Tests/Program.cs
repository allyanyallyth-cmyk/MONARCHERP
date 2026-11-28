using Monarch.Desktop.Central;

var valid = new Dictionary<string, string?>
{
    [CentralDesktopConfiguration.ApiBaseUrlVariable] = "https://api.example.test/",
    [CentralDesktopConfiguration.AuthorityVariable] = "https://login.microsoftonline.com/example-tenant/v2.0",
    [CentralDesktopConfiguration.ClientIdVariable] = "11111111-2222-3333-4444-555555555555",
    [CentralDesktopConfiguration.ApiScopesVariable] = "api://11111111-2222-3333-4444-555555555555/access_as_user"
};

var configuration = CentralDesktopConfiguration.Load(name => valid.GetValueOrDefault(name));
Assert(configuration.ApiBaseAddress.Scheme == Uri.UriSchemeHttps, "HTTPS API configuration accepted");
Assert(configuration.Authentication.Scopes.Count == 1, "API scope parsed");

ExpectFailure(new Dictionary<string, string?>(), "missing configuration rejected");
ExpectFailure(With(CentralDesktopConfiguration.ApiBaseUrlVariable, "http://api.example.test/"), "non-loopback HTTP rejected");
ExpectFailure(With(CentralDesktopConfiguration.AuthorityVariable, "http://identity.example.test/"), "non-HTTPS identity authority rejected");
ExpectFailure(With(CentralDesktopConfiguration.ClientIdVariable, "not-a-guid"), "invalid public-client ID rejected");
ExpectFailure(With(CentralDesktopConfiguration.ApiScopesVariable, "openid profile"), "reserved OIDC scopes rejected");
Console.WriteLine("PASS: central desktop configuration security checks (7 assertions).");

Dictionary<string, string?> With(string key, string value)
{
    var copy = new Dictionary<string, string?>(valid) { [key] = value };
    return copy;
}

void ExpectFailure(IReadOnlyDictionary<string, string?> values, string name)
{
    try
    {
        _ = CentralDesktopConfiguration.Load(key => values.TryGetValue(key, out var value) ? value : null);
        throw new InvalidOperationException("Expected failure: " + name);
    }
    catch (InvalidOperationException exception) when (!exception.Message.StartsWith("Expected failure:", StringComparison.Ordinal))
    {
        Console.WriteLine("PASS: " + name);
    }
}

void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    Console.WriteLine("PASS: " + name);
}
