# ADR-009 — Idempotency Record Expiry

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

## Context
24-hour idempotency window scoped to channel + client reference (PRD §11, Architecture Spec §7).

## Existing RANSYS rule
DDL v1.1: partial unique index `ux_idempotency_active_reference (channel_id, client_reference) WHERE active = true`, plus `expires_at` / `expired_at`.

## Technical issue
Nothing specifies who sets `active = false`. Without that, a reference could never be reused, or the window would not be enforced.

## Options
1. Lazy expiry at check time only.
2. Periodic sweep only.
3. Both.

## Recommended option
Option 3:
1. **Lazy expiry** at check time, inside the same database transaction as the new insert: if the active record has `expires_at <= now`, set `active = false, expired_at = now` before inserting the new record.
2. **Periodic sweep** background worker that expires records in bounded batches.

Concurrent same-reference inserts rely on the partial unique index; a unique violation is treated as "duplicate — reload and compare fingerprint".

## Consequences
Correctness does not depend on the sweep running on time; the sweep only keeps the index small.

## Implementation note (Milestone 6)
- `IdempotencyService.ClaimAsync` checks the active claim (expiring it lazily when due), then, under a savepoint, creates the transaction row and inserts the claim. A PostgreSQL unique violation aborts the enclosing transaction, so the loser of a concurrent race rolls back to the savepoint (removing its transaction row), re-reads the winner's claim and returns either the existing transaction or `DUPLICATE_REFERENCE_CONFLICT`.
- A fingerprint with a different algorithm version cannot prove "same payload" and is treated as a conflict (fail closed, ADR-006).
- The sweep is `IdempotencyExpiryWorker` in `Ransys.Workers` (`FOR UPDATE SKIP LOCKED`, bounded batches, section `Ransys:IdempotencyExpiry`).
