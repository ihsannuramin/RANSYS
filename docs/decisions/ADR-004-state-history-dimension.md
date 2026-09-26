# ADR-004 — State History Across Four Status Dimensions

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

## Context
Every real transition writes `core.transaction_state_history` (State Transition Matrix §5, §59).

## Existing RANSYS rule
Four orthogonal status dimensions: processing, financial, reconciliation, settlement.

## Technical issue
The history table has a single `previous_status` / `new_status` pair and no dimension column, so values that exist in two dimensions (`REVERSAL_PENDING`, `REFUNDED`, `PENDING`, `ADJUSTED`) are ambiguous.

## Options
1. Record processing transitions only.
2. Prefix status values (`FINANCIAL:RESERVED`).
3. Expand migration: add nullable `status_dimension varchar(32)` with a CHECK of `PROCESSING`, `FINANCIAL`, `RECONCILIATION`, `SETTLEMENT`.

## Recommended option
Option 3, as an **expand** migration applied after the v1.1 baseline scripts. One history row per changed dimension; all rows of one transition share `transaction_attempt_id`, `reason_code` and `created_at`. Rows written before the column existed are interpreted as `PROCESSING`.

## Consequences
A small, backward-compatible deviation from DDL v1.1 (additive nullable column), documented in the migration script header.
