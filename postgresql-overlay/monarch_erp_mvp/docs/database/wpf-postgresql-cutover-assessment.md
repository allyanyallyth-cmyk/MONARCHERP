# WPF to PostgreSQL cutover — repository assessment

Assessment date: 2026-11-27
Branch: `feature/postgresql-central-provider-2026-11-27`
Baseline commit: `0a8e6b3869e5f9fd27d03c8bda1092d83ddf995c`
Status: **assessment only; no desktop cutover is claimed**

## 1. Verified current desktop startup path

The cumulative source archive produced by Windows CI was downloaded and inspected. The WPF project is present at `src/Monarch.Desktop/Monarch.Desktop.csproj` and currently targets `.NET Framework 4.6.1` (`net461`).

`src/Monarch.Desktop/MainWindow.xaml.cs` currently performs this startup sequence:

1. Creates `%LocalAppData%/MONARCH/Data/monarch-ledger.db`.
2. Initializes `SqliteLedgerStore`.
3. Initializes local `IdentityStore`, `CompanyDirectoryStore`, and `TenantLedgerStore`.
4. Authenticates through `SecurityGateWindow.Run(_identity)`.
5. Selects company and branch through `TenantContextWindow`.
6. Opens `TenantAccountingSession`, seeds starter accounts, builds the WPF interface, and refreshes the UI.

Consequently, the desktop login, tenant selection, and accounting session are still SQLite-backed. This is not a PostgreSQL central-mode client.

## 2. Scope of the existing PostgreSQL API and HTTP adapter

The API exposes seven protected GL operations, including a bounded tenant-scoped journal-list query:

- `GET /api/v1/general-ledger/accounts`
- `POST /api/v1/general-ledger/accounts`
- `GET /api/v1/general-ledger/journals?limit=250` (limit 1–1000; requires `gl.journal.read`)
- `GET /api/v1/general-ledger/journals/{journalId}`
- `POST /api/v1/general-ledger/journals`
- `POST /api/v1/general-ledger/journals/{journalId}/post`
- `GET /api/v1/general-ledger/reports/trial-balance`

The API requires a validated OIDC identity and exactly one company and branch selection header. PostgreSQL resolves the internal user and checks current database-backed permission within the transaction. The desktop currently has no demonstrated OIDC authorization-code-with-PKCE login, token lifecycle, or central-mode composition root.

The Core store abstraction and HTTP adapter now exist: `IGeneralLedgerStore` includes bounded journal summaries, and `HttpGeneralLedgerStore` maps the seven GL operations to the protected API. The adapter has desktop HTTP contract/smoke coverage in CI. **It is not yet wired into the WPF screens.** The current WPF startup still creates SQLite stores and opens `TenantAccountingSession`; account listing, journal creation/posting, and trial balance are not yet driven by the HTTP adapter.

The desktop central-mode guard remains fail-closed: explicit `MONARCH_DESKTOP_MODE=central` exits before local SQLite initialization because the central WPF client is not available in this release. The guard is a safety barrier, not a central-mode implementation.

## 3. SQLite dependency inventory

A scan of the cumulative source archive found **42 C# source files under `src/Monarch.Core` importing `Microsoft.Data.Sqlite`**. The dependency is not limited to the GL: it includes identity and tenant directory, commercial purchasing and payables, inventory and valuation, ZATCA stores, procurement automation, backup/recovery, and audit/reporting.

This inventory means that replacing a connection string or swapping only the GL store would not complete a central PostgreSQL migration. Each module needs an explicit server-side application/API boundary and authorization model. Until then, the current SQLite implementation must remain intact as the existing local mode; it must not be presented as a central PostgreSQL mode.

## 4. Required implementation sequence

### Gate A — central desktop identity, tenant discovery, and transport

- Implement secure server-side discovery of companies and branches authorized for the authenticated OIDC issuer/subject. Do not rely on caller-supplied tenant identifiers as proof of membership.
- Implement desktop OIDC authorization-code flow with PKCE as a public client; do not embed a client secret.
- Validate issuer, audience, signature, nonce, state, expiry, and callback correlation; handle refresh/expiry and logout safely.
- Keep access tokens out of logs, crash reports, and persistent plaintext settings.
- Implement a typed API transport with TLS certificate validation, cancellation, bounded timeouts, safe error mapping, and correlation IDs.
- Continue validating every selected company/branch and operation in PostgreSQL, even after tenant discovery.

