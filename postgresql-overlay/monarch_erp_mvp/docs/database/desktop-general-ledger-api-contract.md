# Desktop GL API contract and compatibility gate

Date: 2026-11-27
Baseline: Run #350, commit `02fbbc84cc195586397fc11271484622df1f2085`
Status: contract extraction from current API source; not a promise of long-term API stability.

## 1. Authentication and tenant context

All routes are in the `/api/v1/general-ledger` group and require authentication. The server requires exactly one valid GUID in each header:

- `X-Monarch-Company-Id`
- `X-Monarch-Branch-Id`

The desktop must send a bearer access token obtained from a real OIDC authorization-code-with-PKCE flow. It must not synthesize `iss`, `sub`, user IDs, tenant claims, or permission claims. The server extracts issuer and subject from the validated identity and PostgreSQL resolves the internal user and checks current company, branch, and permission membership inside the protected transaction.

A company/branch value selected by the desktop is untrusted input. Never cache an authorization decision as a substitute for server enforcement.

## 2. Current route contract

### List accounts

`GET /api/v1/general-ledger/accounts?includeInactive=false`

Permission: `gl.accounts.read`

Success: JSON array of `GeneralLedgerAccount` records:
- `accountId`: GUID
- `accountCode`, `accountName`, `accountType`, `normalSide`: strings

### Create account

`POST /api/v1/general-ledger/accounts`

Permission: `gl.accounts.manage`

Request:
```json
{
  "accountId": "GUID",
  "accountCode": "1000",
  "accountName": "Cash",
  "accountType": "Asset",
  "normalSide": "Debit"
}
```

Success: HTTP 201 with `{ "accountId": "GUID" }`. The server writes its outbox event transactionally with the account.

### Get journal

`GET /api/v1/general-ledger/journals/{journalId}`

Permission: `gl.journal.read`

Success: journal detail record; HTTP 404 when no record is returned.

### Create journal draft

`POST /api/v1/general-ledger/journals`

Permission: `gl.journal.create`

Request:
```json
{
  "journalId": "GUID",
  "fiscalYear": 2026,
  "journalDate": "2026-11-27",
  "description": "Opening adjustment",
  "currencyCode": "SAR",
  "lines": [
    { "accountId": "GUID", "debitMinor": 10000, "creditMinor": 0, "description": "Debit" },
    { "accountId": "GUID", "debitMinor": 0, "creditMinor": 10000, "description": "Credit" }
  ],
  "reversesJournalId": null
}
```

Money values are signed 64-bit integer minor units, not floating-point decimal amounts. The client must not calculate or serialize money as binary floating point.

### Post journal

`POST /api/v1/general-ledger/journals/{journalId}/post`

Permission: `gl.journal.post`

Success: HTTP 204 No Content. The UI may show “posted” only after receiving this success response. A timeout is an unknown outcome, not proof of failure; the client must query journal state before retrying.

### Trial balance

`GET /api/v1/general-ledger/reports/trial-balance?asOfDate=2026-11-27&currencyCode=SAR`

Permission: `gl.report.trial_balance`

Success: JSON array of rows with `accountId`, `accountCode`, `accountName`, `debitMinor`, and `creditMinor`.

## 3. Error handling rules

- `401`: authentication missing, invalid, or expired; re-authenticate/refresh according to the OIDC client lifecycle.
- `403`: server denied identity, tenant membership, or permission. Do not retry automatically.
- `400`: invalid request or tenant selection; surface a safe validation message.
- `404`: requested journal is not available in the authorized scope.
- `409`: operation conflict; refresh server state before offering a retry.
- `5xx`, connection failure, or timeout: operation outcome may be unknown. Never fall back to SQLite in central mode. For journal posting, read the journal state before retrying.

Do not log bearer tokens, ID tokens, authorization codes, PKCE verifiers, invitation tokens, or raw authentication responses.

## 4. Desktop framework compatibility is a blocking design decision

The inspected WPF project currently targets `.NET Framework 4.6.1` (`net461`), while the API and PostgreSQL components target .NET 8. The API contract uses `DateOnly` for journal dates and query parameters. The WPF project cannot assume native `DateOnly` support or the same JSON behavior as the .NET 8 API.

Before implementing the HTTP adapter, complete a compatibility decision:

1. Prefer a controlled WPF retargeting assessment to a supported modern Windows target (likely `net8.0-windows`), including all legacy dependencies and Windows 10/11 requirements.
2. Do not change the target framework until the complete solution restore/build and compatibility scan prove the change safe.
3. If retargeting is not viable, define explicit wire DTOs and a tested serializer/date converter compatible with the existing target; do not leak API domain types into WPF.
4. Pin the API wire contract with integration tests for JSON names, GUIDs, `DateOnly` values, nullability, integer minor units, status codes, and error bodies.

## 5. Central-mode release gate

The current `MONARCH_DESKTOP_MODE=central` startup guard intentionally exits before SQLite initialization because the desktop API adapter and OIDC PKCE client are not implemented. Keep that behavior until all of these are true:

- real OIDC provider E2E with PKCE and token refresh/expiry/revocation handling;
- TLS-validated HTTP transport with bounded timeouts and cancellation;
- six GL routes consumed by a typed desktop adapter;
- central-mode startup and UI tests prove no SQLite ledger initialization or fallback;
- create draft → read → post → trial balance verified against PostgreSQL;
- retry/timeout tests prove no duplicate posting and no false “posted” UI state.

This document is an extracted contract and engineering gate. It is not evidence that the desktop client is connected or accepted for production.
