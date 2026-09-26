# ADR-008 — Ledger Operation Type Names

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

## Context
`ledger.ledger_transactions.operation_type` classifies every journal.

## Existing RANSYS rule
Ledger Posting Rule Matrix OP-13 uses `operation_type = ADJUSTMENT`; §32 recommends `ADJUSTMENT_CREDIT` / `ADJUSTMENT_DEBIT`.

## Technical issue
The naming is inconsistent within one document, and the DDL has no CHECK on `operation_type`.

## Options
1. Single `ADJUSTMENT` type.
2. The §32 list.

## Recommended option
Option 2: `TOPUP`, `RESERVE`, `POST`, `RELEASE`, `REVERSAL`, `REFUND`, `ADJUSTMENT_CREDIT`, `ADJUSTMENT_DEBIT`, `SETTLEMENT_CLEAR`, `REFUND_CLEAR`.

OP-08 (reversal while reserved) uses `RELEASE` with posting key `TX:<id>:REVERSAL_RELEASE` (ADR-001).

## Consequences
Credit and debit adjustments can be told apart without reading their entries.
