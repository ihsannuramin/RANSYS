# ADR-010 — Refund Posting Inputs

**Status:** Accepted; the fee-refund policy is decided by ADR-014 (2026-09-27).

## Context
Ledger Posting Rule Matrix OP-10/11/12 refund postings debit "PROVIDER_RECEIVABLE / REFUND_CLEARING" and may reverse fee revenue.

## Existing RANSYS rule
A refund is a child transaction with `original_transaction_id`; the sum of successful refunds must not exceed the refundable amount; fee refundability is business policy.

## Technical issue
Neither the debit account nor the fee-refund policy is fixed.

## Options
1. Ledger service decides the fee refund.
2. Caller supplies explicit principal and fee amounts.

## Recommended option
Option 2. `PostRefund` takes explicit `principalAmount` and `feeAmount` (fee may be zero). Principal debits `PROVIDER_RECEIVABLE` of the original transaction's actual provider; fee debits `RANSYS_FEE_REVENUE`. The refund use case decides the fee amount.

The fee-refund policy itself remains **TODO / Architecture Decision Required** and is not implemented in Phase 1.

## Consequences
The ledger service stays policy-free; policy is added later in the refund use case.
