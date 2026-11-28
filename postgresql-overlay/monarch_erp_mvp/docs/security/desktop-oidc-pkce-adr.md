# Desktop OIDC Authorization Code + PKCE — architecture decision

Date: 2026-11-28
Status: **Accepted design; implementation not yet enabled**
Applies to: MONARCH ERP Windows WPF public desktop client and PostgreSQL-backed API.

## Decision

The central-mode desktop client will authenticate as a **public OAuth 2.0 / OpenID Connect client** using Authorization Code Flow with PKCE (S256) through the user's system browser. It will never contain a client secret.

A maintained OIDC client implementation compatible with the shipping desktop target must perform discovery, authorization response validation, ID-token validation, nonce validation, token exchange, and provider metadata/key rotation. Do not hand-roll JWT signature validation or treat a decoded JWT payload as authenticated. Before selecting or adding a package, verify its supported target frameworks, transitive dependencies, maintenance status, and compatibility with both the legacy WPF target and the .NET 8 compatibility build. If the required secure library cannot support the shipping target, the desktop target must be upgraded or the client must be isolated behind a separately tested authentication component; do not weaken validation to preserve net461.

## Protocol requirements

1. Authority is configured by deployment, must be an absolute HTTPS URI, and must not contain user information, query, or fragment.
2. Use OIDC discovery over HTTPS and validate the metadata issuer exactly against the configured authority. Apply the provider's signing keys and refresh metadata safely on key rollover.
3. Generate cryptographically random state, nonce, and a PKCE verifier for every interactive authorization attempt. Derive the challenge with SHA-256 (S256). Reject plain.
4. Open the authorization URI in the system browser. Do not embed a web view that can inspect user credentials.
5. Use a short-lived loopback redirect bound only to 127.0.0.1 or ::1, on an ephemeral port. Accept one callback only, enforce a strict timeout and request-size bound, and reject remote peers, duplicate parameters, missing/ambiguous state, provider errors, and callbacks for another authorization attempt. Do not expose a listener on 0.0.0.0 or a LAN interface.
6. Exchange the authorization code only with the discovered token endpoint over validated TLS, sending the exact redirect URI and the original PKCE verifier. Never send a client secret.
7. Validate the returned ID token using the selected OIDC library: signature, issuer, audience, azp where applicable, lifetime, nonce, and required authentication context. The API independently validates the access token; the desktop must not treat a successful local UI state as authorization.
8. Keep access tokens in process memory only. Do not write them to logs, telemetry, crash dumps, URLs, or ordinary settings. Refresh tokens, if later enabled, require an OS-protected credential store, rotation/reuse detection, revocation handling, and explicit logout semantics. No refresh-token persistence is permitted until those controls are implemented and tested.
9. On HTTP 401, require re-authentication or a separately implemented secure refresh flow. On 403, show an authorization denial; do not retry with another identity or local SQLite. Never automatically retry financial writes after a timeout or unknown outcome.
10. Clear in-memory credentials and local authorization state on logout. Provider logout/revocation is best effort only when supported and must not be represented as successful if the provider did not confirm it.

## API and tenant-context requirements

- The desktop sends only the bearer access token and selected X-Monarch-Company-Id / X-Monarch-Branch-Id values.
- It must not send or trust a user ID, permission list, role, or branch grant as an authorization assertion.
- Tenant discovery is advisory UI data. PostgreSQL revalidates the selected identity, active membership, branch grant, and operation-specific permission inside every protected business transaction.
- TLS certificate validation remains enabled. No permissive certificate callback, trust-all handler, or plaintext remote endpoint is allowed.
- Use bounded connect/request timeouts, cancellation, safe error mapping, and non-secret correlation IDs.

## Required automated acceptance tests

- State mismatch, missing state, duplicate state, provider error, callback replay, wrong callback peer, timeout, malformed callback, and oversized request all fail closed.
- PKCE challenge is S256 and token exchange uses the exact verifier and redirect URI.
- Wrong issuer, audience, azp, signature, nonce, expired token, not-yet-valid token, stale authentication context, and unknown signing key fail closed; key rollover refreshes metadata once without accepting an unvalidated token.
- No client secret is present in configuration, binary, logs, or requests.
- Access/refresh tokens never appear in application logs or persistent plaintext settings.
- Cancellation and disposal close the loopback listener and do not leave a callback port open.
- 401/403/429/5xx and network timeout are mapped safely. A timed-out POST is not automatically replayed.
- Real-provider end-to-end test covers sign-in, company discovery, branch discovery, GL read, GL draft, post authorization, logout, and revoked membership/role denial.
- Windows 10 and 11 runtime tests verify system-browser handoff, loopback callback, DPI/session behavior, and installer configuration.

## Rollout gates

1. Implement and unit-test the authentication component independently.
2. Run provider-backed integration tests against a configured test OIDC tenant.
3. Wire an access-token provider into the existing typed HTTP GL adapter.
4. Wire tenant discovery and GL-only central-mode screens without initializing SQLite.
5. Keep MONARCH_DESKTOP_MODE=central fail-closed until all previous gates and Windows acceptance pass.
6. Only then enable central-mode GL pilot; keep other modules disabled until their server/API and transaction tests pass.

## Explicit non-goals

This decision does not implement OIDC, issue or refresh tokens, connect the WPF screens to PostgreSQL, migrate SQLite data, enable offline financial posting, or declare the product customer-ready. It is the security contract against which implementation and review must be measured.