### Gate B — GL-only central-mode vertical slice

- Wire the existing HTTP-backed `IGeneralLedgerStore` adapter to accounts, journal list/detail/create/post, and trial-balance screens only when central mode is explicitly selected.
- Implement an asynchronous WPF session/view-model boundary; do not block the UI thread on network I/O.
- In central mode, do not initialize or seed the SQLite ledger, do not call `TenantAccountingSession` for GL operations, and never fall back to SQLite after API, authorization, or network failure.
- Keep local mode explicitly labelled and isolated. Do not copy central records into a writable SQLite ledger.
- Do not show “posted” until the API confirms the PostgreSQL transaction committed.

### Gate C — expand modules by business transaction

For each module, implement its PostgreSQL schema, tenant-safe application service, per-operation RBAC, API endpoints, audit/outbox behavior, UI adapter, and integration tests before enabling it in central mode. Prioritize the atomic sales → inventory valuation → GL event/outbox chain, then purchasing/AP/AR, treasury, assets, ZATCA, HR/payroll, and analytics.

### Gate D — historical migration and operations

- Build a read-only SQLite snapshot importer with WAL-aware consistent snapshotting, stable IDs, deterministic checkpoints, idempotent reruns, and signed reconciliation output.
- Reconcile record counts and debit/credit totals by tenant, branch, currency, and fiscal period.
- Prove backup/restore, key rotation, database TLS, runtime role least privilege, and WORM audit export.
- Test Windows 10 and 11 install, upgrade, repair, uninstall, and real-device runtime behavior.

## 5. Release prohibitions

Until all applicable gates pass:

- Do not claim WPF is connected to PostgreSQL.
- Do not enable central-mode financial writes through SQLite.
- Do not implement silent SQLite fallback after a central API failure.
- Do not enable offline financial posting until durable device outbox, server idempotency, revocation, conflict handling, and reconciliation are implemented and acceptance-tested.
- Keep PR #10 in Draft and unmerged.

## 6. Evidence basis

The desktop startup and SQLite coupling were inspected from the cumulative Windows CI source archive. The current API endpoint, application-service, and HTTP adapter sources were cross-checked against the branch head.

Run #417 (`46036059205`, commit `0a8e6b3869e5f9fd27d03c8bda1092d83ddf995c`) completed successfully:
- PostgreSQL 16 integration job passed, including the journal-list query tests for aggregated debit, bounded limits, invalid-limit rejection, and branch isolation.
- Windows Python regression, full Windows solution restore/build, existing .NET smoke tests, isolated WPF .NET 8 compatibility publish/package verification, desktop HTTP adapter contract tests, and cumulative archive boundary checks passed.
- The cumulative artifact contains the HTTP adapter, concurrency-test patch and contract test, and this assessment.

This is CI build/integration evidence and source inspection. It is **not** a Windows desktop runtime acceptance test, a real OIDC-provider login, or a live WPF-to-PostgreSQL transaction E2E test.

## 7. WPF .NET 8 publish compatibility experiment (2026-11-27)

The production desktop project remains `net461` and the current installer continues to package `artifacts/desktop-net461` plus the `net461` recovery utility. The isolated `Monarch.Desktop.Net8.Compatibility.csproj` passed a Windows CI build and a `win-x64` framework-dependent publish.

The Windows validation workflow verifies the executable, runtime files, Core/SQLite/JSON dependencies, and the declared `Microsoft.NETCore.App 8.0.0` plus `Microsoft.WindowsDesktop.App 8.0.0` frameworks. The artifact is explicitly named `MONARCH-WPF-NET8-WIN-X64-COMPATIBILITY-EXPERIMENT`; it is not included in the customer installer or release bundle.

The downloaded package was inspected: it contains the WPF executable, Core, SQLite, and JSON dependencies, and does not contain `Monarch.PostgreSql.dll`. It is therefore a local SQLite compatibility experiment, not the central PostgreSQL desktop client. Runtime execution, Windows 10/11 acceptance, installer migration, recovery utility retargeting, and production cutover remain separate release gates.
