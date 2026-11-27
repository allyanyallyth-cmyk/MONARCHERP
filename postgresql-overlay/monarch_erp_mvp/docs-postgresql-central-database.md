# MONARCH ERP — PostgreSQL central database foundation
SQLite remains the local/offline provider. PostgreSQL is added as a separate .NET 8 Npgsql provider; no existing SQLite stores are replaced.
The initial migration creates company, branch, tenant-user, branch-grant and transactional outbox tables. Tenant transactions require CompanyId, BranchId and UserId, set transaction-local context, and verify active branch membership. Forced RLS policies provide database-level tenant filtering. The outbox uses a unique (company_id,event_id) key and ON CONFLICT DO NOTHING.
Configure MONARCH_POSTGRESQL_CONNECTION through the service secret store. Never commit credentials. MONARCH_POSTGRESQL_AUTO_MIGRATE=true enables startup migration for a controlled migration deployment only.
Production runtime credentials must be non-superuser, must not bypass RLS, and should not own schema objects. The app must derive tenant context from authenticated server-side identity, never directly from client-supplied tenant identifiers. GUC-based RLS is defense in depth and is not protection against a party holding the raw database credential.
CI uses a non-superuser runtime role and two tenants to verify RLS isolation, migration idempotency, branch membership, and duplicate outbox event rejection.
Not complete: PostgreSQL repositories for accounting, inventory, sales, purchasing, payroll, reporting and identity lifecycle; local-to-central sync/conflict resolution; production provisioning, TLS policy, backups, failover and load testing. This foundation does not make the ERP client-ready.

## JWT bearer validation foundation (2026-11-27)

The API can register JWT bearer validation only when all three deployment settings are supplied through the environment/secret store:

- `MONARCH_JWT_ISSUER`: exact trusted token issuer.
- `MONARCH_JWT_AUDIENCE`: exact API audience.
- `MONARCH_JWT_SIGNING_KEY_BASE64`: Base64 encoding of at least 32 cryptographically random bytes.

The API validates the signature, exact issuer, exact audience, token lifetime/expiration and HS256 algorithm, with a 30-second clock skew. Incomplete configuration or a key shorter than 256 bits stops startup; no secret is committed to source control. When all settings are absent, authentication middleware remains unregistered for local/demo development.

Generate a key in a secure provisioning environment (PowerShell/.NET 8 example):

```powershell
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
# Store $key in the deployment secret manager; do not commit it or print it in CI logs.
```

This is an authentication-validation foundation only. It does not issue tokens, implement refresh/revocation or signing-key rotation, establish an identity provider, or expose protected GL HTTP endpoints. Do not treat the API as production-authenticated until token issuance, key lifecycle, authorization policies and endpoint-level integration tests are delivered. The GL application service still enforces tenant and operation claims independently. Offline financial posting remains disabled until a durable, reconciled device outbox synchronizer is implemented.


## Authenticated General Ledger HTTP API

The API exposes the PostgreSQL General Ledger only when all three JWT settings are complete and `MONARCH_POSTGRESQL_CONNECTION` is configured. If JWT is configured without PostgreSQL, startup fails. When JWT is not configured, the authenticated financial route group is not mapped; this preserves local/demo operation without silently exposing financial operations.

All routes are under `/api/v1/general-ledger` and require a validated Bearer token. The application service independently checks the exact permission claim and derives company, branch and user IDs from the single authenticated identity. Tenant IDs are not accepted in route, query or request-body payloads. PostgreSQL signed tenant context and RLS remain the final data boundary.

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

This API surface is an initial GL slice; it does not yet provide token issuance, refresh/revocation, signing-key rotation, external OIDC federation, sales/POS, ZATCA, treasury or other ERP endpoints. Do not assign the GL permissions to ordinary users until role provisioning and approval workflows are deployed.
