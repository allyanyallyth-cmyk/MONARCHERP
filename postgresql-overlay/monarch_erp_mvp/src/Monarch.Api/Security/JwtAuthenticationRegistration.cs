using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Monarch.Api.Security;

/// <summary>
/// Registers fail-closed JWT bearer validation. OIDC discovery is the preferred
/// production mode because signing keys are obtained and refreshed from trusted metadata.
/// The shared-secret HS256 mode remains for controlled compatibility deployments.
/// No authentication scheme is registered when both modes are absent (local/demo mode).
/// </summary>
public static class JwtAuthenticationRegistration
{
    public const string IssuerEnvironmentVariable = "MONARCH_JWT_ISSUER";
    public const string AudienceEnvironmentVariable = "MONARCH_JWT_AUDIENCE";
    public const string SigningKeyEnvironmentVariable = "MONARCH_JWT_SIGNING_KEY_BASE64";
    public const string OidcAuthorityEnvironmentVariable = "MONARCH_OIDC_AUTHORITY";
    public const string OidcAudienceEnvironmentVariable = "MONARCH_OIDC_AUDIENCE";

    public static bool AddFromConfiguration(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var authority = configuration[OidcAuthorityEnvironmentVariable];
        var oidcAudience = configuration[OidcAudienceEnvironmentVariable];
        var oidcConfiguredCount = new[] { authority, oidcAudience }
            .Count(value => !string.IsNullOrWhiteSpace(value));

        var issuer = configuration[IssuerEnvironmentVariable];
        var audience = configuration[AudienceEnvironmentVariable];
        var encodedKey = configuration[SigningKeyEnvironmentVariable];
        var jwtConfiguredCount = new[] { issuer, audience, encodedKey }
            .Count(value => !string.IsNullOrWhiteSpace(value));

        if (oidcConfiguredCount == 0 && jwtConfiguredCount == 0)
            return false;

        if (oidcConfiguredCount != 0 && jwtConfiguredCount != 0)
            throw new InvalidOperationException(
                "Configure either OIDC authority/audience or legacy JWT issuer/audience/key, not both.");

        if (oidcConfiguredCount != 0)
        {
            if (oidcConfiguredCount != 2)
                throw new InvalidOperationException(
                    "OIDC configuration is incomplete. Set authority and audience together.");

            if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
                authorityUri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(authorityUri.UserInfo) ||
                !string.IsNullOrEmpty(authorityUri.Query) ||
                !string.IsNullOrEmpty(authorityUri.Fragment))
                throw new InvalidOperationException(
                    "OIDC authority must be an absolute HTTPS URL without user info, query or fragment.");

            if (oidcAudience!.Length > 256)
                throw new InvalidOperationException("OIDC audience must not exceed 256 characters.");

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(options =>
            {
                options.Authority = authority!.TrimEnd('/');
                options.Audience = oidcAudience;
                options.RequireHttpsMetadata = true;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidAudience = oidcAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
            services.AddAuthorization();
            return true;
        }

        if (jwtConfiguredCount != 3)
            throw new InvalidOperationException(
                "Legacy JWT configuration is incomplete. Set issuer, audience and a Base64 signing key together.");

        if (issuer!.Length > 256 || audience!.Length > 256)
            throw new InvalidOperationException("JWT issuer and audience must not exceed 256 characters.");

        byte[] signingKey;
        try
        {
            signingKey = Convert.FromBase64String(encodedKey!);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("JWT signing key must be valid Base64.", exception);
        }

        if (signingKey.Length < 32)
            throw new InvalidOperationException("JWT signing key must contain at least 32 random bytes.");

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(options =>
        {
            options.SaveToken = false;
            options.IncludeErrorDetails = false;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }
            };
        });
        services.AddAuthorization();
        return true;
    }
}