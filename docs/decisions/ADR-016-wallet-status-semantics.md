# ADR-016 — Wallet Status Semantics

**Status:** Accepted — product owner decision, 2026-09-27 (freeze/unfreeze/close operations pending)

## Context
DDL v1.1 defines wallet status `ACTIVE`, `FROZEN`, `CLOSED` without behavior. Milestone 5 implemented an interim rule: no new debits unless ACTIVE.

## Decision
| Status | Behavior |
|---|---|
| `ACTIVE` | Normal operation. |
| `FROZEN` | Blocks **new financial consumption / reservation**. Still allows finalization, release, reversal, refund and reconciliation of **existing** transactions. |
| `CLOSED` | Terminal. Closing requires zero balances (ledger, available, reserved), no active reservation, and no unresolved financial transaction. |

## Consequences
- Ledger: reserve and debit adjustments require ACTIVE; commit, release, compensation and refund credits are allowed while FROZEN; nothing is allowed on CLOSED, and the close preconditions guarantee nothing is left to finalize.
- A controlled status-change operation (freeze, unfreeze, close) enforces the preconditions and is audited. CLOSED can never be reopened.
- A top-up is not consumption, so it is accepted on a FROZEN wallet.
