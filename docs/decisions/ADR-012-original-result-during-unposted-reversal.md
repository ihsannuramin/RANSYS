# ADR-012 — Original Result Arriving While a Reversal of an Unposted Transaction Is Pending

**Status:** Accepted and implemented — product owner decision, 2026-09-27 (Milestone 11d; the interim option below is removed)

## Context
A payment goes IN_DOUBT (reservation held), then a reversal is started: `processing = REVERSAL_PENDING`, `financial = RESERVED` (State Transition Matrix PS-09 case A). While the reversal is in flight, a definitive result for the **original** request arrives (callback, status check, advice or reconciliation).

## Existing RANSYS rule
- Reversal success with an active reservation releases the hold (PS-10, OP-08).
- A reversal decline never makes the original FAILED (§18).
- The processing diagram (§6) has no edge from REVERSAL_PENDING to SUCCESS or FAILED for the original result. ADR-003 adds REVERSAL_PENDING → SUCCESS only for a declined reversal of a *posted* payment.

## Technical issue
Two truths are pending at once: the original outcome and the reversal outcome. Examples:
- The original SUCCESS arrives while the reversal is pending. If the reversal later succeeds, the correct ledger effect is POST followed by a compensating REVERSAL (OP-03 + OP-09), not just a release (OP-08).
- The original FAILED arrives while the reversal is pending. The reservation should be released, but the reversal attempt is still open at the provider.

The documents do not define the state or ledger sequence for these interleavings.

## Options
1. Reject the original result (no mutation), record it in attempt/callback history, and resolve through the reversal outcome and reconciliation.
2. Apply the original result immediately: SUCCESS → post and move financial to REVERSAL_PENDING; FAILED → release and treat the reversal as moot.
3. Queue the original result and re-evaluate once the reversal result is known.

## Recommended option
Option 1 as the **interim** behavior (implemented): `Transaction.CompleteSuccess` / `CompleteFailure` return `INVALID_STATE_TRANSITION` for `REVERSAL_PENDING + RESERVED`, with no state or ledger change. This is fail-safe: the reservation stays held and no money moves until a defined path resolves it. Reversal decline returns the transaction to IN_DOUBT (ADR-003), where the original result is then accepted normally.

The final behavior (option 2 or 3) needs architecture review because it defines a new ledger sequence.

## Consequences
In this rare interleaving, recovery waits for the reversal result or for reconciliation. The rejected original result must still be persisted in attempt/callback history by the application layer (M7), so no evidence is lost.

## Decision (2026-09-27)
- A reversal is modeled as a **child transaction** with its own `ransys_transaction_id` and `original_transaction_id` (transaction type REVERSAL), with its own idempotency, attempts and state history.
- Starting a reversal **does not overwrite** the original transaction's processing state. The original keeps its own truth (e.g. IN_DOUBT, SUCCESS) while the reversal child is processed.
- The current reject/no-mutation behavior for original results arriving during a reversal stays in place **only until this refactor is completed**.

## Consequences of the decision
- The interleaving that motivated this ADR disappears: an original result is applied to the original normally while the reversal child is pending. When the reversal child succeeds, the financial effect is chosen from the original's financial state **at that moment** (active reservation → release; posted → compensating reversal).
- ADR-003 (REVERSAL_PENDING → SUCCESS on the original) is superseded: a declined reversal only fails the child and leaves the original untouched.
- The original no longer uses REVERSAL_PENDING for the reversal flow. The value stays in the enum and in the DDL CHECK (no destructive schema change).

## Implementation note (Milestone 11d)
- `ReversalService.StartAsync` locks the original, builds a REVERSAL child (same merchant/channel/product/amount, own client reference and idempotency, routed to the original's actual provider, which must have `supports_reversal`), and inserts it through `IdempotencyService.ClaimAsync`. The original is not modified.
- At most one non-FAILED reversal per original: checked under the original's row lock, backed by the partial unique index `ux_transactions_one_open_reversal` (migration `0006`).
- `TransactionFinalizationService` locks parent before child. When the child reaches SUCCESS, `Transaction.ApplyReversalConfirmed` moves the original to REVERSED in the same database transaction: an active reservation is released (`TX:<original>:REVERSAL_RELEASE`); a posted payment is compensated (`TX:<original>:REVERSAL:<child>`). An already FAILED or REVERSED original moves no money.
- A declined child fails alone. REVERSAL attempts belong to the child only. The original no longer uses REVERSAL_PENDING (value kept in enum/DDL).
