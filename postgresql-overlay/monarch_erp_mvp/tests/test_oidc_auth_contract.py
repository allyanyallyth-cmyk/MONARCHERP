from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AUTH = (ROOT / "src/Monarch.Api/Security/JwtAuthenticationRegistration.cs").read_text(encoding="utf-8")
DOCS = (ROOT / "docs-postgresql-central-database.md").read_text(encoding="utf-8")


def test_oidc_mode_uses_discovery_and_https_metadata():
    assert 'public const string OidcAuthorityEnvironmentVariable = "MONARCH_OIDC_AUTHORITY"' in AUTH
    assert 'public const string OidcAudienceEnvironmentVariable = "MONARCH_OIDC_AUDIENCE"' in AUTH
    assert "options.Authority = authority!.TrimEnd('/')" in AUTH
    assert "options.Audience = oidcAudience" in AUTH
    assert "options.RequireHttpsMetadata = true" in AUTH
    assert "ValidateIssuerSigningKey = true" in AUTH
    assert "ValidateIssuer = true" in AUTH
    assert "ValidateAudience = true" in AUTH
    assert "ValidateLifetime = true" in AUTH


def test_oidc_configuration_fails_closed_for_partial_or_unsafe_authorities():
    assert "oidcConfiguredCount != 2" in AUTH
    assert "authorityUri.Scheme != Uri.UriSchemeHttps" in AUTH
    assert "!string.IsNullOrEmpty(authorityUri.UserInfo)" in AUTH
    assert "!string.IsNullOrEmpty(authorityUri.Query)" in AUTH
    assert "!string.IsNullOrEmpty(authorityUri.Fragment)" in AUTH


def test_oidc_and_legacy_symmetric_jwt_modes_are_mutually_exclusive():
    assert "oidcConfiguredCount != 0 && jwtConfiguredCount != 0" in AUTH
    assert "Configure either OIDC authority/audience or legacy JWT issuer/audience/key, not both." in AUTH
    assert "ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }" in AUTH


def test_deployment_guide_marks_oidc_as_preferred_and_documents_token_boundaries():
    assert "OIDC discovery is the preferred production mode" in DOCS
    assert "Do not accept ID tokens as API access tokens" in DOCS
    assert "The API does not store provider passwords or implement a custom token issuer." in DOCS
    assert "automated user provisioning" in DOCS
    assert "X-Monarch-Company-Id" in DOCS
    assert "X-Monarch-Branch-Id" in DOCS
