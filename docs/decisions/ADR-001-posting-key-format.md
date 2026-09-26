# ADR-001 — Ledger Posting Key Format

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

## Context
Every ledger posting needs a unique `posting_key` (`UNIQUE` on `ledger.ledger_transactions.posting_key`) so retries never double-post.

## Existing RANSYS rule
- Ledger Posting Rule Matrix §6 / OP-xx: `TX123:RESERVE`, `TX123:POST`, `TX123:REVERSAL_RELEASE`, `ADJ:<ref>`.
- Architecture Spec / ERD: `ADJUSTMENT:ADJ001`.
- main.md §3.5: `TX:<transaction-id>:RESERVE`, `ADJUSTMENT:<reference>`.

## Technical issue
Three incompatible spellings. A posting key must be deterministic across all code paths or idempotency silently breaks.

## Options
1. Ledger Matrix format.
2. main.md format, extended for OP-08.

## Recommended option
Option 2. Canonical formats:

| Operation | posting_key |
|---|---|
| Reserve | `TX:<txId>:RESERVE` |
| Payment success | `TX:<txId>:POST` |
| Release | `TX:<txId>:RELEASE` |
| Reversal while reserved (OP-08) | `TX:<txId>:REVERSAL_RELEASE` |
| Reversal after post (OP-09) | `TX:<txId>:REVERSAL:<reference>` |
| Refund | `TX:<txId>:REFUND:<reference>` |
| Top-up | `TOPUP:<reference>` |
| Adjustment | `ADJUSTMENT:<reference>` |

`<txId>` is the lowercase canonical UUID of `ransys_transaction_id`. Keys are built only by one `PostingKey` factory in `Ransys.Ledger`.

## Consequences
The Ledger Matrix examples are illustrative only; code and tests use this table.
