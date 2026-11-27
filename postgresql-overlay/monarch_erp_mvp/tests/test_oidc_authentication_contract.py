from pathlib import Path


SOURCE = (
    Path(__file__).resolve().parents[1]
    / "src"
    / "Monarch.Api"
    / "Security"
    / "JwtAuthenticationRegistration.cs"
)


def _source() -> str:
    assert SOURCE.is_file(), f"Authentication registration source not found: {SOURCE}"
    return SOURCE.read_text(encoding="utf-8")


def test_oidc_requires_complete_configuration_and_rejects_mixed_modes():
    source = _source()
    assert "oidcConfiguredCount != 2" in source
    assert "jwtConfiguredCount != 3" in source
    assert "oidcConfiguredCount != 0 && jwtConfiguredCount != 0" in source
    assert "Configure either OIDC authority/audience or legacy JWT issuer/audience/key" in source


def test_oidc_authority_must_be_https_and_have_no_url_credentials_or_parameters():
    source = _source()
    assert "Uri.TryCreate(authority, UriKind.Absolute" in source
    assert "authorityUri.Scheme != Uri.UriSchemeHttps" in source
    assert "authorityUri.UserInfo" in source
    assert "authorityUri.Query" in source
    assert "authorityUri.Fragment" in source


def test_oidc_bearer_validation_uses_discovery_and_fails_closed():
    source = _source()
    assert "options.Authority = authority!.TrimEnd('/')" in source
    assert "options.RequireHttpsMetadata = true" in source
    assert "RequireSignedTokens = true" in source
    assert "ValidateIssuerSigningKey = true" in source
    assert "ValidateIssuer = true" in source
    assert "ValidateAudience = true" in source
    assert "ValidAudience = oidcAudience" in source
    assert "ValidateLifetime = true" in source
    assert "options.SaveToken = false" in source
    assert "options.IncludeErrorDetails = false" in source
    assert "options.MapInboundClaims = false" in source


def test_oidc_audience_is_bounded_and_oidc_does_not_accept_legacy_shared_secret():
    source = _source()
    assert "oidcAudience!.Length > 256" in source
    assert "MONARCH_JWT_SIGNING_KEY_BASE64" in source
    assert "MONARCH_OIDC_AUDIENCE" in source
