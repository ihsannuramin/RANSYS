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

## Detailed rules (product owner decision, 2026-09-27)
- **`FULL`:** the whole charged amount of the component is refunded with the refund that brings the cumulative refunded principal up to the original principal. Earlier partial refunds return nothing for that component.
- **`PRO_RATA`:** cumulative and truncated. Cumulative fee refund = `truncate(charged × cumulative refunded principal ÷ original principal)` to the currency scale; the fee part of this refund = cumulative fee refund − fee already refunded for that component. This never over-refunds, and the total equals the charged amount exactly when the principal is fully refunded.
- **`NONE`:** nothing is ever refunded for the component.
