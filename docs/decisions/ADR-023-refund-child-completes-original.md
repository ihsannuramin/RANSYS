# ADR-023 — A Refund Child Completes the Original Directly

**Status:** Proposed — implemented end to end: the domain (Milestone 12a), the refund use case (`ChildTransactionService`, `TransactionFinalizationService.ApplyRefundToOriginalAsync`, Milestone 12d), and the merchant API refund endpoint (Milestone 12e) all call this path. Decision status is unchanged pending product-owner acceptance; implementation completeness and decision acceptance are tracked separately.

## Context
State Transition Matrix PS-11..PS-13 describe refunds on the original: SUCCESS → REFUND_PENDING → PARTIALLY_REFUNDED / REFUNDED (financial POSTED → REFUND_PENDING → …). ADR-012 made reversals child transactions that never overwrite the original while they run. Refunds are also child transactions (`original_transaction_id`, State Transition Matrix §19).

## Existing RANSYS rule
- Refund postings go only through `ILedgerPostingService.PostRefundAsync` (key `TX:<original>:REFUND:<ref>`), with maker-checker approval, and total refunds never exceed the posted amount.
- Refund fee amounts come only from `RefundFeeCalculator` over the original's captured fee components (ADR-014).
- Lock order parent → child (ADR-012).

## Technical issue
With REFUND_PENDING on the original, a running refund overwrites the original's processing truth, and several concurrent partial refunds would fight over one pending flag. This is the same problem ADR-012 solved for reversals.

## Options
1. Keep PS-11..PS-13 literally: the original goes REFUND_PENDING while the refund child runs.
2. Extend the ADR-012 principle: the original is not touched while the refund runs; when the refund child succeeds the original moves directly to PARTIALLY_REFUNDED or REFUNDED.

## Recommended option
Option 2.
- `Transaction.AuthorizeRefund()`: the original must be a reserving type in SUCCESS + POSTED or PARTIALLY_REFUNDED + PARTIALLY_REFUNDED; otherwise `REFUND_NOT_ALLOWED`. The amount limit stays in the ledger.
- `Transaction.ApplyRefundCompleted(refundTransactionId, fullyRefunded, ctx)`, in the same DB transaction as `PostRefundAsync` (executed by the caller; the method returns `LedgerAction.None`):
  - SUCCESS + POSTED → PARTIALLY_REFUNDED + PARTIALLY_REFUNDED (`fullyRefunded = false`) or REFUNDED + REFUNDED (`true`);
  - PARTIALLY_REFUNDED → REFUNDED + REFUNDED when `fullyRefunded`; another partial refund is `NoChange` (no self edge);
  - REFUNDED → `NoChange`; any other state → `INVALID_STATE_TRANSITION`.
- Transition tables gain processing SUCCESS → PARTIALLY_REFUNDED / REFUNDED and financial POSTED → PARTIALLY_REFUNDED / REFUNDED. The REFUND_PENDING edges stay declared (no destructive change) but are unused.
- Aggregate invariant: processing PARTIALLY_REFUNDED ⇔ financial PARTIALLY_REFUNDED, processing REFUNDED ⇔ financial REFUNDED.

## Consequences
- REFUND_PENDING never appears on the original (and so never in the API `transactionStatus`). The refund child carries its own PROCESSING / IN_DOUBT / SUCCESS / FAILED truth; a failed refund child leaves the original untouched.
- A second partial refund leaves no status history row on the original; the evidence is the child transaction and its ledger journal.
- Needs architecture review because it amends PS-11..PS-13.
