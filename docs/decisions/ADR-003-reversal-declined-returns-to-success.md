# ADR-003 — Declined Reversal Returns Processing Status to SUCCESS

**Status:** Superseded by ADR-012 (implemented in Milestone 11d): a declined reversal only fails the reversal child; the REVERSAL_PENDING → SUCCESS edge was removed.

## Context
A reversal is requested on a SUCCESS + POSTED transaction and the provider definitively declines it.

## Existing RANSYS rule
State Transition Matrix §18: processing "should return/remain SUCCESS", financial remains POSTED. The financial diagram (§23) has `REVERSAL_PENDING -> POSTED`.

## Technical issue
The processing diagram (§6) and reference table (§73) have no `REVERSAL_PENDING -> SUCCESS` edge.

## Options
1. Leave the transaction in REVERSAL_PENDING indefinitely.
2. Add a guarded `REVERSAL_PENDING -> SUCCESS` edge.

## Recommended option
Option 2, allowed **only** when the financial status is `REVERSAL_PENDING` coming from `POSTED` and the reason code is `REVERSAL_DECLINED`. Financial returns `REVERSAL_PENDING -> POSTED`.

If the original truth was never proven (financial `RESERVED`), a declined reversal returns processing to `IN_DOUBT` (existing edge) — never `FAILED`.

## Consequences
The domain transition table contains one edge not drawn in the §6 diagram; it is covered by dedicated tests.
