# MONARCH ERP — PostgreSQL central database foundation
SQLite remains the local/offline provider. PostgreSQL is added as a separate .NET 8 Npgsql provider; no existing SQLite stores are replaced.
Ordered PostgreSQL migrations create company, branch, tenant-user, branch-grant, transactional outbox, immutable General Ledger, OIDC identity mapping and RBAC tables. OIDC-backed GL operations resolve the internal user from the validated issuer/subject pair and verify the requested permission inside the same transaction. Forced RLS policies provide database-level tenant filtering. The outbox uses a unique (company_id,event_id) key and ON CONFLICT DO NOTHING.
Configure MONARCH_POSTGRESQL_CONNECTION through the service secret store. Never commit credentials. MONARCH_POSTGRESQL_AUTO_MIGRATE=true enables startup migration for a controlled migration deployment only.
Production runtime credentials must be non-superuser, must not bypass RLS, and should not own schema objects. The runtime login role must be granted membership in monarch_api_runtime to execute narrowly scoped OIDC resolution and authorization functions; this group role is NOLOGIN, NOINHERIT and NOBYPASSRLS. The application derives identity from validated OIDC iss and sub claims. Client-supplied company/branch headers are selections only and are checked against database membership. Tenant, internal-user and permission claims from the token are not trusted. GUC-based RLS is defense in depth and is not protection against a party holding the raw database credential.
CI uses a non-superuser runtime role and two tenants to verify RLS isolation, migration idempotency, branch membership, and duplicate outbox event rejection.
Not complete: production identity-provider/client setup, automated user provisioning and administration UI, full role approval workflow, PostgreSQL repositories for inventory, sales, purchasing, payroll and reporting, local-to-central sync/conflict resolution, production TLS acceptance, backups, failover and load testing. This foundation does not make the ERP client-ready.

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

When both authentication modes are absent, authentication middleware remains unregistered for local/demo development. This mode does not expose protected financial GL routes. For OIDC requests, the API accepts exactly one authenticated identity and extracts only its iss and sub claims. It does not accept monarch:user_id, monarch:company_id, monarch:branch_id or monarch:permission claims as authorization inputs.


### Controlled OIDC user and role provisioning

Provisioning is a privileged administrative operation performed by a database administrator or trusted identity-provisioning service, never by the ordinary ERP API runtime login. Use the exact issuer and subject values from the provider's validated access token. The external identity table enforces uniqueness for issuer, subject and company. Changes to identity mappings, roles, role permissions and user-role assignments are recorded in the authorization audit table. Database triggers reject UPDATE, DELETE and TRUNCATE on this audit table; privileged superusers can still disable triggers, so production audit exports must also be retained in independently controlled WORM storage.

The minimum provisioning sequence is:
1. Create or verify the internal company user and active branch grant.
2. Insert the exact OIDC issuer/subject mapping to that internal company/user.
3. Create a company-scoped role.
4. Grant only the required permission codes to that role.
5. Assign the role to the internal user with an approved administrator actor.
6. Verify that the runtime login is a member of monarch_api_runtime; do not grant it direct DML rights on identity or RBAC tables.

The six initial permission codes are gl.accounts.read, gl.accounts.manage, gl.journal.read, gl.journal.create, gl.journal.post and gl.report.trial_balance. No permissions are assigned automatically on first login.

## Authenticated General Ledger HTTP API

The API exposes the PostgreSQL General Ledger only when either complete OIDC configuration or complete legacy JWT configuration is present and `MONARCH_POSTGRESQL_CONNECTION` is configured. Authentication configured without PostgreSQL fails startup. When authentication is not configured, the authenticated financial route group is not mapped.

All routes are under /api/v1/general-ledger and require a validated Bearer access token. Every request must provide exactly one X-Monarch-Company-Id and one X-Monarch-Branch-Id header containing canonical GUIDs. These headers are untrusted selections, not authorization evidence. The API extracts iss and sub from the single authenticated identity; PostgreSQL resolves that pair to the internal user_id, validates active company/branch membership and evaluates the server-selected operation permission inside the same database transaction. The authorization function locks identity, membership and RBAC rows for the duration of the protected transaction, so concurrent revocation waits for that transaction to finish. The HMAC-signed tenant context includes the identity and permission, and RLS remains the final data boundary.

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

This API surface is an initial GL slice; it does not yet provide a deployed identity provider/client, automated user provisioning, a role-administration UI, provider-specific login UI, live token issuance acceptance tests, sales/POS, ZATCA, treasury or other ERP endpoints. The schema supports controlled provisioning by a privileged deployment/identity administrator. Do not grant GL permissions to ordinary users without an approved role assignment. Never grant the API login direct DML access to identity or RBAC tables. The runtime login must be explicitly granted membership in monarch_api_runtime. Provision the HMAC key MONARCH_POSTGRESQL_CONTEXT_HMAC_KEY in the server secret store, separately from client devices. Offline financial posting remains disabled until a durable, reconciled device outbox synchronizer is implemented.
