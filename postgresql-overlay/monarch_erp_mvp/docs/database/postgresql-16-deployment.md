# MONARCH ERP — PostgreSQL 16 deployment and security boundary

## Authority and write ownership

- PostgreSQL 16 is the authoritative database for hosted, multi-company and multi-branch operation.
- SQLite is not a second cloud master. During this migration phase it remains the existing local/offline store only; a device must not write the same business transaction independently to SQLite and PostgreSQL.
- The SQLite-to-PostgreSQL offline queue, durable device identity, acknowledgement protocol, conflict policy and reconciliation are not yet production-complete. Until that work is shipped, disable offline financial posting on clients that cannot reach the authoritative API. Do not silently replay posted GL journals.
- Each module moves only after its PostgreSQL repository, migration, transaction boundary, RLS policy, idempotency key and SQLite/PostgreSQL parity suite are complete.

## Neon project deployment

The dedicated MONARCH ERP Neon project is provisioned separately from the ALALYANI AI database:

- Neon project: `monarch-erp-postgresql`
- Project ID: `wild-queen-83537574`
- Region: `aws-eu-central-1`
- PostgreSQL: 16
- Default branch: `main`
- Database: `monarch_erp`
- Owner role: `monarch_owner`

The versioned schema migrations `001_central_tenancy` through `012_minimal_gl_runtime_privileges` are applied to the Neon `main` branch. The migration ledger stores SHA-256 checksums. The schema was first rehearsed on an isolated Neon validation branch. No company, user, OIDC identity, financial, or demo business records have been seeded.

Use the pooled Neon connection string for API traffic and the direct (non-pooled) connection string for schema migrations and backup/restore. Store the connection string only in the API host's secret manager as `MONARCH_POSTGRESQL_CONNECTION`. Store a separate random 256-bit hex key as `MONARCH_POSTGRESQL_CONTEXT_HMAC_KEY`, and provision that exact key into `monarch_security.context_secrets` using the DBA procedure below. Never put either value in source control, workflow logs, or a client-side WPF configuration.

The Neon project now has a dedicated `monarch_app` login role. It is `INHERIT`, `NOSUPERUSER`, `NOCREATEDB`, `NOCREATEROLE`, and `NOBYPASSRLS`, and is a member of `monarch_api_runtime`; it has no direct access to tenant membership or RBAC tables. Its random password is retained by Neon and is not committed to the repository. The pooled connection string for this role still must be copied from the Neon Console and placed in the API host secret manager as `MONARCH_POSTGRESQL_CONNECTION`. The context HMAC secret has not yet been provisioned, and no API host secret store is connected from this workspace. Protected API routes must remain disabled until both secrets are configured and the API health/authorization flow is tested.

## Runtime PostgreSQL role

Use a dedicated non-owner, non-superuser role. It must not have BYPASSRLS, CREATEROLE, CREATEDB, schema CREATE, table ownership, or membership in `monarch_security_definer`.

Example DBA provisioning (choose a strong password through the secret manager; do not commit it):

```sql
CREATE ROLE monarch_app LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
GRANT CONNECT ON DATABASE monarch_erp TO monarch_app;
GRANT monarch_api_runtime TO monarch_app;
```

Do not grant INSERT/UPDATE/DELETE/TRUNCATE on tenant membership or membership audit tables to the runtime role. Membership lifecycle functions are privileged operations; grant their EXECUTE permission only to a separately authenticated membership-administration role after authorization review.

The migration creates `monarch_security_definer` as a NOLOGIN, NOINHERIT, BYPASSRLS role. It owns only the narrowly scoped SECURITY DEFINER functions that manage membership and validate signed tenant context. The deployment identity must be privileged enough to create this role and transfer function ownership. The runtime role must never be granted membership in it.

## Signed tenant context

Generate one independent 256-bit random key and store it in the server secret manager as hex:

```sh
openssl rand -hex 32
```

Provision the same secret into the database through the DBA secret-injection process:

```sql
INSERT INTO monarch_security.context_secrets(secret_id, secret)
VALUES (1, decode('<INJECT_FROM_SECRET_MANAGER>', 'hex'));
```

Set `MONARCH_POSTGRESQL_CONTEXT_HMAC_KEY` to that exact hex value for the API process. The application signs company/branch/user plus a short issue timestamp; PostgreSQL verifies the HMAC before setting transaction-local context. RLS checks that signature on every scoped query. Arbitrary custom GUC values alone are not authorization.

Rotate the key only with a coordinated deployment procedure that updates the DB secret and application secret together. Never log the key or signatures.

## TLS and pooling

Set `MONARCH_POSTGRESQL_CONNECTION` in the server secret manager. Production connections require `SSL Mode=VerifyFull`; install the issuing CA in the host trust store and use the database certificate hostname. The provider enables Npgsql pooling and preserves `Maximum Pool Size` / `Minimum Pool Size` values supplied in the connection string (Npgsql defaults apply when omitted); `KeepAlive` defaults to 30 seconds. Tenant context is set with transaction-local settings and is cleared by PostgreSQL at commit/rollback. Do not use session-level `SET` for tenant identity.

## Migrations

