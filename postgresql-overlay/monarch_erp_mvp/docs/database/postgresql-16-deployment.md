# MONARCH ERP — PostgreSQL 16 deployment and security boundary

## Authority and write ownership

- PostgreSQL 16 is the authoritative database for hosted, multi-company and multi-branch operation.
- SQLite is not a second cloud master. During this migration phase it remains the existing local/offline store only; a device must not write the same business transaction independently to SQLite and PostgreSQL.
- The SQLite-to-PostgreSQL offline queue, durable device identity, acknowledgement protocol, conflict policy and reconciliation are not yet production-complete. Until that work is shipped, disable offline financial posting on clients that cannot reach the authoritative API. Do not silently replay posted GL journals.
- Each module moves only after its PostgreSQL repository, migration, transaction boundary, RLS policy, idempotency key and SQLite/PostgreSQL parity suite are complete.

## Runtime PostgreSQL role

Use a dedicated non-owner, non-superuser role. It must not have BYPASSRLS, CREATEROLE, CREATEDB, schema CREATE, table ownership, or membership in `monarch_security_definer`.

Example DBA provisioning (choose a strong password through the secret manager; do not commit it):

```sql
CREATE ROLE monarch_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
GRANT CONNECT ON DATABASE monarch TO monarch_app;
GRANT USAGE ON SCHEMA public, monarch_security TO monarch_app;
GRANT EXECUTE ON FUNCTION monarch_security.set_tenant_context(uuid,uuid,uuid,bigint,text) TO monarch_app;
GRANT EXECUTE ON FUNCTION monarch_security.context_is_valid() TO monarch_app;

GRANT SELECT ON monarch_gl_accounts, monarch_gl_journals, monarch_gl_journal_lines TO monarch_app;
GRANT INSERT ON monarch_gl_accounts, monarch_gl_journals, monarch_gl_journal_lines,
  monarch_gl_journal_sequences, monarch_outbox TO monarch_app;
GRANT UPDATE ON monarch_gl_journals, monarch_gl_journal_lines, monarch_gl_journal_sequences TO monarch_app;
GRANT EXECUTE ON FUNCTION monarch_gl_next_journal_number(uuid,uuid,integer) TO monarch_app;
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

Set `MONARCH_POSTGRESQL_CONNECTION` in the server secret manager. Production connections require `SSL Mode=VerifyFull`; install the issuing CA in the host trust store and use the database certificate hostname. The provider enables Npgsql pooling (max 100, configurable in a future options revision). Tenant context is set with transaction-local settings and is cleared by PostgreSQL at commit/rollback. Do not use session-level `SET` for tenant identity.

## Migrations

Run migrations with a dedicated deployment identity, not the runtime role. Migrations are embedded, ordered by versioned resource name, SHA-256 recorded, serialized by an advisory transaction lock and each applied in its own transaction.

If an existing database has a legacy migration ledger row with a NULL checksum, deployment intentionally stops. Verify the live schema against that historical migration and perform an explicit, reviewed baseline operation; never auto-stamp an unknown schema or edit a migration that has already been applied.

## General Ledger V1 boundary

The current PostgreSQL GL foundation includes company-scoped chart of accounts, branch-scoped journals and lines, atomic journal numbering, integer minor-unit amounts, balanced journal validation at both repository and database trigger boundaries, immutable posted journals, exact reversal linkage, currency-filtered trial balance and an outbox event committed in the same transaction as posting.

It does not yet include full fiscal-period close controls, tax subledgers, ZATCA invoice/credit-note lifecycle, exchange-rate revaluation, consolidation, bank reconciliation, or the offline SQLite synchronization engine. Those are explicit remaining scope, not assumed complete.
