using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Monarch.Api.Security;

public sealed record OidcIdentityOwnershipProof(string Issuer, string Subject, string Nonce);

/// <summary>
/// Validates an OIDC ID token independently of the API access token.
/// Discovery metadata and signing keys come from the already HTTPS-pinned JwtBearer authority.
/// The ID token must be fresh and contain the exact invitation nonce.
/// </summary>
public sealed class OidcEnrollmentProofValidator
{
    public const string ClientIdEnvironmentVariable = "MONARCH_OIDC_ENROLLMENT_CLIENT_ID";
    private static readonly TimeSpan MaximumAuthenticationAge = TimeSpan.FromMinutes(5);
    private readonly IOptionsMonitor<JwtBearerOptions> _bearerOptions;
    private readonly string _clientId;

    public OidcEnrollmentProofValidator(
        IOptionsMonitor<JwtBearerOptions> bearerOptions,
        IConfiguration configuration)
    {
        _bearerOptions = bearerOptions;
        _clientId = configuration[ClientIdEnvironmentVariable]
            ?? throw new InvalidOperationException($"{ClientIdEnvironmentVariable} is required for identity enrollment.");
        if (string.IsNullOrWhiteSpace(_clientId) || _clientId.Length > 256)
            throw new InvalidOperationException("OIDC enrollment client ID must contain 1-256 characters.");
    }

    public async Task<OidcIdentityOwnershipProof> ValidateAsync(
        string idToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 32_768)
            throw new SecurityTokenException("A bounded OIDC ID token is required.");

        var options = _bearerOptions.Get(JwtBearerDefaults.AuthenticationScheme);
        var manager = options.ConfigurationManager
            ?? throw new InvalidOperationException("OIDC discovery configuration is unavailable.");
        var metadata = await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(metadata.Issuer) || !Uri.TryCreate(metadata.Issuer, UriKind.Absolute, out var issuerUri)
            || issuerUri.Scheme != Uri.UriSchemeHttps || metadata.SigningKeys.Count == 0)
            throw new SecurityTokenException("Trusted OIDC issuer metadata or signing keys are unavailable.");

        var validation = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidateIssuer = true,
            ValidIssuer = metadata.Issuer,
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256 }
        };

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(idToken, validation).ConfigureAwait(false);
        if (!result.IsValid || result.ClaimsIdentity is null)
            throw new SecurityTokenException("OIDC ID token signature, issuer, audience or lifetime validation failed.", result.Exception);

        var jwt = new JsonWebToken(idToken);
        var issuer = Single(jwt, "iss", 512);
        var subject = Single(jwt, "sub", 512);
        var nonce = Single(jwt, "nonce", 256);
        if (!string.Equals(issuer, metadata.Issuer, StringComparison.Ordinal))
            throw new SecurityTokenException("OIDC issuer does not match trusted discovery metadata.");

        var audiences = jwt.Audiences.ToArray();
        if (audiences.Length > 1 && !string.Equals(Single(jwt, "azp", 256), _clientId, StringComparison.Ordinal))
            throw new SecurityTokenException("OIDC authorized-party claim is invalid for a multi-audience ID token.");

        var now = DateTimeOffset.UtcNow;
        var issuedAt = UnixTime(Single(jwt, "iat", 32), "iat");
        var authenticatedAt = UnixTime(Single(jwt, "auth_time", 32), "auth_time");
        if (issuedAt > now.AddSeconds(30) || now - issuedAt > MaximumAuthenticationAge ||
            authenticatedAt > now.AddSeconds(30) || now - authenticatedAt > MaximumAuthenticationAge)
            throw new SecurityTokenException("OIDC authentication proof is not sufficiently recent.");

        return new OidcIdentityOwnershipProof(issuer, subject, nonce);
    }

    private static string Single(JsonWebToken token, string name, int maximumLength)
    {
        var values = token.Claims.Where(claim => claim.Type == name).Select(claim => claim.Value).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0].Length > maximumLength)
            throw new SecurityTokenException($"OIDC ID token must contain exactly one valid '{name}' claim.");
        return values[0];
    }

    private static DateTimeOffset UnixTime(string value, string claimName)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            throw new SecurityTokenException($"OIDC '{claimName}' claim is not a valid NumericDate.");
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new SecurityTokenException($"OIDC '{claimName}' claim is outside the supported time range.", exception);
        }
    }
}
