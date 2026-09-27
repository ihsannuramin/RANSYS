# ADR-026 — Persist the Latest Attempt's Response Data for Replay

**Status:** Accepted — correctness fix restoring a documented contract guarantee (idempotent replay must return the same business result), not a new open product decision.

## Context
`ProviderResult.Data` (Provider Adapter Contract v1, `Ransys.Adapter.Contracts.V1.ProviderAdapterContracts.cs`) carries business-facing response fields the adapter maps from the provider reply — for example an inquiry's `billAmount`. `ProviderResultInterpreter.Interpret` reads `ProviderResult.Data` only to build the in-memory `TransactionProcessingResult` returned on the *first* response (`TransactionProcessingService.CompleteAsync`, live call path). `AttemptOutcome` (`src/Ransys.Domain/Attempts/AttemptOutcome.cs`) has no field for it, so nothing keyed to the attempt row is persisted, and `PostgresTransactionAttemptStore` has no column to write it to either.

Idempotent replay (`TransactionProcessingService.ReplayAsync`) loads the transaction and its attempt history from the database, takes the latest resolved attempt's `Outcome`, and calls `Build(..., data: NoData, ...)` unconditionally — `NoData` being a hardcoded empty dictionary. A merchant who retries an inquiry or payment within the 24h idempotency window after losing the first HTTP response gets back status `0000`/SUCCESS with `data: {}` instead of the `billAmount` (or other product data) the contract promised on the first response.

## Existing RANSYS rule
- Idempotency (PRD §11, ADR-009): the same channel + client reference + same fingerprint within the 24h window must return the existing transaction's result, not a degraded one.
- CLAUDE.md, Domain conventions: "`Metadata` is only for provider/product-specific extensions. Fields that come into common use get promoted to canonical fields." `Data` is exactly such a field: a stable, contract-documented, business-facing response, not an internal/raw provider extension.
- ADR-005 / `TransactionAttempt`: an attempt's outcome, once recorded, is immutable; nothing may rewrite `outcome_recorded_at` or the columns it already wrote.

## Technical issue
Two related gaps, both in code, already verified:
1. `AttemptOutcome` has no `Data` property at all, so `ProviderResultInterpreter.Interpret` (~line 77–91) cannot attach it to the outcome it builds, and `PostgresTransactionAttemptStore` cannot round-trip something that does not exist on the domain type.
2. Even with the field added in memory, there is no database column to persist it in `core.transaction_attempts`, so a replay after a process restart (a real scenario: idempotency claims are looked up in a fresh DB session, not kept in process memory) still has nothing to read back.

## Options
1. Recompute `Data` on replay by re-deriving it from other persisted attempt columns (e.g. `provider_response_code`/`provider_transaction_status`). Rejected: `Data` is an open, provider/product-defined shape (Provider Adapter Contract v1 §9); there is no general way to reconstruct it from the narrower canonical columns without provider-specific logic in Core, which would violate the adapter boundary.
2. Store `Data` inside the existing `metadata` (`ExtensionMetadata`) column instead of a new column. Rejected: `ExtensionMetadata` enforces namespaced keys (`product.*`/`provider.*`/`extension.*`) and rejects sensitive field names — rules designed for internal/raw provider extension data, not for a business-facing response projection that must be returned to the merchant as-is. Conflating the two purposes would either loosen `ExtensionMetadata`'s validation for everyone or force `Data`'s keys through namespacing rules the OpenAPI contract does not define.
3. Add a new `response_data jsonb NULL` column on `core.transaction_attempts` (expand migration `0009`), a matching optional `AttemptOutcome.Data` property, and thread it through `ProviderResultInterpreter` → `PostgresTransactionAttemptStore` → `TransactionProcessingService.CompleteAsync`'s replay branch.

## Recommended option
Option 3.
- `AttemptOutcome.Create(...)` gains an optional trailing parameter `IReadOnlyDictionary<string, JsonElement>? data = null`, pass-through with no additional validation (the shape is the adapter contract's business, not the domain's). Every existing caller compiles unchanged.
- `ProviderResultInterpreter.Interpret` passes `data: normalized.Data.Count > 0 ? normalized.Data : null` into `AttemptOutcome.Create`. The `Conservative(...)` fallback (used when the adapter returned no result, or the outcome failed domain validation) never carries `Data`: a conservative outcome exists precisely because RANSYS does not actually know what the provider said, so attaching a business payload there would fabricate a result the transaction never earned.
- Migration `0009_transaction_attempt_response_data.sql` adds `response_data jsonb NULL` — additive, no backfill, no constraint. `0001` and every other released script stay untouched.
- `PostgresTransactionAttemptStore.RecordOutcomeAsync` writes `response_data = CAST(@ResponseData AS jsonb)` in the same `UPDATE ... WHERE outcome_recorded_at IS NULL` as every other outcome column, so it inherits the existing immutability guard (an attempt's outcome, once recorded, cannot be overwritten). `GetByTransactionAsync` reads it back and `ToOutcome` deserializes it into `AttemptOutcome.Data`.
- `TransactionProcessingService.CompleteAsync`'s replay branch (`prepared.Attempt is null`) now builds the response from `prepared.LatestOutcome?.Data ?? NoData` instead of the hardcoded `NoData`. Only the **latest resolved attempt's** data is exposed — the same "most recent attempt wins" rule the rest of replay already follows for `ProviderStan`/`ProviderRrn`.
- `Data` is deliberately *not* added to `ExtensionMetadata`/`metadata`: it is a distinct, business-facing projection (CLAUDE.md's "fields that come into common use get promoted to canonical fields" — this is that promotion), while `metadata` remains for provider/product extension fields and interpreter overflow (`extension.adapter.*`).

## Consequences
- A merchant retry of an already-completed inquiry or payment within the idempotency window now returns the same `data` as the original response, matching the OpenAPI contract's implicit "replay returns the current transaction" guarantee (timestamps/status text need not be byte-identical, but business data must not silently disappear).
- `response_data` is additive and nullable; rows written before `0009` simply read back `Data = null`, which replay already treats the same as "no data" (`NoData`).
- No change to `TransactionAttempt` immutability, to the Provider Adapter Contract, or to any released migration script.
- Out of scope: recovery (`AttemptRecoveryService`, R5) and callback-sourced results (R1/R2, already fixed in 13a) follow their own outcome-recording paths, which already go through `RecordOutcomeAsync`/`AttemptOutcome.Create` and therefore pick up `Data` persistence automatically without further changes here.
