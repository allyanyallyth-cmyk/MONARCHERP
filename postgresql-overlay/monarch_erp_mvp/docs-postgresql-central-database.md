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
