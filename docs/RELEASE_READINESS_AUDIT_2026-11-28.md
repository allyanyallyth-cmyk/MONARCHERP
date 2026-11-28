# MONARCH ERP — Release Readiness Audit
**Audit date:** 2026-11-28  
**Reviewed source ref:** `00d8ca6f2d932e5e8cf5ab046e515f0fd7edccca`  
**Cumulative artifact:** `MONARCH-ERP-CUMULATIVE-POSTGRESQL-2026-11-27.zip`  
**Artifact SHA-256:** `61fe4f0dbea1a5410cb80e39e0ec49f0dc55eb47f632b30f41031fe1ce0111a0`

## Executive decision

**NOT APPROVED FOR CUSTOMER PRODUCTION.** The current source passes the latest Windows build and PostgreSQL CI jobs, and the PostgreSQL General Ledger foundation is real. However, the production WPF application is still SQLite-only. Its central-mode startup guard explicitly exits before initializing SQLite, and the WPF project does not reference the MSAL authentication project or the remote General Ledger adapter. OIDC is not configured in the hosted API, so protected GL and tenant routes are not mapped. There is no durable offline-to-central synchronization implementation for financial posting, and the commercial installer/licensing/upgrade path has not been validated for this PostgreSQL architecture.

A development/demo evaluation can continue under controlled conditions. Do not load customer financial data or describe this as a production-ready ERP.

## What was inspected

- The GitHub branch tree at the reviewed commit: 165 entries, including the nested cumulative source archive and PostgreSQL overlay.
- The downloaded cumulative artifact: 626 archive entries; ZIP integrity check passed. It contains 113 C# files, 40 SQL files, 2 XAML files, and 113 Python test files under the test tree.
- The complete solution/project declarations, WPF startup path, API startup/authentication registration, PostgreSQL provider and migration runner, SQL migrations 001–013, installer script, Windows packaging scripts, CI workflows, README, roadmap, architecture and cutover documentation.
- The active GitHub Actions run 46130277996 and its Windows job logs.

## Verified implementation and tests

| Area | Evidence | Assessment |
|---|---|---|
| Windows solution build | CI run 46130277996; Windows job reports `Build succeeded` | [Tested in CI] |
| Python regression suite | Windows CI log: `811 passed` | [Tested in CI] |
| Core accounting/tenant smoke checks | Windows CI completed the C# smoke-test step, including journal, restore, tenant isolation, branch grants and revocation checks | [Tested in CI] |
| Desktop HTTP adapter contracts | CI exercised token/header mapping, DTO serialization, 403 handling, no unsafe write retry, HTTPS enforcement and tenant discovery | [Tested in CI] |
| Desktop authentication component | CI reports `PASS: desktop OIDC configuration validation.` | [Tested in CI; not a live IdP login] |
| PostgreSQL integration | CI PostgreSQL job completed successfully against PostgreSQL 16; tenant/RLS and outbox integration assertions ran | [Tested in CI] |
| Neon schema | Migrations 001–013 applied and checksums recorded on the dedicated Neon main branch | [Applied to hosted DB] |
| Tenant isolation | Restricted runtime role and separate validation tenants tested against Neon; cross-tenant read denied and authorized tenant insert accepted | [Tested against Neon validation branch] |
| Vercel API deployment | Latest reviewed deployment state was READY in region fra1 | [Deployed; runtime DB connectivity not proven] |
| Vercel secrets | PostgreSQL connection and context HMAC values exist as sensitive Preview-only variables scoped to the feature branch | [Configured; not a successful runtime connection test] |
| Cumulative source artifact | CI uploaded artifact ID 15257209187; nested ZIP integrity and SHA-256 checked | [Generated] |

## Critical blockers

### 1. WPF is not connected to the PostgreSQL API

The production desktop project `src/Monarch.Desktop/Monarch.Desktop.csproj` still targets `net461` and references only `Monarch.Core`. It does not reference `Monarch.Desktop.Authentication`, and the WPF startup path constructs `SqliteLedgerStore`, `IdentityStore`, `CompanyDirectoryStore` and `TenantLedgerStore`.

The WPF startup explicitly rejects `MONARCH_DESKTOP_MODE=central` with a message that central PostgreSQL mode is unavailable. The UI also labels itself as using local SQLite. The remote adapter `HttpGeneralLedgerStore` and MSAL component exist as separate source components, but they are not wired into the production WPF application.

The `net8.0-windows` WPF compatibility project is an isolated build experiment, not the production desktop target and not evidence of a central-mode cutover.

**Gate:** implement and test the actual desktop sign-in, API client, tenant discovery, GL read/write/report workflows and fail-closed central mode. Do not remove the explicit central-mode guard until those end-to-end tests pass.

### 2. Hosted OIDC and user enrollment are not configured

The Vercel Preview environment currently has the PostgreSQL connection and tenant-context HMAC secrets only. The following identity-provider configuration is still missing: `MONARCH_OIDC_AUTHORITY`, `MONARCH_OIDC_AUDIENCE`, and `MONARCH_OIDC_ENROLLMENT_CLIENT_ID`, plus the registered desktop public-client application and redirect URI.