Run migrations with a dedicated deployment identity, not the runtime role. Migrations are embedded, ordered by versioned resource name, SHA-256 recorded, serialized by an advisory transaction lock and each applied in its own transaction.

If an existing database has a legacy migration ledger row with a NULL checksum, deployment intentionally stops. Verify the live schema against that historical migration and perform an explicit, reviewed baseline operation; never auto-stamp an unknown schema or edit a migration that has already been applied.

## General Ledger V1 boundary

The current PostgreSQL GL foundation includes company-scoped chart of accounts, branch-scoped journals and lines, atomic journal numbering, integer minor-unit amounts, balanced journal validation at both repository and database trigger boundaries, immutable posted journals, exact reversal linkage, currency-filtered trial balance and an outbox event committed in the same transaction as posting.

It does not yet include full fiscal-period close controls, tax subledgers, ZATCA invoice/credit-note lifecycle, exchange-rate revaluation, consolidation, bank reconciliation, or the offline SQLite synchronization engine. Those are explicit remaining scope, not assumed complete.


## OIDC identity enrollment safety gate

The current provisioning workflow supports independently approved identity and role administration, but a two-person approval is not proof that the target user controls an OIDC identity. Therefore migration 008 installs a database trigger that:

- rejects every new `LINK_IDENTITY` provisioning request;
- rejects approval of any legacy pending `LINK_IDENTITY` request;
- leaves role assignment/revocation and identity revocation workflows unchanged.

Do not remove this guard or manually insert into `monarch_security.external_identities` for end-user onboarding. Identity enrollment remains unavailable until a dedicated flow validates a fresh OIDC authentication for the target user (issuer, subject, audience, nonce, signature and lifetime), binds a short-lived single-use claim to the exact company/user, stores only a hash of the claim secret, and then requires independent approval. Claim redemption and identity-link approval must be atomic, replay-resistant and audited. Raw claim secrets must never enter application logs.

This is a deliberate fail-closed restriction, not a completed identity-enrollment feature.

## OIDC identity enrollment (migration 009)

The previous fail-closed LINK_IDENTITY request path remains disabled. The replacement enrollment flow is separate and requires cryptographic proof plus a different approver:

1. Configure MONARCH_OIDC_AUTHORITY and MONARCH_OIDC_AUDIENCE for API access-token validation, and configure MONARCH_OIDC_ENROLLMENT_CLIENT_ID to the exact OIDC client ID used for the enrollment ID token. If the enrollment client ID is absent, enrollment routes are not registered.
2. An already-linked tenant administrator with database permission security.identity.invite creates an invitation for a different active member in the selected branch. The API returns a 256-bit random invitation secret and a 256-bit random OIDC nonce exactly once. Deliver these over a trusted channel; never log them.
3. The target user must authenticate through the configured OIDC provider using the returned nonce and a maximum authentication age of 300 seconds (max_age=300). The client sends the resulting ID token and invitation secret to POST /api/v1/security/identity-enrollments/redeem over HTTPS.
4. The API independently validates the ID token signature against current HTTPS discovery signing keys, exact issuer, enrollment-client audience, lifetime, algorithm (RS256 or ES256), unique issuer/subject/nonce claims, and recent iat plus auth_time. PostgreSQL atomically verifies the invitation hash, expected issuer, nonce hash, expiry and target membership, then consumes the invitation into PROVEN.
5. A different already-linked administrator with security.identity.enrollment.approve reviews the proven enrollment and approves it. PostgreSQL re-locks membership and inserts the identity mapping plus approval state in one transaction. The inviter and target cannot approve.
6. A claim can be redeemed only once. The database stores SHA-256 hashes of the invitation secret and nonce, never their plaintext values. Proven claims must be approved within 24 hours.

The API access-token audience and the ID-token enrollment client audience are deliberately separate. The ID token is not accepted as an API bearer access token. The redemption route is anonymous at the API layer because possession of the invitation alone is insufficient: independent ID-token validation and nonce-bound database redemption are mandatory. All invitation creation, listing and approval routes require a validated API bearer token and database-backed RBAC. Tenant company and branch headers are selections only; PostgreSQL verifies membership and permission.

The client integration must actually send the exact returned nonce in the OIDC authorization request and request max_age=300; merely forwarding an arbitrary previously issued ID token is not an acceptable proof. The provider must issue signed ID tokens using RS256 or ES256 and include auth_time. Invitation delivery, client-side OIDC authorization initiation, production reverse-proxy HTTPS enforcement, rate limiting and real-provider end-to-end acceptance remain deployment/client integration tasks. Do not remove migration 008's legacy LINK_IDENTITY guard.
### Verified recipient requirement

Cryptographic validation proves that the redeemer controls the OIDC subject named in the ID token; it does not, by itself, prove that this subject belongs to the human represented by the target MONARCH user record. Therefore the invitation secret must be delivered to a separately verified contact channel already known to belong to that target (or handed over in person after identity verification). The approving administrator must independently confirm the subject-to-person association against the organization's trusted onboarding records before approval. Do not send the invitation secret and nonce through an unverified contact supplied by the requester. Do not log request or response bodies containing invitation secrets or ID tokens. Until a verified-contact delivery integration and that human identity-assurance control are operational, treat this as a controlled administrator-assisted enrollment workflow, not unattended self-service onboarding.