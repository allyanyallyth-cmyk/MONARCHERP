# PostgreSQL GL application cutover — execution contract

Status: implementation gate; this document does not claim that the UI/API cutover is complete.

## Non-negotiable invariants
1. PostgreSQL is the sole authority for hosted financial writes.
2. A request never chooses its own company, branch, or user scope. The API derives the scope from a validated authenticated principal and verifies active membership inside the database transaction.
3. Every operation is authorized separately (read accounts, read journal, create draft, edit draft, post, reverse, export). Membership alone is not an authorization grant.
4. A posted journal and its lines are immutable. Corrections use an exact reversal and a new journal.
5. The same database transaction persists the journal and its outbox event. The event ID is deterministic and retries are idempotent.
6. No SQLite/PostgreSQL dual-write is permitted.
7. Offline financial posting stays disabled until the device outbox protocol and reconciliation are implemented and acceptance-tested.

## Required application boundary
Introduce an application service that accepts authenticated actor context, not client-supplied tenant identifiers:
- ListAccounts: read-only, scoped to the authenticated company.
- GetJournal: read-only, scoped to company and branch; return not-found for both absent and RLS-hidden records.
- CreateDraft: require gl.journal.create; validate currency, account ownership, and integer minor-unit amounts.
- UpdateDraft: require gl.journal.edit; only creator/authorized finance role may edit an unposted draft.
- Post: require gl.journal.post; revalidate balanced totals and account eligibility inside the transaction.
- Reverse: require gl.journal.reverse; require exact reversal semantics and a reason; never mutate the original posted journal.
- GetTrialBalance: require gl.report.trial_balance; filter by company, branch, period, and currency.

The application service must not accept companyId, branchId, or userId as trusted method parameters from HTTP payloads. If a route contains a resource ID, resolve its tenant scope from the signed server principal and enforce RLS on the same connection/transaction.

## API security requirements
- Require authenticated identity for every GL endpoint.
- Map stable application permission keys to the authenticated principal; deny by default.
- Never trust role names, tenant IDs, or permission arrays supplied in request JSON.
- Use parameterized SQL and a transaction-local signed tenant context.
- Return a consistent authorization/not-found response that does not disclose cross-tenant resource existence.
- Audit actor, action, resource ID, timestamp, outcome, and correlation ID. Do not log HMAC secrets, access tokens, or full connection strings.
- Apply request-size limits, validation, cancellation tokens, and structured error handling.

## UI cutover requirements
- Central mode must call the authenticated API/application service for all GL reads and writes.
- Remove/disable direct SQLite GL writes in central mode; do not silently fall back to SQLite after a PostgreSQL/API error.
- Keep existing SQLite code intact until an explicit, tested offline mode exists. It must not be a second active financial authority.
- Display connection/authorization failures distinctly; never report a journal as posted until the server confirms commit.

## Historical migration gate
A one-time importer is not production-ready until it:
1. Takes a consistent SQLite snapshot (including WAL state) and records source database identity/hash.
2. Preserves stable IDs, account codes, journal numbers, currencies, minor-unit amounts, dates, actor metadata, reversals, and audit history.
3. Imports in deterministic batches with a durable checkpoint and idempotent rerun behavior.
4. Rejects malformed, unbalanced, orphaned, duplicate-number, or cross-tenant records; never silently repairs accounting data.
5. Reconciles counts and per-account/per-currency debit-credit totals by company, branch, and fiscal period.
6. Produces a signed migration report and a rollback/restore procedure.
7. Keeps the source database unchanged and read-only throughout import.

## Offline synchronization gate (future work)
Before enabling offline financial writes, define immutable device-generated operation IDs, device identity and key rotation, monotonic per-device sequence, signed payloads, durable retry state, server idempotency, conflict policy, revocation handling, and explicit acceptance/rejection receipts. Posted records must never be overwritten by a stale device. Conflicts affecting financial meaning must be escalated for authorized human resolution.

## Acceptance tests required before central UI cutover
- Anonymous request denied for every GL route.
- User from company A cannot enumerate, read, mutate, export, or infer company B records.
- User in branch A cannot access branch B records without explicit permission.
- Revoked membership and revoked permissions take effect on the next transaction.
- Each permission is independently enforced for draft creation/edit/post/reverse/report.
- Client-supplied tenant/user/role fields cannot change the effective scope.
- PostgreSQL/API outage never triggers SQLite fallback or a false success response.
- Concurrent journal posts preserve unique numbering and balanced ledger state.
- A failed transaction leaves neither a posted journal nor an outbox event.
- A retry returns the original outcome and does not duplicate journal/outbox records.
- UI confirms posting only after server commit.

## Release gate
Do not mark the GL cutover complete until the application service, API endpoints, permission matrix, central-mode UI wiring, negative security tests, historical migration rehearsal, and Windows 10/11 runtime validation all pass. Keep the pull request in draft until these gates are evidenced by CI and review.