When JWT/OIDC configuration is absent, API startup does not register authentication and does not map protected GL or tenant-discovery routes. Health/demo routes remain available. Therefore the existence of a READY deployment is not proof of authenticated ERP functionality.

**Gate:** configure a real provider, verify authority/audience/client settings, complete an actual MSAL PKCE sign-in, and test API authorization and controlled identity enrollment with a test tenant.

### 3. Vercel-to-Neon runtime connection remains unverified

The Preview deployment is protected by Vercel SSO. Unauthenticated probes did not reach the application, and no runtime logs were observed for the checked period. Neon connectivity was validated separately through database tooling and PostgreSQL CI, but a successful Npgsql health query from the deployed API runtime has not been captured.

**Gate:** use an authorized Preview session or a protected deployment smoke test to execute `/health/postgresql`, verify TLS hostname validation and capture a successful runtime result. Keep Production secrets and deployment untouched until approval.

### 4. Offline synchronization is not complete

SQLite remains the desktop persistence engine. The architecture and cutover documentation explicitly require a durable local outbox, authenticated cloud inbox, idempotent event delivery, sequence/version rules, retry/quarantine, conflict resolution, acknowledgement persistence and reconciliation. These are not completed as a production sync engine.

**Gate:** no offline financial posting may synchronize to the hosted ledger until the complete protocol and crash/network/replay/conflict tests pass. No dual-write or silent SQLite fallback is permitted.

### 5. PostgreSQL coverage is General Ledger foundation, not the entire ERP

The current PostgreSQL provider has tenant/security and GL/outbox schema and repositories. The deployment documentation explicitly lists PostgreSQL repositories for inventory, sales, purchasing, payroll and reporting as incomplete. Existing commercial/inventory modules largely remain SQLite-backed.

**Gate:** design and implement provider contracts and PostgreSQL persistence/API operations module by module, with migration, RLS, authorization and parity tests before central mode exposes those modules.

### 6. Installer and commercial release are not accepted

The legacy installer definition and package script target the `.NET Framework 4.6.1` desktop build and package local database schemas/tools. The active PostgreSQL overlay validation workflow does not run the Inno Setup packaging and install/upgrade/uninstall acceptance workflow. The separate Windows workflow exists in the source bundle but its presence is not evidence that the installer was executed in the latest PostgreSQL validation run.

No implemented license/trial activation subsystem was found in the reviewed source. Release signing, trusted timestamping, customer upgrade/rollback and clean uninstall have not been accepted.

**Gate:** choose the production desktop runtime first, then build a matching installer, test fresh install/upgrade/rollback/uninstall on clean Windows 10 and 11 VMs, sign binaries, and implement/test the agreed licensing policy.

### 7. Repository and PR topology needs deliberate review

PR #10 is open and Draft, and its base is `ci/extract-build-test-monarch-2026-11-27`, not the repository default branch `main`. The current head and `main` have diverged. The comparison reports 350 commits ahead and 2 behind for the head relative to `main`; the PR base is an ancestor of the head. This appears to be a staging/extraction branch arrangement and must be reconciled intentionally.

**Gate:** inspect the base branch purpose and compare the intended production integration strategy before retargeting or merging. No merge or Production deployment was performed during this audit.

## Medium and lower-priority release work

- Add real Windows 10 and 11 interactive UI tests and accessibility/RTL checks.
- Add database migration rehearsal from supported SQLite versions, including deterministic accounting reconciliation and rollback evidence.
- Validate backups, restore, key rotation, disaster recovery, RPO/RTO and retention.
- Add API request limits, operational metrics, structured redacted logs, alerting, rate limits and load/failover tests.
- Complete ZATCA production certificate/secret provider, certificate lifecycle validation, live sandbox integration and reconciliation.
- Review financial-period close, tax, multi-currency, bank reconciliation and reporting scope before claiming a complete accounting suite.

## Exact release decision

- **Source foundation:** available and cumulative.
- **Windows compilation:** passed in CI for the reviewed source ref.
- **PostgreSQL GL/security foundation:** integration-tested; hosted schema applied.
- **Hosted API:** Preview deployment exists; authenticated runtime and Neon connectivity not yet proven.
- **WPF central operation:** not implemented; explicitly blocked.
- **Offline/central synchronization:** not implemented to release acceptance.
- **All ERP modules on PostgreSQL:** not implemented.
- **Customer installer/licensing/upgrade:** not accepted.
- **Customer production release:** **NO-GO**.

## Next execution order

1. Reconcile PR #10 branch ancestry and establish the intended integration branch without force-pushing or merging blindly.
2. Configure OIDC and prove the hosted API-to-Neon connection.
3. Integrate the actual WPF client with MSAL/API/tenant discovery and PostgreSQL GL operations.
4. Implement durable offline synchronization and reconciliation; retain fail-closed behavior until acceptance.
5. Port sales, inventory and purchasing persistence/API boundaries, then payroll/reporting as separately scoped modules.
6. Build and validate the production installer, upgrade/uninstall, signing and licensing.
7. Run Windows 10/11 acceptance, backup/restore drills, security and performance tests; only then reconsider GO.
