# ADR-014 — Refund Fee Policy per Fee Component

**Status:** Accepted — product owner decision, 2026-09-27 (implementation pending)

## Context
ADR-010 left the fee-refund policy open. A refund must decide how much of the originally charged merchant fees to return.

## Existing RANSYS rule
- Fee components carry a charged amount, an accounting amount and a refundable flag (Canonical Data Model §15, DDL `transaction_fee_components.refundable boolean`).
- Financial terms are fixed at reservation; finalization never uses current configuration (Ledger Posting Rule Matrix §48–49).
- Total refunds never exceed the refundable original amount (Ledger Posting Rule Matrix §22).

## Decision
- Refundability is defined **per original fee component**, captured on the original transaction.
- Supported policies: `NONE`, `PRO_RATA`, `FULL`. **Default is `NONE`.**
- Refund fee amounts are always derived from the original transaction's captured fee components. They are **never recalculated with current fee configuration**.

## Consequences
- Expand migration: `transaction_fee_components.refund_policy varchar(16) NOT NULL DEFAULT 'NONE'` with a CHECK constraint. The existing boolean `refundable` is kept for compatibility (`refundable = refund_policy <> 'NONE'`).
- The domain `FeeComponent` carries a `RefundPolicy`; a refund-fee calculator computes the fee part of each refund from the original components and the refunds already posted.
- Open details to confirm before implementation: behavior of `FULL` on partial refunds, and the rounding rule for `PRO_RATA`.
