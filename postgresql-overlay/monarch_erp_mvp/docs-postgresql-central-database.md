# MONARCH ERP — PostgreSQL central database foundation
SQLite remains the local/offline provider. PostgreSQL is added as a separate .NET 8 Npgsql provider; no existing SQLite stores are replaced.
The initial migration creates company, branch, tenant-user, branch-grant and transactional outbox tables. Tenant transactions require CompanyId, BranchId and UserId, set transaction-local context, and verify active branch membership. Forced RLS policies provide database-level tenant filtering. The outbox uses a unique (company_id,event_id) key and ON CONFLICT DO NOTHING.
Configure MONARCH_POSTGRESQL_CONNECTION through the service secret store. Never commit credentials. MONARCH_POSTGRESQL_AUTO_MIGRATE=true enables startup migration for a controlled migration deployment only.
Production runtime credentials must be non-superuser, must not bypass RLS, and should not own schema objects. The app must derive tenant context from authenticated server-side identity, never directly from client-supplied tenant identifiers. GUC-based RLS is defense in depth and is not protection against a party holding the raw database credential.
CI uses a non-superuser runtime role and two tenants to verify RLS isolation, migration idempotency, branch membership, and duplicate outbox event rejection.
Not complete: PostgreSQL repositories for accounting, inventory, sales, purchasing, payroll, reporting and identity lifecycle; local-to-central sync/conflict resolution; production provisioning, TLS policy, backups, failover and load testing. This foundation does not make the ERP client-ready.

## JWT bearer validation and OIDC federation

The API supports two mutually exclusive bearer-validation modes. **OIDC discovery is the preferred production mode**; the symmetric HS256 mode below is retained for controlled compatibility deployments and CI only.

### Preferred OIDC mode

Configure both values through the deployment secret/configuration system:

- `MONARCH_OIDC_AUTHORITY`: trusted identity-provider authority, as an absolute HTTPS URL.
- `MONARCH_OIDC_AUDIENCE`: audience registered for the MONARCH ERP API.

The API uses the provider's OIDC discovery metadata and published signing keys. The JWT bearer middleware validates signed tokens, issuer, audience and lifetime; metadata transport requires HTTPS, token details are not saved, and raw authentication errors are not returned to callers. The middleware's configuration manager can refresh provider signing keys as metadata changes, avoiding a manually pinned signing key in the ERP API.

Authority and audience must both be present. HTTP authorities, URLs containing user information/query/fragment, partial configuration, or simultaneous OIDC and legacy JWT configuration fail startup. The provider must issue short-lived access tokens and protect its own refresh-token/session lifecycle. The API does not store provider passwords or implement a custom token issuer.

Example deployment settings (values are placeholders, not real provider endpoints):

```powershell
$env:MONARCH_OIDC_AUTHORITY = "https://identity.example.com"
$env:MONARCH_OIDC_AUDIENCE = "monarch-erp-api"
```

Before production, configure the provider's application/client, API audience, redirect URIs, MFA and account recovery policy. Do not accept ID tokens as API access tokens; the API audience must match an access token intended for this resource. The provider-specific setup and end-to-end sign-in flow still require deployment configuration and acceptance tests.

### Legacy HS256 compatibility mode

The API can also validate a locally provisioned HS256 signing key when all three settings are supplied:

- `MONARCH_JWT_ISSUER`: exact trusted token issuer.
- `MONARCH_JWT_AUDIENCE`: exact API audience.
- `MONARCH_JWT_SIGNING_KEY_BASE64`: Base64 encoding of at least 32 cryptographically random bytes.

The API validates signature, exact issuer/audience, expiry/lifetime and HS256 algorithm with a 30-second clock skew. Incomplete configuration or a key shorter than 256 bits stops startup. A shared HMAC key gives every holder signing authority, so this mode is not the recommended multi-service production identity architecture.

Generate a key only for a controlled compatibility deployment (PowerShell/.NET 8):

```powershell
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
# Store $key in the deployment secret manager; never commit it or print it in CI logs.
```

When both authentication modes are absent, authentication middleware remains unregistered for local/demo development. This mode does not expose protected financial GL routes. The GL application service independently enforces operation permissions and derives tenant claims from exactly one authenticated identity.

## Authenticated General Ledger HTTP API

The API exposes the PostgreSQL General Ledger only when either complete OIDC configuration or complete legacy JWT configuration is present and `MONARCH_POSTGRESQL_CONNECTION` is configured. Authentication configured without PostgreSQL fails startup. When authentication is not configured, the authenticated financial route group is not mapped.

All routes are under `/api/v1/general-ledger` and require a validated Bearer access token. The application service independently checks the exact permission claim and derives company, branch and user IDs from the single authenticated identity. Tenant IDs are not accepted in route, query or request-body payloads. PostgreSQL signed tenant context and RLS remain the final data boundary.

| Method | Route | Required permission |
|---|---|---|
| GET | `/accounts?includeInactive=false` | `gl.accounts.read` |
| POST | `/accounts` | `gl.accounts.manage` |
| GET | `/journals/{journalId}` | `gl.journal.read` |
| POST | `/journals` | `gl.journal.create` |
| POST | `/journals/{journalId}/post` | `gl.journal.post` |
| GET | `/reports/trial-balance?asOfDate=YYYY-MM-DD&currencyCode=SAR` | `gl.report.trial_balance` |

Draft journal line amounts are integer minor units, with exactly one positive debit or credit per line. The PostgreSQL store validates balancing, account activity, branch/company scope, reversal exactness, immutable posting and transactional outbox behavior. The API does not expose a caller-controlled tenant selector.

Permission failures return HTTP 403, malformed domain input returns HTTP 400, and conflicting domain state returns HTTP 409. A missing journal returns HTTP 404. Infrastructure failures are not converted into successful responses.

This API surface is an initial GL slice; it does not yet provide a deployed identity provider/client, user provisioning and role mapping, provider-specific login UI, token issuance acceptance tests, sales/POS, ZATCA, treasury or other ERP endpoints. OIDC validation is implemented but not proof of production identity lifecycle readiness. Do not assign GL permissions to ordinary users until role provisioning and approval workflows are deployed. Offline financial posting remains disabled until a durable, reconciled device outbox synchronizer is implemented.
