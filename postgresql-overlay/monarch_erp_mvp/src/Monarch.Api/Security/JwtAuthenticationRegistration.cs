using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Monarch.Api.Security;

/// <summary>
/// Registers fail-closed JWT bearer validation from deployment environment settings.
/// The signing secret must be a Base64-encoded random key of at least 256 bits.
/// No authentication scheme is registered when all settings are absent (local/demo mode).
/// </summary>
public static class JwtAuthenticationRegistration
{
    public const string IssuerEnvironmentVariable = "MONARCH_JWT_ISSUER";
    public const string AudienceEnvironmentVariable = "MONARCH_JWT_AUDIENCE";
    public const string SigningKeyEnvironmentVariable = "MONARCH_JWT_SIGNING_KEY_BASE64";

    public static bool AddFromConfiguration(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var issuer = configuration[IssuerEnvironmentVariable];
        var audience = configuration[AudienceEnvironmentVariable];
        var encodedKey = configuration[SigningKeyEnvironmentVariable];
        var configuredCount = new[] { issuer, audience, encodedKey }.Count(value => !string.IsNullOrWhiteSpace(value));

        if (configuredCount == 0)
            return false;

        if (configuredCount != 3)
            throw new InvalidOperationException(
                "JWT configuration is incomplete. Set issuer, audience and a Base64 signing key together.");

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