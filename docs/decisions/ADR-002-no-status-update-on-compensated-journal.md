# ADR-002 — Compensated Journals Are Never Updated

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

## Context
OP-09 reversal creates a compensating journal for a posted payment.

## Existing RANSYS rule
The ledger is immutable; "original journal unchanged" (main.md §15, Ledger Matrix §34, ERD v1.1 §21).

## Technical issue
DDL v1.1 allows `ledger_transactions.status = 'REVERSED_BY_COMPENSATION'`, which implies updating the original journal row.

## Options
1. Update the original status to `REVERSED_BY_COMPENSATION`.
2. Never update; derive "compensated" by querying `compensates_ledger_transaction_id`.

## Recommended option
Option 2. Application code never issues `UPDATE` on `ledger.ledger_transactions`. The status value stays in the CHECK constraint (no schema change) but is unused.

## Consequences
"Is compensated?" is a lookup, not a column read. A future contract migration may remove the unused value.
