# WPF to PostgreSQL cutover — repository assessment

Assessment date: 2026-11-27
Branch: `feature/postgresql-central-provider-2026-11-27`
Baseline commit: `1d99afbd8942b51fc7f51f59bbf4ffbb921c73e4`
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

## 2. Scope of the existing PostgreSQL API

The API currently exposes six protected GL operations:

- `GET /api/v1/general-ledger/accounts`
- `POST /api/v1/general-ledger/accounts`
- `GET /api/v1/general-ledger/journals/{journalId}`
- `POST /api/v1/general-ledger/journals`
- `POST /api/v1/general-ledger/journals/{journalId}/post`
- `GET /api/v1/general-ledger/reports/trial-balance`

The API requires a validated OIDC identity and exactly one company and branch selection header. PostgreSQL resolves the internal user and checks current database-backed permission within the transaction. The desktop currently has no demonstrated OIDC authorization-code-with-PKCE login, token lifecycle, or authenticated API transport.

The desktop's existing accounting UI still uses `TenantAccountingSession` methods directly for account listing, journal creation/posting, and trial balance. The API store abstraction exists in `Monarch.Core.Abstractions.IGeneralLedgerStore`, but the WPF screens are not wired to an HTTP-backed implementation.

## 3. SQLite dependency inventory

A scan of the cumulative source archive found **42 C# source files under `src/Monarch.Core` importing `Microsoft.Data.Sqlite`**. The dependency is not limited to the GL: it includes identity and tenant directory, commercial purchasing and payables, inventory and valuation, ZATCA stores, procurement automation, backup/recovery, and audit/reporting.

This inventory means that replacing a connection string or swapping only the GL store would not complete a central PostgreSQL migration. Each module needs an explicit server-side application/API boundary and authorization model. Until then, the current SQLite implementation must remain intact as the existing local mode; it must not be presented as a central PostgreSQL mode.

## 4. Required implementation sequence

### Gate A — central desktop identity and transport

- Implement desktop OIDC authorization-code flow with PKCE as a public client; do not embed a client secret.
- Validate issuer, audience, signature, nonce, state, expiry, and callback correlation; handle refresh/expiry and logout safely.
- Keep access tokens out of logs, crash reports, and persistent plaintext settings.
- Implement a typed API transport with TLS certificate validation, cancellation, bounded timeouts, safe error mapping, and correlation IDs.
- Obtain company/branch selections from server-authorized metadata or treat selections as untrusted IDs that PostgreSQL validates on every operation.

### Gate B — GL-only central-mode vertical slice

- Add an HTTP-backed `IGeneralLedgerStore` adapter matching the six API operations and their exact request/response contracts.
- Wire accounts, journals, posting, and trial-balance screens to that adapter only when central mode is explicitly selected.
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

This assessment was derived from the cumulative CI artifact for Run #344, including the actual WPF project and source files, and cross-checked against the current API endpoint and application-service source. It is an architecture/source inspection, not a Windows runtime acceptance test.


## WPF .NET 8 publish compatibility experiment (2026-11-27)

The production desktop project remains `net461` and the current installer continues to package `artifacts/desktop-net461` plus the `net461` recovery utility. The isolated `Monarch.Desktop.Net8.Compatibility.csproj` has passed a Windows CI compile, but that result alone is not a publish/package acceptance.

The Windows validation workflow now separately publishes this isolated project for `win-x64` as a framework-dependent .NET 8 desktop package, verifies the expected executable/runtime files and transitive Core/SQLite/JSON dependencies, and uploads it under the explicitly experimental artifact name `MONARCH-WPF-NET8-WIN-X64-COMPATIBILITY-EXPERIMENT`. The output is stored outside the cumulative source tree and is not included in the customer installer or release bundle.

This probe deliberately remains a local SQLite desktop compatibility package: it does not include `Monarch.PostgreSql.dll`, does not wire the central HTTP store into WPF, does not implement OIDC/PKCE, and does not authorize central mode. Runtime execution, Windows 10/11 acceptance, installer migration, recovery utility retargeting, and production cutover remain separate release gates.
