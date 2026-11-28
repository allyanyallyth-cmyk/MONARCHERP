# WPF to PostgreSQL cutover — repository assessment

Assessment date: 2026-11-28
Branch: `feature/postgresql-central-provider-2026-11-27`
Baseline: OIDC tenant-discovery foundation added after successful Run #419
Status: **assessment only; no desktop cutover is claimed**

## 1. Verified current desktop startup path

The cumulative source archive produced by Windows CI was inspected. The production WPF project currently targets `.NET Framework 4.6.1` (`net461`).

The current desktop startup path still creates `%LocalAppData%/MONARCH/Data/monarch-ledger.db`, initializes `SqliteLedgerStore`, `IdentityStore`, `CompanyDirectoryStore`, and `TenantLedgerStore`, authenticates through `SecurityGateWindow`, selects a company/branch through `TenantContextWindow`, and opens `TenantAccountingSession`.

Consequently, desktop login, tenant selection, and accounting remain SQLite-backed. This is not a PostgreSQL central-mode client.

## 2. PostgreSQL GL API and HTTP adapter

The API exposes seven protected GL operations:

- `GET /api/v1/general-ledger/accounts`
- `POST /api/v1/general-ledger/accounts`
- `GET /api/v1/general-ledger/journals?limit=250` (limit 1–1000; requires `gl.journal.read`)
- `GET /api/v1/general-ledger/journals/{journalId}`
- `POST /api/v1/general-ledger/journals`
- `POST /api/v1/general-ledger/journals/{journalId}/post`
- `GET /api/v1/general-ledger/reports/trial-balance`

The API requires a validated OIDC identity and selected company/branch IDs. PostgreSQL resolves the internal user and checks current database-backed permission within the protected business transaction.

The Core store abstraction and HTTP adapter exist, and their CI contract/smoke tests pass. **The adapter is not wired into the WPF screens.** WPF still uses SQLite stores and `TenantAccountingSession`. The explicit `MONARCH_DESKTOP_MODE=central` guard exits before SQLite initialization; it is a fail-closed safety barrier, not a central-mode implementation.

## 3. OIDC tenant discovery — implementation status

Migration `011_oidc_tenant_discovery.sql` adds two PostgreSQL security-definer functions:

- `monarch_security.list_oidc_companies(issuer, subject)`
- `monarch_security.list_oidc_company_branches(company, issuer, subject)`

They return only active companies/branches linked to the exact OIDC issuer/subject, an active internal user, and active branch grants. The functions use a fixed search path and are owned by the non-login `monarch_security_definer` role. `PUBLIC` execution is revoked; the API runtime receives only `EXECUTE`, not direct table-read grants from this migration.

The API adds authenticated endpoints:

- `GET /api/v1/tenant-context/companies`
- `GET /api/v1/tenant-context/companies/{companyId}/branches`

`TenantDiscoveryApplicationService` accepts exactly one authenticated identity and extracts one `iss` and one `sub`; it does not accept a user ID, permission, or branch list from caller claims. The endpoints are mapped only in the authenticated PostgreSQL API configuration.

**Implementation exists; runtime data-isolation acceptance remains pending.** The current source-contract suite checks the fixed search path, function grants, claim extraction, route authorization, and API registration. The PostgreSQL migration runner will execute migration 011 in CI, but dedicated database integration assertions for multi-company users, revoked grants, ungranted branches, wrong subjects, and cross-company probes still need to be added and passed before this feature is considered integration-tested.

## 4. SQLite dependency inventory

A scan of the cumulative source archive found 42 C# source files under `src/Monarch.Core` importing `Microsoft.Data.Sqlite`. The dependency spans identity/tenant directory, purchasing/payables, inventory/valuation, ZATCA, procurement automation, backup/recovery, and audit/reporting.

Replacing a connection string or swapping only the GL store would not complete central PostgreSQL migration. Each module needs an explicit server-side application/API boundary and authorization model. Keep SQLite intact for the existing local mode; do not present it as central PostgreSQL mode.

## 5. Required implementation sequence

### Gate A — central desktop identity and transport

- Complete and pass PostgreSQL integration tests for tenant discovery, including multiple companies, active/inactive branches, revoked grants, unmapped subjects, and cross-company probes.
- Implement desktop OIDC Authorization Code + PKCE as a public client; no embedded client secret.
- Validate issuer, audience, signature, nonce, state, expiry, and callback correlation; handle token refresh/expiry and logout safely.
- Keep access tokens out of logs, crash reports, and persistent plaintext settings.
- Implement typed API transport with TLS validation, cancellation, bounded timeouts, safe error mapping, and correlation IDs.
- Continue validating every selected company/branch and operation in PostgreSQL even after discovery.

### Gate B — GL-only central-mode vertical slice

- Wire the existing HTTP-backed `IGeneralLedgerStore` adapter to accounts, journal list/detail/create/post, and trial-balance screens only when central mode is explicitly selected.
- Implement an asynchronous WPF session/view-model boundary; do not block the UI thread on network I/O.
- In central mode, do not initialize or seed SQLite, do not call `TenantAccountingSession` for GL operations, and never fall back to SQLite after API, authorization, or network failure.
- Keep local mode explicitly labelled and isolated. Do not copy central records into a writable SQLite ledger.
- Do not show “posted” until the API confirms PostgreSQL commit.

### Gate C — expand modules by business transaction

For each module, implement PostgreSQL schema, tenant-safe application service, per-operation RBAC, API endpoints, audit/outbox behavior, UI adapter, and integration tests before enabling it in central mode. Prioritize atomic sales → inventory valuation → GL event/outbox, then purchasing/AP/AR, treasury, assets, ZATCA, HR/payroll, and analytics.

### Gate D — historical migration and operations

- Build a read-only SQLite snapshot importer with WAL-aware consistent snapshotting, stable IDs, deterministic checkpoints, idempotent reruns, and signed reconciliation output.
- Reconcile record counts and debit/credit totals by tenant, branch, currency, and fiscal period.
- Prove backup/restore, key rotation, database TLS, runtime role least privilege, and WORM audit export.
- Test Windows 10 and 11 install, upgrade, repair, uninstall, and real-device runtime behavior.

## 6. Release prohibitions

Until all applicable gates pass:

- Do not claim WPF is connected to PostgreSQL.
- Do not enable central-mode financial writes through SQLite.
- Do not silently fall back to SQLite after a central API failure.
- Do not enable offline financial posting until durable device outbox, server idempotency, revocation, conflict handling, and reconciliation are implemented and acceptance-tested.
- Keep PR #10 in Draft and unmerged.

## 7. CI evidence and limitations

Run #419 (`46061634083`, commit `9792db8495083d587d647b3999bed5ce3ba8baee`) passed both Windows build and PostgreSQL integration jobs, including full solution restore/build, Python regression, PostgreSQL 16 migration/integration suite, WPF compatibility publish/package verification, .NET smoke tests, HTTP adapter contract tests, and cumulative archive verification.

The tenant-discovery implementation added after Run #419 is not covered by that run. It must receive its own CI result. A successful build or migration application alone is not data-isolation acceptance; dedicated tests are required as described in Gate A.

The isolated `Monarch.Desktop.Net8.Compatibility.csproj` artifact remains a local SQLite experiment. It does not contain `Monarch.PostgreSql.dll`, does not wire central HTTP transport, and does not implement OIDC/PKCE. It is not the central PostgreSQL desktop client.
