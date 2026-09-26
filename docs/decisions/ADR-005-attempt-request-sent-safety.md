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
