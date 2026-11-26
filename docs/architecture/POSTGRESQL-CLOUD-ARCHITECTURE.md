# MONARCH ERP — PostgreSQL Cloud Architecture Roadmap

Status: architectural decision for future implementation. This document does not claim that a PostgreSQL provider, cloud deployment, or synchronization service is already implemented.

## 1. Target topology

- Desktop/offline: WPF client continues to operate against a local SQLite database.
- Cloud: MONARCH API is the only public application boundary. It connects to a managed PostgreSQL service over TLS. PostgreSQL must not be exposed directly to desktop/mobile clients or the public Internet.
- Synchronization: a durable local outbox sends committed local changes to the API; the cloud persists accepted events and an inbox/deduplication record in the same PostgreSQL transaction.
- Other clients: future web and mobile clients use the same authenticated API and business rules. They do not get direct database credentials.

PostgreSQL is the planned central cloud system of record. SQLite remains the local offline store/cache. A customer may operate locally for an extended period and synchronize later.

## 2. Database-provider boundary

Business services must depend on domain/application interfaces, not on Microsoft.Data.Sqlite, SQL dialects, or provider-specific connection objects.

Introduce provider-specific persistence implementations behind explicit contracts, for example:
- ILedgerRepository / IJournalUnitOfWork
- IInventoryRepository
- ISalesRepository
- IOutboxStore / IInboxStore
- ITenantContext and IClock

Implement SQLite first for desktop/offline use and PostgreSQL for the cloud API. Do not pretend that SQL is portable: keep SQLite and PostgreSQL migrations/queries provider-specific behind the same domain contract. Avoid passing raw SQL or provider connections across module boundaries.

## 3. Tenant and authorization isolation

Every tenant-owned row must carry company_id; branch-owned records also carry branch_id. Enforce scope in the application service and database:
- PostgreSQL composite foreign keys must include tenant keys, preventing cross-company references.
- Enable Row-Level Security on tenant-owned tables as defense in depth.
- The API sets tenant/user context transaction-locally (SET LOCAL) after authenticating the caller. Never use session-global tenant state with pooled connections.
- Policies must fail closed when tenant context is absent.
- The runtime database role must not own protected tables and must not have BYPASSRLS.
- Authorization is evaluated on every operation; UI visibility is not an authorization boundary.
- Add adversarial tests for cross-company reads/writes, branch escalation, missing context, inactive users, pooled-connection context leakage, and background jobs.

RLS is an additional boundary, not a replacement for explicit authorization and correctly scoped queries.

## 4. Data and concurrency conventions

- Use UUID identifiers consistently across local and cloud records.
- Store timestamps as UTC (timestamptz in PostgreSQL); preserve business dates separately as date-only values.
- Store money as integer minor units or exact numeric, never binary floating point.
- Use explicit row_version values for optimistic concurrency. Do not rely on PostgreSQL's internal xmin as a durable application version.
- Keep posted journals and finalized commercial documents immutable; correct them through reversals/credit notes, not destructive updates.
- Use transactionally allocated document counters scoped by company/branch/document type where legal numbering rules require it.
- Treat schema migrations as versioned, reviewed, forward-only production changes with tested backup/rollback procedures.

## 5. Offline synchronization protocol

Each locally committed business change creates an outbox event in the same SQLite transaction as the business write. Events should include:
- globally unique event_id
- tenant_id / company_id, branch_id, and actor/device identifiers
- aggregate type and aggregate ID
- aggregate version and event schema version
- occurred-at UTC timestamp and causation/correlation IDs
- payload plus integrity metadata

The cloud API authenticates the device and tenant, validates the event, and records the inbox deduplication key and business changes atomically. Repeated delivery of the same event must return the original result without duplicating an invoice, stock movement, journal, or payment.

Use acknowledgements and durable cursors. Retry transient failures with bounded exponential backoff and jitter; quarantine poison events with a visible operator workflow. Do not delete local outbox events until the server acknowledgement is durably recorded.

Conflict policy is domain-specific, never blanket last-write-wins:
- posted accounting entries: immutable; use explicit reversal/correction workflows
- stock movements: append-only, ordered by durable sequence and reconciled against the cloud ledger
- master data: version checks and explicit conflict resolution
- drafts: controlled merge or user review

## 6. Reliability and security

- API authentication via a standards-based identity provider (OIDC/OAuth 2.0); short-lived access tokens and refresh-token rotation where applicable.
- TLS for every client/API and API/database connection.
- Secrets in a managed secret store, never in source code, ZIP bundles, desktop config, or logs.
- Least-privilege database roles; separate migration/owner role from runtime role.
- Rate limits, request size limits, audit trails, correlation IDs, and redaction of credentials/PII in logs.
- Automated encrypted backups, point-in-time recovery, restore drills, retention policy, and documented RPO/RTO.
- Tenant-aware audit events and security monitoring.
- No autonomous agent may post payments, journals, payroll, or external submissions without explicit permission/approval policy.

## 7. Required test gates before cloud release

1. SQLite and PostgreSQL repository contract tests run against both real engines.
2. Migration tests from every supported schema version.
3. Tenant isolation and RLS tests using the actual restricted runtime database role.
4. Idempotent event delivery, out-of-order delivery, duplicate delivery, retry, poison-event, and crash-recovery tests.
5. Concurrent numbering, stock movement, journal posting, and outbox worker tests.
6. Network interruption and long-offline synchronization tests.
7. Backup/PITR restore drills and tenant data export/deletion procedures.
8. Load, latency, failover, and security testing before production rollout.

## 8. Implementation sequence

1. Finish and stabilize the current SQLite persistence, restore, and Windows validation work.
2. Introduce repository/unit-of-work interfaces without changing current business behavior.
3. Add provider contract tests and a PostgreSQL schema/migration project.
4. Implement PostgreSQL repositories and API tenant context/RLS.
5. Implement the durable outbox/inbox protocol and reconciliation.
6. Add cloud deployment, secrets, backups, observability, and disaster recovery.
7. Connect web/mobile clients only through the API.

This roadmap is additive. It does not authorize removal of existing SQLite functionality or previously completed modules.