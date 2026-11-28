# MONARCH ERP

**MONARCH ERP** is a modular Windows ERP foundation with an Arabic RTL desktop client, local SQLite persistence, and a separate .NET 8 API/PostgreSQL central-service foundation.

> **Release decision (2026-11-28): NOT READY FOR CUSTOMER PRODUCTION.**
> The Windows solution and current CI suites pass, but the WPF client is still local-SQLite-only. Central PostgreSQL mode is deliberately blocked in the desktop startup path until API/OIDC integration and synchronization are completed.

## Current verified state

- GitHub Actions run [46130277996](https://github.com/allyanyallyth-cmyk/MONARCHERP/actions/runs/46130277996): Windows build and PostgreSQL integration jobs completed successfully.
- Python regression suite: 811 passed in the Windows CI job.
- .NET solution build, Core smoke checks, desktop HTTP contract checks, and desktop authentication component checks passed in that CI run.
- PostgreSQL migrations 001–013 were applied to the dedicated Neon project. RLS/tenant-isolation and outbox idempotency were exercised in PostgreSQL integration tests and a separate validation branch.
- A Vercel Preview deployment exists for the API. Its database connection from the deployed runtime has **not** yet been proven by an authenticated health probe.
- The cumulative source artifact is published by the successful CI run. It is a source bundle, not a signed customer installer.

## Architecture and operating modes

- **Local desktop:** WPF on .NET Framework 4.6.1 using SQLite. The code includes tenant/company/branch controls and a local accounting/commercial foundation.
- **Central service foundation:** ASP.NET Core API on .NET 8, Npgsql/PostgreSQL 16, transaction-local tenant context, database RLS, and a General Ledger API boundary.
- **PostgreSQL is not yet the active desktop financial provider.** Setting `MONARCH_DESKTOP_MODE=central` intentionally stops startup rather than silently falling back to SQLite.
- The separate `.NET 8 WPF compatibility` project is an isolated build experiment; it is not yet the production desktop target and does not complete the central-mode UI cutover.
- SQLite remains preserved. Do not remove it or enable dual writes.

## Major modules in the source foundation

Accounting and tenant controls; sales and commercial documents; purchasing and supplier invoices; inventory and valuation; supplier payables and alerts; backup/recovery; ZATCA preparation; procurement analytics and draft-only autonomy. Module presence in source does not mean every module has a PostgreSQL repository or production acceptance test.

## Critical remaining release gates

1. Integrate the WPF desktop with the authenticated API using MSAL/OIDC; wire tenant discovery and GL reads/writes into the real UI.
2. Configure a real OIDC authority, API audience, desktop public-client ID, redirect URI and enrollment client; execute real-provider sign-in and authorization tests.
3. Prove Vercel-to-Neon TLS/Npgsql connectivity from the deployed runtime using an authenticated, observable health check.
4. Implement durable local outbox/cloud inbox synchronization, replay protection, retry, conflict handling and reconciliation before allowing offline financial posting to sync.
5. Implement PostgreSQL repositories and API coverage for inventory, sales, purchasing, payroll and reporting; the current central GL foundation is not a full ERP data layer.
6. Rehearse historical SQLite-to-central migration with reconciliation and rollback.
7. Produce and test a customer installer/upgrade/uninstall path for the selected supported Windows versions; sign release binaries.
8. Add license/trial activation, secure key lifecycle and customer administration if commercial licensing remains required.
9. Complete Windows 10/11 device testing, restore drills, security review, performance/load and operational monitoring.
10. Keep PR #10 in Draft until the cutover gates are evidenced. It currently targets the CI extraction branch, not `main`; review branch ancestry before changing the PR base or merging.

## Build and test

On a Windows development machine with .NET 8 SDK, Visual Studio Build Tools, Python 3.12 and the required Windows desktop targeting packs:

```powershell
dotnet restore MonarchERP.sln
dotnet build MonarchERP.sln --configuration Release --no-restore
dotnet run --project tests/Monarch.Core.SmokeTests/Monarch.Core.SmokeTests.csproj --configuration Release
dotnet run --project tests/Monarch.Desktop.Http.Tests/Monarch.Desktop.Http.Tests.csproj --configuration Release
dotnet run --project tests/Monarch.Desktop.Authentication.Tests/Monarch.Desktop.Authentication.Tests.csproj --configuration Release
```

The PostgreSQL integration project requires a disposable PostgreSQL 16 database and a restricted runtime role. Never run destructive migration tests against the customer's production database.

## Release policy

- Keep the project cumulative: changes must preserve existing source, modules, migrations and recovery assets.
- Never put database credentials, HMAC keys, signing keys or customer secrets in source control or desktop packages.
- Never report a journal as posted before the central database confirms commit.
- Never silently fall back from central mode to SQLite after an API/database failure.
- Do not call this build customer-ready until all critical release gates above pass.
