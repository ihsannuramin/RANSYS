# ADR-019 — VOID Is Scaffolded and Fails Closed

**Status:** Accepted (interim) — product owner decision, 2026-09-27. VOID financial semantics remain **TODO / Architecture Decision Required**.

## Context
The OpenAPI v1 and the Provider Adapter Contract v1 expose VOID (`VoidAsync`, capability `VOID`, ADR-017/018). The Ledger Posting Rule Matrix and the State Transition Matrix define no VOID postings or original-side transitions.

## Existing RANSYS rule
- VOID is an explicit capability, never mapped to REVERSAL or REFUND (ADR-017).
- Reversals and refunds are child transactions that never overwrite the original while they run (ADR-012, ADR-023).
- Anything non-deterministic becomes a reconciliation exception; money never moves without a defined posting rule.

## Technical issue
A confirmed VOID could mean "release the reservation", "compensate the posting" or "cancel before settlement", depending on the provider and product. Choosing one silently would define a new ledger sequence.

## Options
1. Treat VOID as a reversal (release / compensate). Rejected: contradicts ADR-017.
2. Reject VOID requests entirely until designed.
3. Accept VOID as a child transaction and send it to the provider, but on success change nothing financially and flag the original for manual review.

## Recommended option
Option 3 (interim, decided).
- A VOID is a child transaction (type VOID, own id, `original_transaction_id` required, own idempotency and attempts; primary attempt type `VOID`, which follows the "no new request after a possible send" rule).
- `Transaction.AuthorizeVoid()` allows the same original states as `AuthorizeReversal()` (reserving transaction PENDING/IN_DOUBT + RESERVED, or SUCCESS + POSTED); otherwise `VOID_NOT_ALLOWED`.
- `Transaction.RecordVoidConfirmed(voidTransactionId, ctx)` on the original never changes processing or financial status and returns `LedgerAction.None`. It sets reconciliation to EXCEPTION with reason `VOID_CONFIRMED_REQUIRES_REVIEW` (same path as a conflicting provider result, `TransitionKind.ConflictRecorded`) and is idempotent once the exception exists.

## Consequences
- A successful VOID leaves the original's reservation or posting in place until an operator resolves the exception through a controlled operation (reversal, refund or adjustment with maker-checker).
- The final VOID semantics need a new ADR that defines the ledger postings and original-side transitions.
