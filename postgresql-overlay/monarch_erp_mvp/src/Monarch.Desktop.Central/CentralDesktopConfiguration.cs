using Monarch.Desktop.Authentication;

namespace Monarch.Desktop.Central;

/// <summary>Configuration for the isolated central client. No secrets or credentials are accepted.</summary>
public sealed class CentralDesktopConfiguration
{
    public const string ApiBaseUrlVariable = "MONARCH_CENTRAL_API_BASE_URL";
    public const string AuthorityVariable = "MONARCH_OIDC_AUTHORITY";
    public const string ClientIdVariable = "MONARCH_OIDC_DESKTOP_CLIENT_ID";
    public const string ApiScopesVariable = "MONARCH_OIDC_API_SCOPES";

    private CentralDesktopConfiguration(Uri apiBaseAddress, MsalDesktopAuthenticationOptions authentication)
    {
        ApiBaseAddress = apiBaseAddress;
        Authentication = authentication;
    }

    public Uri ApiBaseAddress { get; }
    public MsalDesktopAuthenticationOptions Authentication { get; }

    public static CentralDesktopConfiguration LoadFromEnvironment() =>
        Load(Environment.GetEnvironmentVariable);

    public static CentralDesktopConfiguration Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var apiText = Required(read, ApiBaseUrlVariable);
        if (!Uri.TryCreate(apiText, UriKind.Absolute, out var apiUri) ||
            (apiUri.Scheme != Uri.UriSchemeHttps && !(apiUri.Scheme == Uri.UriSchemeHttp && apiUri.IsLoopback)) ||
            apiUri.UserInfo.Length != 0 || apiUri.Query.Length != 0 || apiUri.Fragment.Length != 0 ||
            !apiUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException($"{ApiBaseUrlVariable} must be an absolute HTTPS API base URL ending in '/'. HTTP is allowed only for loopback development.");

        var authority = Required(read, AuthorityVariable);
        var clientId = Required(read, ClientIdVariable);
        var scopesText = Required(read, ApiScopesVariable);
        var scopes = scopesText.Split(new[] { ' ', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            var authentication = new MsalDesktopAuthenticationOptions(authority, clientId, scopes);
            return new CentralDesktopConfiguration(apiUri, authentication);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The central desktop OIDC configuration is invalid: " + exception.Message, exception);
        }
    }

    private static string Required(Func<string, string?> read, string name)
    {
        var value = read(name)?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required configuration variable {name} is missing.")
            : value;
    }
}
