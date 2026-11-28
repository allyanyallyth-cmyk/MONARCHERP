# MONARCH central desktop — first GL read-only slice

**Status:** isolated .NET 8 WPF client builds and its configuration validation tests pass in GitHub Actions. This is not a production release and has not completed a live identity-provider login or a hosted API/Neon runtime smoke test.

## Scope delivered

- Separate Monarch.Desktop.Central WPF application targeting net8.0-windows.
- MSAL public-client authentication using the existing Monarch.Desktop.Authentication component.
- Authenticated tenant discovery for companies and branches.
- Read-only General Ledger account listing through the existing HttpGeneralLedgerStore.
- HTTPS-only API transport, bounded 30-second client timeout, and fail-closed error handling.
- No SQLite store is constructed or used by this application. It has no automatic local-mode fallback.
- The existing Monarch.Desktop net461 local application and its SQLite workflow are unchanged.
- No journal creation, journal posting, offline financial posting, or module cutover is exposed by this first slice.

## Required machine environment

Set these variables for the Windows user/process that launches the central client:

| Variable | Required value |
|---|---|
| MONARCH_CENTRAL_API_BASE_URL | Absolute HTTPS API origin/base URL ending in / |
| MONARCH_OIDC_AUTHORITY | HTTPS authority of the configured identity provider |
| MONARCH_OIDC_DESKTOP_CLIENT_ID | Registered desktop public-client application ID (GUID for the current MSAL options) |
| MONARCH_OIDC_API_SCOPES | One or more delegated API scopes, separated by spaces or semicolons |

The desktop is a public client. Do not create or embed a client secret. The current MSAL component uses the system browser and the http://localhost loopback redirect; register that exact redirect URI in the identity provider.

Do not put the PostgreSQL connection string or tenant-context HMAC key in the desktop. Those secrets remain server-side in the API host.

## Server prerequisites

The API must have valid MONARCH_OIDC_AUTHORITY and MONARCH_OIDC_AUDIENCE settings and a working PostgreSQL runtime connection. The identity provider must issue an access token for the API audience and configured scope. The authenticated identity must already be linked to an active MONARCH user, company, branch grant, and the relevant database permissions.

A READY Vercel deployment alone does not establish that these prerequisites are satisfied. The existing Preview deployment's SSO protection and the live Npgsql-to-Neon runtime connection must be tested through an authorized session.

## CI acceptance performed

Workflow: MONARCH central desktop slice

- Restored the central WPF project on windows-2022.
- Built the WPF client for .NET 8.
- Ran seven configuration security assertions: valid HTTPS config, scope parsing, missing config rejection, non-loopback HTTP rejection, insecure authority rejection, invalid client ID rejection, and reserved OIDC scope rejection.
- Published a framework-dependent win-x64 package.
- Verified required executable/runtime/authentication files and confirmed the PostgreSQL server provider assembly is not shipped with the desktop client.

These checks do not simulate an actual interactive MSAL login or a real hosted API call.

## Remaining acceptance gates

1. Register/configure the actual identity provider and desktop public client; validate redirect URI, issuer, audience, scopes, PKCE, token refresh, logout, and API authorization.
2. Run an authorized end-to-end sign-in against the Preview API and verify company/branch discovery plus account reads from Neon.
3. Add UI-level tests for expired tokens, revoked grants, 401/403, timeouts, cancellation, and rapid tenant switching.
4. Implement journal read/detail, draft creation, posting confirmation, and trial balance only after the account-list slice passes the live hosted test.
5. Keep offline financial posting disabled until durable outbox synchronization, idempotency, revocation, conflict handling, and reconciliation are implemented and tested.
6. Do not merge this slice into the existing production desktop or mark MONARCH customer-ready until installer, Windows 10/11 runtime, upgrade, backup/restore, and release gates pass.
