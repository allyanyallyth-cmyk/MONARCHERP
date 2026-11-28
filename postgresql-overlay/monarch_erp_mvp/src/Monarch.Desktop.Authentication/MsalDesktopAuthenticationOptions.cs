namespace Monarch.Desktop.Authentication;

/// <summary>
/// Public-client configuration for the MONARCH central API. No client secret is supported.
/// </summary>
public sealed class MsalDesktopAuthenticationOptions
{
    public MsalDesktopAuthenticationOptions(string authority, string clientId, IEnumerable<string> scopes)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
            authorityUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(authorityUri.UserInfo) ||
            !string.IsNullOrEmpty(authorityUri.Query) ||
            !string.IsNullOrEmpty(authorityUri.Fragment))
            throw new ArgumentException("OIDC authority must be an absolute HTTPS URI without user info, query, or fragment.", nameof(authority));

        if (!Guid.TryParse(clientId, out _))
            throw new ArgumentException("The desktop public-client application ID must be a GUID.", nameof(clientId));

        var normalizedScopes = scopes?.Select(s => s?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? throw new ArgumentNullException(nameof(scopes));

        if (normalizedScopes.Length == 0 ||
            normalizedScopes.Any(s => s.Equals("openid", StringComparison.OrdinalIgnoreCase) ||
                                      s.Equals("offline_access", StringComparison.OrdinalIgnoreCase) ||
                                      s.Equals("profile", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Configure at least one API scope; OIDC reserved scopes are added by MSAL and must not be supplied here.", nameof(scopes));

        Authority = authorityUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        ClientId = clientId;
        Scopes = Array.AsReadOnly(normalizedScopes);
    }

    public string Authority { get; }
    public string ClientId { get; }
    public IReadOnlyList<string> Scopes { get; }
}
