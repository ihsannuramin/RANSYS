# ADR-027 — Latest Provider Result Projection

**Status:** Accepted — correctness fix restoring an already-implied guarantee (a later/final async report must not
lose its business evidence), not a new open product decision.

## Context
An external architecture re-review of commit `1fc9d10` (`review/RANSYS_Architecture_ReReview_1fc9d10.md`, RR1/RR2)
found two P1 regressions in the R1/R2 callback fix landed at `4e8edf5`:

- **RR1 (lock order):** `ProviderCallbackSink.SubmitAsync` locked the callback's own transaction id
  (`forUpdate: true`) *before* calling `TransactionFinalizationService.ApplyAsync`. For a refund/reversal/void
  callback that id is the **child**'s, not the original's — but `ApplyAsync` itself locks the child's original
  (parent) first, then the child (ADR-012/023/019). The sink therefore took child → parent while the sync
  completion path (`TransactionProcessingService`) takes parent → child on the same rows: a genuine PostgreSQL
  deadlock (40P01) between two real concurrent code paths.
- **RR2 (evidence loss):** the sink only called `TransactionAttemptService.RecordOutcomeAsync` when
  `!attempt.IsOutcomeRecorded`. That guard is correct (ADR-005: an attempt's outcome, once recorded, is immutable),
  but once *any* outcome was already recorded for an attempt — a sync TIMEOUT, a PENDING callback, or
  `AttemptRecoveryService`'s synthetic outcome — a later, final callback (e.g. the real SUCCESS with a concrete
  provider reference/RRN/business data) still finalized the transaction's status and ledger effect while its
  evidence was recorded nowhere. `ProviderRequestFactory.OriginalProviderReferences.From` (child provider requests),
  `PostgresTransactionQuery` (GET's STAN/RRN) and `TransactionProcessingService`'s idempotent replay branch all read
  only `transaction_attempts`, so none of them could ever see it.

## Existing RANSYS rule
- ADR-005: `transaction_attempts` is immutable operational history once an outcome is recorded; nothing may
  overwrite it. This rule is correct and stays untouched.
- ADR-012/023/019 and `TransactionFinalizationService`'s own docstring: lock order for a child result is parent
  before child, always, from every source.
- CLAUDE.md, Domain conventions: "`Metadata` is only for provider/product-specific extensions. Fields that come
  into common use get promoted to canonical fields." The provider's evidence for the transaction's actual business
  result is exactly such a field, not an internal extension.
- ADR-026: the business-facing response data of the attempt that first resolves a transaction is already persisted
  for replay. ADR-026 does not cover a *second*, later report for an attempt that already has a recorded outcome —
  that gap is this ADR.

## Technical issue
Two problems, one process fix and one data-model fix, both already verified in code:
1. Any code path that locks a transaction row before handing off to `TransactionFinalizationService.ApplyAsync`
   (which locks parent-then-child again) can take locks in the wrong relative order against a concurrent caller
   that only ever goes through `ApplyAsync` directly.
2. `transaction_attempts` intentionally has no field left to hold a *second* report once the first outcome is
   recorded, and correctly so (ADR-005 immutability). There was no other place to keep a later report's evidence,
   so it was silently dropped by every reader that only looks at `transaction_attempts`.

## Options
1. Keep locking in the sink but reorder it to peek unlocked, conditionally lock the parent, then lock the child,
   duplicating `TransactionFinalizationService`'s own lock sequence. Rejected: two independent implementations of
   the same lock order drift apart over time (this is exactly how RR1 was introduced fixing R1); the callback path
   and every other caller of finalization would still be reading/writing attempt outcomes through two different
   code paths, a repeat risk for RR2-shaped bugs.
2. Relax ADR-005 and let a later callback overwrite `transaction_attempts`' recorded outcome. Rejected: outright
   contradicts ADR-005 and the immutability invariant `AttemptRecoveryService`/reconciliation depend on; a
   transport-level record must stay exactly what was received when it was received.
3. Delete all locking/correlation/evidence-recording from `ProviderCallbackSink`; make
   `TransactionFinalizationService.ApplyAsync` — which already gets the lock order right — the single place that
   locks a transaction for a provider result, correlates a callback's provider to its attempt under that lock, and
   records the attempt outcome if it doesn't have one yet. Add a new, explicitly-mutable projection on the
   `Transaction` aggregate for the transaction's latest provider evidence, fed from any source, read by GET/replay/
   child-request code in *preference to* (never instead of) the existing per-attempt fallback.

## Recommended option
Option 3.
- `ProviderCallbackSink.SubmitAsync` no longer locks anything itself: it only calls
  `ProviderResultInterpreter.Interpret`, then builds a `ProviderResultCommand` carrying the reporting provider
  (`ExpectedProvider`) and the interpreted `AttemptOutcome` (`Evidence`), and calls
  `TransactionFinalizationService.ApplyAsync`. It no longer depends on `ITransactionRepository` or
  `ITransactionAttemptStore`/`TransactionAttemptService` at all.
- `TransactionFinalizationService.ApplyAsync` gains the correlation step, inserted right after it locks the target
  transaction (so it happens under the same lock the rest of the method already uses, never before): if
  `command.ExpectedProvider` is set, it is compared against `transaction.Routing.CurrentProvider` (a mismatch is
  `ErrorCodes.ProviderMismatch`, a fail-closed rejection so a callback from a superseded/failed-over provider is
  never misapplied), the transaction's attempts are loaded, and the highest-numbered attempt on that provider is
  used as the effective `AttemptId` for the transition. If that attempt has no recorded outcome yet, `Evidence` is
  recorded on it through the existing `TransactionAttemptService.RecordOutcomeAsync` (unchanged ADR-005 semantics).
  Because the callback path and the sync completion path (`TransactionProcessingService`) now both go through this
  one method, and only this method decides lock order, they cannot take locks in different orders against each
  other (RR1).
- `Transaction` gains `LatestProviderResult` (`TransactionResultProjection(AttemptOutcome Evidence, ChangeSource
  Source, DateTimeOffset RecordedAt)`) and `RecordLatestProviderResult(evidence, source, recordedAt)`. This is a
  plain, always-overwritable field — explicitly **not** a status dimension: it goes through no
  `TransactionTransitions` table, produces no `StateChange`/history row, and changing it never fails. Whenever
  `ApplyAsync` has evidence for the transaction it just transitioned (`command.Evidence is not null`), it calls
  `RecordLatestProviderResult` right before persisting, so the write is in the same database transaction as the
  status/ledger effect it describes (RR2).
- New additive columns on `core.transactions` (migration `0010`, expand-only, `0001` untouched):
  `latest_result_provider_reference varchar(128)`, `latest_result_provider_stan varchar(32)`,
  `latest_result_provider_rrn varchar(64)` (same lengths as `AttemptOutcome`'s own limits),
  `latest_result_data jsonb`, `latest_result_source varchar(32)`, `latest_result_recorded_at timestamptz`, all
  `NULL` until a projection is ever recorded. `TransactionStore`/`TransactionRowMapper` read/write them the same
  way `0009`'s `response_data` is handled, and rebuild a minimal `AttemptOutcome` (only the fields this read path
  needs: provider reference/STAN/RRN/data) on load.
- Readers prefer the projection over the per-attempt fallback, never replacing it:
  `ProviderRequestFactory.OriginalProviderReferences.From(original, originalAttempts)` returns the original's
  `LatestProviderResult` when it has any reference, else falls back to scanning attempts exactly as before;
  `PostgresTransactionQuery`'s STAN/RRN columns become `COALESCE(t.latest_result_provider_*, a.provider_*)`;
  `TransactionProcessingService`'s replay branch prefers
  `prepared.Transaction.LatestProviderResult?.Evidence.Data` over the latest resolved attempt's own `Data`.
- Nothing changes for any existing caller of `TransactionFinalizationService.ApplyAsync`/`ProviderResultCommand`
  that does not pass `ExpectedProvider`/`Evidence` (both new, optional, trailing, default `null`): the sync
  completion path, `AttemptRecoveryService`, and internal reversal/refund/void completion are unaffected.

## Consequences
- The callback path and the sync completion path now share one lock-acquisition routine, so they cannot deadlock
  against each other on the same transaction (RR1 closed).
- A later/final async report is never silently dropped again, even after an earlier outcome (timeout, PENDING,
  synthetic recovery) already made `transaction_attempts` immutable for that attempt (RR2 closed). `GET`, replay and
  child provider requests read the latest known evidence, not necessarily the first-recorded one.
- `transaction_attempts` keeps its ADR-005 immutability guarantee unconditionally; this ADR adds a projection
  alongside it, not a way around it.
- The projection is fed by any caller of `TransactionFinalizationService.ApplyAsync` that supplies `Evidence` on its
  `ProviderResultCommand` — today that is `ProviderCallbackSink` (every callback) and `TransactionProcessingService`
  (both the sync completion path in `RecordInSessionAsync` and its `MarkInDoubtAsync` fallback); status check,
  advice and reconciliation results can join the same guarantee later, with no further schema change.

### Precedence: accepted result vs. conflict evidence (RR3 follow-up)
An external re-review (`review/RANSYS_Architecture_Review_b9616fb.md`, T1/T2) found that "always overwrite" was too
broad: `TransactionFinalizationService.ApplyAsync` produces three kinds of `TransitionOutcome`
(`TransactionCore/Finalization/TransactionFinalizationService.cs`, `Domain/Transactions/StateTransitions.cs`), and
they do not all deserve the same treatment of the transaction the projection describes:
- **`TransitionKind.Applied`** — a genuinely new accepted transition (first SUCCESS/FAILED, or a state change the
  domain allows). Its evidence, if any, **always updates** `LatestProviderResult`: this is exactly the case ADR-027
  was written for.
- **`TransitionKind.NoChange`** — a duplicate/late report that *agrees* with the already-accepted resolution (e.g. a
  callback confirming a SUCCESS the sync path already recorded, or the reverse). No status/ledger/outbox effect
  follows, but its evidence can still be strictly richer than what is stored (a fuller reference/RRN/business data
  payload) — so it **also updates** `LatestProviderResult`, on its own (no ledger action, no status event; only the
  projection column write, same database transaction).
- **`TransitionKind.ConflictRecorded`** (`Transaction.CompleteSuccess`/`CompleteFailure`'s `RecordConflict` branches)
  — a report that *contradicts* the already-accepted resolution (e.g. FAILED arriving after an accepted
  SUCCESS/POSTED). This only ever moves `ReconciliationStatus` to `EXCEPTION`; the transaction's real financial
  outcome does not change. Its evidence **never updates** `LatestProviderResult`: promoting a contradicting report to
  "the" business result for GET/replay/child requests would let a wrong or malicious later message silently rewrite
  the reference/RRN/data of a transaction that already posted or already failed for real. The conflict itself stays
  fully visible through the reconciliation-exception reason code/description this transition already records; a
  separate conflict-evidence log is out of scope here.

In short: `LatestProviderResult` represents the **accepted** result, not merely the **most recently received** one.
`Applied` and `NoChange` both refine that accepted picture; `ConflictRecorded` never does.
- Out of scope: this does not change what counts as a valid transition, ADR-012/023/019's child semantics, or
  ADR-018's `PROTOCOL_ERROR` handling.

### Merge, not overwrite (U1/U2 follow-up)
A further external re-review (`review/RANSYS_Review_Progress_7dbaf4b.md`, U1/U2) found that RR3's "also updates" for
`Applied`/`NoChange` was itself still a blind unconditional overwrite of the whole projection — `Transaction.
RecordLatestProviderResult(evidence, source, recordedAt)` always replaced every field with whatever the new report
carried, including blank ones. Two concrete regressions followed: a later report with the *same* accepted status but
*less complete* evidence (empty reference/STAN/RRN/data) silently erased a fuller previously-accepted projection
(U1), and a later report with a genuinely *different* provider reference silently replaced the earlier one with no
trace, purely because both said SUCCESS. Readers had the same class of bug: `TransactionProcessingService.Build`'s
`accepted?.Data ?? fallbackData` chain (and the equivalent `PostgresTransactionQuery` `COALESCE`) could not tell "an
accepted projection exists but its own `Data`/STAN/RRN is legitimately empty" from "no accepted projection exists at
all", and fell through to stale attempt-outcome data a later, final, empty-data report was supposed to supersede (U2).

The fix: `RecordLatestProviderResult` is replaced by `Transaction.MergeLatestProviderResult(incoming, source,
recordedAt) : ProviderResultMergeOutcome`:
- No projection yet ⇒ `Recorded`: the incoming evidence becomes the projection outright.
- The incoming report carries no provider identity at all — `ProviderReference`, `ProviderStan` and `ProviderRrn` all
  null, and no non-empty `Data` either — ⇒ `IgnoredNoIdentity`: nothing changes; a duplicate/late report with nothing
  concrete can never erase or dilute an already-known projection.
- Otherwise, `ProviderReference`, `ProviderStan` and `ProviderRrn` are merged **independently, field by field**
  (`MergeIdentityField`, a per-field decision table — never the earlier collapsed
  `ProviderReference ?? ProviderStan ?? ProviderRrn` single-string comparison, which mixed three semantically distinct
  identifiers and both let genuine conflicts through and rejected valid enrichments, see the "Per-field identity
  comparison (V1)" section below):
  - existing null, incoming null → stays null;
  - existing null, incoming non-null → enriches (the incoming value is taken);
  - existing non-null, incoming null → keeps the existing value (a blank incoming field never erases it);
  - existing non-null, incoming non-null and equal → keeps it (no-op);
  - existing non-null, incoming non-null and different → a conflict on that field alone.
  A conflict on *any one* of the three fields makes the whole report `ConflictingIdentity` and changes nothing (no
  cross-field fallback: a shared RRN never excuses a different STAN). Otherwise the merge proceeds: non-identity
  fields fill in blanks the same way as before, and non-empty `Data` replaces the existing payload as a whole (a
  snapshot, never a key-by-key splice — two reports' business payloads are never spliced together, since that could
  silently mix incompatible data) ⇒ `Enriched`.
- `ConflictingIdentity`: nothing on the projection changes. This is a genuinely different provider reference/STAN/RRN
  reported for the same transaction — never silently swapped in.

### Per-field identity comparison (V1) and a durable conflict record (V2)
A further external re-review (`review/RANSYS_Review_Progress_2ff8477.md`, V1–V3) found two problems with the U1/U2
merge above as first implemented:
- **V1:** the identity comparison itself still collapsed `ProviderReference`, `ProviderStan` and `ProviderRrn` into one
  string via `??` before comparing. This both let a genuine conflict through (same reference, but a different STAN/RRN
  silently overwrote the existing ones once the collapsed strings happened to match) and rejected valid enrichment
  (existing `RRN` only, incoming adds a `ProviderReference` under the same `RRN` — the collapsed strings differ,
  falsely flagged as conflicting). The fix above (`MergeIdentityField` applied per field) is what closed this.
- **V2:** `ConflictingIdentity` used to be handled nowhere: `TransactionFinalizationService.ApplyAsync`'s `NoChange`
  branch checked only for `Recorded`/`Enriched` and otherwise fell through unchanged, so the conflict was detected but
  never observable — no persistence, no history row, no outbox event; `ProviderCallbackSink` acked it as a plain
  `DUPLICATE`, indistinguishable from a harmless repeat. The fix: `ConflictingIdentity` now calls
  `Transaction.RecordProviderEvidenceConflict`, which reuses the same reconciliation-exception mechanism a
  status-level contradiction already uses (`ReasonCodes.ConflictingProviderEvidence`, distinct from the status-level
  `ConflictingProviderResult`; idempotent once `ReconciliationStatus` is already `Exception`). This produces a real,
  durable `TransactionEventTypes.ReconExceptionCreated` outbox event, and the sink acks it with the same
  `ProviderCallbackSink.ConflictRecorded` code a status-level conflict already gets — never `Duplicate`.

`TransactionFinalizationService.ApplyAsync` calls `MergeLatestProviderResult` only in the `NoChange` branch (a
duplicate/late report of a status that is *already* accepted); the post-ledger `Applied` branch still calls
`RecordLatestProviderResult` unconditionally (a genuinely new transition's evidence always replaces the projection
outright, per the "Applied always replaces" rule above) — `ConflictRecorded` (the status-level kind, from
`CompleteSuccess`/`CompleteFailure`'s own contradiction branches) still never touches the projection at all, unchanged
from RR3. In the `NoChange` branch, the transaction row is re-persisted for `Recorded`/`Enriched` (the merge changed
the projection) and also for `ConflictingIdentity` (the reconciliation-exception write), but not for
`IgnoredNoIdentity` (truly nothing changed).

Readers that used to prefer "the projection if any of its fields is non-null, else the fallback" now prefer "the
projection if it exists at all (regardless of which of its own fields are null), else the fallback":
`TransactionProcessingService.Build` first asks whether `transaction.LatestProviderResult` exists; only when it does
not does it fall back to the attempt outcome / caller-supplied fallback data. `PostgresTransactionQuery`'s GET query
changes from `COALESCE(t.latest_result_provider_stan, a.provider_stan)` to a `CASE WHEN t.latest_result_recorded_at
IS NOT NULL THEN t.latest_result_provider_stan ELSE a.provider_stan END` — trusting an existing accepted
projection's own (possibly null) value instead of silently falling back to the older attempt-join value.
`ProviderRequestFactory.OriginalProviderReferences.From(original, originalAttempts)` (V3, corrected) now shares this
*exact same* rule, not an intentionally different one: it falls back to the attempt scan only when the original has
**no projection at all** (a legacy transaction from before ADR-027, or one whose evidence was genuinely never
captured), never merely because the existing projection's own reference/STAN/RRN happen to all be null. An earlier
version of this fix left the child reader on its old "falls back whenever the projection has no reference at all"
rule, reasoning that "a child request just needs some reference to proceed" — that reasoning was itself the bug: it
let a superseded PENDING callback's provisional reference (already immutable on `transaction_attempts`, ADR-005) leak
into a refund/reversal/void child's outgoing provider request even after a later, final, reference-less report was
correctly accepted as the transaction's real outcome. Out of scope, not implemented here: failing closed (or otherwise
deferring the operation) when a provider genuinely needs a reference to address the original and none has been
accepted yet is its own product decision and needs its own ADR.
