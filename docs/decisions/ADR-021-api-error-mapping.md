# ADR-021 — API Error Mapping (HTTP Status and Response Codes)

**Status:** Accepted — product owner decision (Milestone 12 plan), implemented in Milestone 12e (`src/Ransys.Api/Errors/ApiErrors.cs`, `Validation/RequestValidator.cs`).

## Context
OpenAPI v1 §6 separates the HTTP status (API/security boundary) from the business truth (`responseCode` + `transactionStatus`). It declares 200, 400, 401, 403, 409, 429, 503 for the POST endpoints and 200, 401, 403, 404, 429, 503 for `GET /api/v1/transactions/{id}`. The Response Code Catalog v1 does not exist yet; Architecture Spec §21 / PRD list example codes only.

Transaction Core (M12d) returns a `RansysError` when a request is rejected **before** a transaction is accepted into normal processing (validation, reference data, the original's state for children, duplicate conflict), and throws `FinancialDependencyUnavailableException` when the Transaction DB fails before any provider call. Business outcomes of an accepted transaction (provider decline, insufficient balance, no route, IN_DOUBT) are results, not errors.

## Existing RANSYS rule
- HTTP 200 = the request entered normal processing, including FAILED / PENDING / IN_DOUBT (OpenAPI v1 §6, handoff §13).
- Use only codes already approved in the docs (handoff §30); anything else is a TODO for the catalog.
- Same client reference + different fingerprint ⇒ 409 / 2003.
- Cannot establish the durable transaction + reserve ⇒ no provider call, fail closed.

## Technical issue
Several M12d rejections have no obvious status in the declared set: the original's state for a child (`REFUND_NOT_ALLOWED`, `REVERSAL_ALREADY_ACTIVE`, …), refund over the remaining amount, a merchant without a wallet in the currency, a child whose provider lacks the capability, missing/ambiguous configuration, and unexpected failures. 409 is described in the OpenAPI as "same active clientReference used with a different fingerprint" only. There are no approved codes for "not found", "rate limited" or "forbidden". The OpenAPI GET operation declares no 400, but the handoff (§28) and the plan require 400 for a malformed path UUID.

## Options
1. Add statuses (404 on POST, 422, 500) and new codes. Rejected: changes the contract and invents codes.
2. Map every rejection into the declared set with the approved codes, and record the gaps as TODOs.

## Recommended option
Option 2. Only the approved codes `0000, 1001, 1002, 2001, 2003, 3001, 4001, 5001` are used.

| Situation | HTTP | errorCode / responseCode | field |
|---|---|---|---|
| Accepted transaction, any outcome (SUCCESS, FAILED, PENDING, IN_DOUBT, …) | 200 | `responseCode` from the result: 0000 success, 1002 IN_DOUBT, 4001 insufficient balance, 5001 no route, captured code or 1001 otherwise | — |
| Malformed JSON, wrong JSON type (e.g. amount as a number), unknown or mis-cased property, duplicate property, non-object body, non-JSON content type | 400 | 2001 | JSON path when known |
| Schema validation: required, min/max length, `DecimalAmount` pattern, amount ≤ 0, currency `^[A-Z]{3}$`, `format: uuid` / `date-time` (RFC 3339 with offset), enum, `Idempotency-Key` > 128 | 400 | 2001 | property path (`amount.value`, `destination.type`, `Idempotency-Key`, …) |
| Core validation: `PRODUCT_NOT_AVAILABLE`, `CURRENCY_NOT_SUPPORTED`, `MONEY_PRECISION_EXCEEDS_SCALE`, `CURRENCY_MISMATCH`, metadata errors, other `Validation` category | 400 | 2001 | from the error (`productCode`, `amount.currency`, `amount.value`, …) |
| `ORIGINAL_TRANSACTION_INVALID` (unknown or other channel's original, indistinguishable) | 400 | 2001 | `originalTransactionId` |
| Original's state forbids the child: `REFUND_NOT_ALLOWED`, `REVERSAL_NOT_ALLOWED`, `REVERSAL_ALREADY_ACTIVE`, `VOID_NOT_ALLOWED`, `VOID_ALREADY_ACTIVE`, `INVALID_STATE_TRANSITION` | 400 | 2001 | `originalTransactionId` |
| `REFUND_EXCEEDS_POSTED` (child cap or ledger backstop) | 400 | 2001 | `refundAmount.value` |
| `WALLET_NOT_FOUND` (merchant has no usable main wallet in the currency) | 400 | 2001 | `amount.currency` |
| `INSUFFICIENT_BALANCE` returned as an error (normally it is a FAILED 200) | 400 | 4001 | — |
| Child whose original provider lacks the capability (`NO_ROUTE_AVAILABLE`, `REVERSAL_NOT_SUPPORTED`) | 400 | 5001 | `originalTransactionId` |
| `DUPLICATE_REFERENCE_CONFLICT` (also a child reference reused for another original) | 409 | 2003 | `clientReference` |
| Authentication failure (any reason; the reason is only logged) | 401 | 3001 | — |
| `Authorization` category (not produced yet) | 403 | 3001 | — |
| GET: transaction unknown or not visible to the channel | 404 | 2001 | `ransysTransactionId` |
| GET: malformed path UUID | 400 | 2001 | `ransysTransactionId` |
| Rate limit (hook, disabled by default) | 429 | 1001 | — |
| `FinancialDependencyUnavailableException`, identity store unavailable during authentication | 503 | 1001 | — |
| `CONFIGURATION_NOT_AVAILABLE`, `FEE_RULE_AMBIGUOUS` (fail closed on configuration) | 503 | 5001 | — |
| Any other error category (`CONCURRENCY_CONFLICT`, `PERSISTED_STATE_INVALID`, internal) and unhandled exceptions | 503 | 1001 | — |

Rationale for the choices:
- The original's state and refund limits are "canonical validation failure before normal transaction processing" (OpenAPI `BadRequest`): no transaction is created and the provider is not called. Keeping 409 exclusively for the duplicate-reference conflict preserves its documented meaning for client retry logic.
- 503 is used for every failure where nothing was committed or sent, so the client can safely retry with the same `clientReference` (idempotency). No 500 is emitted; error bodies never contain exception text.
- The 400 on GET for a malformed UUID follows handoff §28 and the plan; it is an undeclared response in the YAML (see Consequences).
- `ApiErrorResponse.correlationId` is the ASP.NET Core request trace identifier.

## Consequences
- TODO / Architecture Decision Required (Response Code Catalog v1): distinct codes for not found, rate limited, forbidden, original-state rejections, refund limit, wallet missing, provider capability missing, configuration unavailable and dependency unavailable (instead of reusing 1001 / 2001 / 5001); replay could use the PRD's `3003 REPLAY_DETECTED` once approved.
- TODO (OpenAPI v1.0.x, additive): declare `400` on `GET /api/v1/transactions/{ransysTransactionId}`.
- Changing any row of the table is a client-visible change and needs a new ADR.
