# ADR-005 — Attempt Ownership and Crash-Window Safety for RequestSent

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

## Context
Failover is allowed only when RANSYS knows the financial request was not sent (main.md §4, State Transition Matrix §15).

## Existing RANSYS rule
- `transaction_attempts.request_sent` is safety-critical.
- Adapters have no wallet/ledger credentials (ERD v1.1 §40–41, Architecture Spec §16).
- Sequence Pack SD-01 inserts the attempt with `request_sent=false` before the call and shows the *adapter* updating `request_sent=true`.

## Technical issue
1. If Core crashes after sending but before recording the outcome, the persisted `request_sent=false` falsely looks safe, which could lead to a double purchase through failover.
2. SD-01 gives the adapter a database write, which conflicts with the adapter isolation rule.

## Options
1. Trust `request_sent=false` as persisted.
2. Treat an attempt without a recorded outcome as unknown.

## Recommended option
Option 2:
- Only Transaction Core writes `transaction_attempts`. Adapters report `RequestSent` / `TransportStatus` in `ProviderTransportResult`; Core persists them.
- Failover is permitted only when the attempt has an explicitly recorded outcome proving non-delivery: `transport_status = 'NOT_SENT'`, or `CONNECTION_ERROR` with `request_sent = false` reported by the adapter.
- An attempt still in its initial state after a restart (no recorded outcome) is treated as **possibly sent → IN_DOUBT**: reservation held, recovery via status check, reversal or reconciliation.

## Consequences
Slightly more IN_DOUBT cases after crashes, and never a double financial request. The recovery worker must scan for outcome-less attempts.

## Implementation note (Milestone 7)
- Migration `0004` adds `core.transaction_attempts.outcome_recorded_at` (expand). A started attempt is inserted **before** the provider call and stored pessimistically as `request_sent = true`, `transport_status = 'SENT'`, `outcome_recorded_at = NULL`, so any reader that ignores the marker still sees "possibly sent". Recording the outcome sets the real values and the marker exactly once (`WHERE outcome_recorded_at IS NULL`); the row is immutable afterwards.
- `Transaction.AuthorizeAttempt` enforces: attempts target the current routed provider; a new PAYMENT/REFUND request is allowed only while PROCESSING and only if every earlier financial request proves not-sent; STATUS_CHECK/ADVICE only while the outcome is open; REVERSAL only while REVERSAL_PENDING.
- `AttemptResolution.Classify` maps network truth + adapter outcome to NotSent / Success / Failed / Pending / InDoubt. Only an explicit, consistent provider response is definitive; everything else is InDoubt.
- `AttemptRecoveryService` closes outcome-less attempts older than a minimum age (longer than the provider timeout) with an explicit unknown outcome (`request_sent = true`, TIMEOUT, metadata `extension.recovery.outcomeSource = SYSTEM_RECOVERY`) and moves the transaction to IN_DOUBT with the reservation held. It never regresses a transaction that another path already resolved.
