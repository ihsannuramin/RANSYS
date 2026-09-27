# RANSYS

Universal transaction switching and processing platform (C# / .NET 10, PostgreSQL).

The design documents in [`docs/`](docs/) are authoritative. Implementation decisions that refine or resolve conflicts in those documents are recorded in [`docs/decisions/`](docs/decisions/).

## Build and test

```bash
dotnet build Ransys.sln
dotnet test Ransys.sln
```

Run a single test project or test:

```bash
dotnet test tests/Ransys.Domain.Tests
dotnet test Ransys.sln --filter "FullyQualifiedName~DependencyRulesTests"
```

### Database-backed tests

Tests that depend on PostgreSQL semantics (row locks, unique posting keys, deferred journal triggers, `SKIP LOCKED`) run against a real PostgreSQL instance, never an in-memory substitute. Configure the connection with:

```bash
export RANSYS_TEST_PG="Host=localhost;Port=5432;Database=RANSYS_PG;Username=postgres;Password=<password>"
```

Default when unset: `Host=localhost;Port=5432;Database=RANSYS_PG;Username=postgres`.


## Running the worker host

`Ransys.Workers` requires the connection string `ConnectionStrings:TransactionDb` and refuses to start without it. Locally it comes from .NET User Secrets (stored outside the repository, loaded in Development):

```bash
dotnet user-secrets set "ConnectionStrings:TransactionDb" "<connection string>" --project src/Ransys.Workers
dotnet run --project src/Ransys.Workers
```

Other environments supply it through the environment variable `ConnectionStrings__TransactionDb`. The host does not run migrations. It runs:

- the transactional outbox worker (`Ransys:Outbox:*`: `BatchSize`, `LeaseDuration`, `PublishTimeout`, `MaxAttempts`, `BaseRetryDelay`, `MaxRetryDelay`, `PollInterval`). The Phase 1 publisher only logs event identities (placeholder until a Backoffice consumer exists). Several instances can run in parallel.
- the idempotency expiry sweep (`Ransys:IdempotencyExpiry:Interval`, `Ransys:IdempotencyExpiry:BatchSize`).

## Solution layout

| Project | Responsibility |
|---|---|
| `Ransys.Domain` | Canonical value objects, strongly typed IDs, Transaction aggregate and state machine. No infrastructure dependencies. |
| `Ransys.Application` | Result/error model, clock, ID generation, correlation, unit-of-work ports. |
| `Ransys.Contracts` | Versioned event envelopes and payloads, provider request/result contracts. |
| `Ransys.Ledger` | Posting rules and `ILedgerPostingService` (reserve, post, release, reversal, refund, top-up, adjustments). |
| `Ransys.TransactionCore` | Transaction use cases: idempotency, reserve/finalize, attempts. |
| `Ransys.Routing` | Priority routing (primary / secondary / fallback). |
| `Ransys.Configuration` | Versioned configuration lookup. |
| `Ransys.Persistence.PostgreSql` | Npgsql + Dapper data access, SQL migrations (DDL v1.1). |
| `Ransys.Infrastructure` | Technical services (UUIDv7, secret provider, raw message storage). |
| `Ransys.Adapter.Contracts` / `Ransys.Adapter.Sdk` | Provider adapter boundary: Provider Adapter Contract v1 (C# + gRPC/protobuf), implemented since Milestone 12b. |
| `Ransys.Workers` | Background host: transactional outbox worker, idempotency expiry sweep. |
| `Ransys.Api` | API host / composition root: OpenAPI v1 merchant endpoints, implemented since Milestone 12e. |

Dependency direction: `Domain ← Application ← Infrastructure / API`, enforced by `tests/Ransys.IntegrationTests/Architecture/DependencyRulesTests.cs`.

## Implementation status (Phase 1)

| Milestone | Status |
|---|---|
| 1 Repository foundation | Done |
| 2 Canonical domain types | Done |
| 3 Transaction aggregate | Done |
| 4 PostgreSQL persistence | Done |
| 5 Ledger posting service | Done |
| 6 Idempotency service | Done |
| 7 Transaction attempts | Done |
| 8 Transactional outbox | Done |
| 9 Routing foundation | Done |
| 10 Concurrency scenarios | Done |
| 12a Reconcile contracts for OpenAPI v1 / Adapter Contract v1 (capability catalog, refund/void domain model) | Done |
| 12b Provider Adapter Contract v1 (`Ransys.Adapter.Contracts`, `Ransys.Adapter.Sdk`) | Done |
| 12c Core provider gateway + callback sink (`TransactionCore/Providers`) | Done |
| 12d Transaction processing orchestration (`TransactionCore/Processing`) | Done |
| 12e Merchant API endpoints (`Ransys.Api`) | Done |
| 12f API/security/contract tests (`tests/Ransys.Api.Tests`) | Done |
| 12g Milestone 12 documentation | Done |
| 13 Architecture review remediation (`review/RANSYS_Architecture_Review_f1fbefe.md`) | Done — see below |

## Phase 1 Definition of Done (main.md §28)

| Criterion | Status | Evidence |
|---|---|---|
| Solution builds successfully | Met | `dotnet build Ransys.sln`: 0 warnings, 0 errors (`TreatWarningsAsErrors`) |
| Domain project has no infrastructure dependency | Met | `tests/Ransys.IntegrationTests/Architecture/DependencyRulesTests.cs` |
| Canonical types are implemented | Met | `src/Ransys.Domain` (Money, IDs, identity, fingerprint, fees, references, endpoint, customer, routing decision, attempt) + `tests/Ransys.Domain.Tests` |
| Transaction aggregate enforces valid transitions | Met | `Transaction` + `TransactionTransitions`; `ExhaustiveTransitionTests` runs every method in every reachable state |
| PostgreSQL schema/migrations exist | Met | `src/Ransys.Persistence.PostgreSql/Migrations/Scripts` (DDL v1.1 + ADR-004/005/013 expand migrations), `MigrationTests` |
| Wallet reserve is atomic | Met | `LedgerPostingService.ReserveAsync` (`FOR UPDATE`), `ReserveWithTransactionStateTests` |
| Ledger postings balance | Met | `Journal.Create` + deferred DB trigger; `SchemaConstraintTests`, ledger-reconstructed projection checks |
| Duplicate posting keys are safe | Met | Posting-key idempotency (`AlreadyPosted` / `POSTING_KEY_CONFLICT`); concurrent duplicate reserve/post tests |
| Idempotency behavior works | Met | `IdempotencyService` (savepoint claim, lazy expiry, sweep); `IdempotencyServiceTests` (unit + PostgreSQL) |
| Transaction attempts work | Met | `TransactionAttemptService`, `AttemptRecoveryService`; `TransactionAttemptTests` |
| Outbox works brokerless | Met | `OutboxProcessor` + `OutboxWorker` (`FOR UPDATE SKIP LOCKED`, lease, retry, DEAD); `OutboxProcessorTests`; verified with the real worker host |
| Same-wallet concurrent spend cannot create negative balance | Met | `Same_wallet_race_only_one_reservation_succeeds`, `Many_concurrent_reservations_never_overspend`, mixed reserve/finalize load test |
| Timeout can remain `IN_DOUBT` with reserve held | Met | Aggregate `MarkInDoubt`, ledger `ChangeHoldReasonAsync`, `Timeout_goes_in_doubt_with_hold_kept...`, crash-recovery test |
| Integration tests use actual PostgreSQL semantics | Met | `PostgresDatabaseFixture` (local PostgreSQL 18, `RANSYS_PG`); no in-memory/SQLite substitutes |
| Documentation lists unresolved architecture decisions | Met | `docs/decisions/ADR-001…013` and the list below |

Cross-component concurrency (main.md §22) is covered by `tests/Ransys.IntegrationTests/Concurrency/CrossComponentConcurrencyTests.cs`: duplicate provider results from several sources, status check vs reconciliation with contradicting results, callback vs recovery worker, and concurrent reservations with finalizations on one wallet (lock-order / deadlock check).

## Merchant API (OpenAPI v1) and Provider Adapter Contract v1 — Milestone 12

Contracts: `docs/RANSYS_OpenAPI_v1.yaml`, `docs/RANSYS_Provider_Adapter_Contract_v1.md`, `docs/RANSYS_Provider_Adapter_v1.proto`, `docs/RANSYS_Provider_Adapter_Contracts_v1.cs`.

- **Endpoints** (`src/Ransys.Api`): `POST /api/v1/{inquiries,payments,transfers,refunds,reversals,voids}` and `GET /api/v1/transactions/{ransysTransactionId}`. DTOs in `Contracts/V1` mirror the OpenAPI schemas. Money is a decimal string, and unknown JSON properties are rejected. HTTP 200 means the request entered processing; the business result is `transactionStatus` plus `responseCode` (ADR-021).
- **Security** (ADR-022, proposed): production authentication fails closed (401). A development authenticator (`X-Ransys-Client-Id` = channel UUID, timestamp window, nonce, `Content-Digest`) is allowed only in Development/Test with `Ransys:Auth:AllowDevelopmentAuthentication=true`. Signature verification (`ISignatureVerifier`) still needs the signature profile.
- **Processing**: `TransactionProcessingService` persists the transaction, idempotency claim, fee (ADR-020), reserve, route and attempt **before** calling the provider. It calls the provider outside any DB transaction, then records and finalizes the result. A database failure *before* the provider is ever called (session 1) surfaces as API 503, and the provider is never called. A database failure *after* the provider was called (session 2) never returns 503 — the honest answer is IN_DOUBT 1002, with the outcome-less attempt left for `AttemptRecoveryService` to resolve (never silently dropped, never treated as "not sent").
- **Child transactions**: reversal (ADR-012), refund (ADR-023/024) and void (ADR-019: fail closed, reconciliation exception) are children with their own id. Starting one never overwrites the original.
- **Provider adapters**: the contract is in `Ransys.Adapter.Contracts.V1` and the gRPC binding in `Ransys.Adapter.Sdk`, with the same semantics as the in-process binding. Core uses `IProviderAdapterResolver` (`InProcessProviderAdapterRegistry`), `ProviderResultInterpreter` (conservative `RequestSent`) and `ProviderCallbackSink` (idempotent callbacks).
- **Running locally**: `dotnet user-secrets set "ConnectionStrings:TransactionDb" "<connection string>" --project src/Ransys.Api`. No real provider adapter is registered yet.

| Handoff §31 criterion | Status |
|---|---|
| OpenAPI v1 endpoints implemented, DTOs match YAML | Met (`tests/Ransys.Api.Tests` contract tests compare DTOs with the YAML schemas) |
| DTOs do not leak persistence/domain internals | Met |
| Decimal string amounts map safely to decimal | Met |
| Reversal / refund child transactions; VOID independent | Met (ADR-012, ADR-023, ADR-019) |
| Provider Adapter Contract v1 and protobuf compile | Met (build; proto byte-equal to the doc) |
| `requestSent` safety preserved; adapter cannot mutate financial state | Met (ADR-005, `ProviderResultRules`, dependency rules) |
| Callback ingress idempotency-ready | Met (`ProviderCallbackSink`) |
| `dotnet build` / `dotnet test` pass | Met: 0 warnings, 1,439 tests (`f1fbefe`, .NET 10.0.401, local PostgreSQL 18) |
| Conflicts documented as ADRs | ADR-018 … ADR-024 |

Remaining TODOs: Response Code Catalog v1 (only approved codes are used; frozen wallet and unmapped failures fall back to 1001), the signature profile and production authenticator, real provider adapters and their configuration, inquiry default currency, a hosted recovery worker schedule, outbox consumers (Backoffice), the circuit breaker, and the final VOID financial semantics (ADR-019).

## Architecture review remediation — Milestone 13

An external architecture review (`review/RANSYS_Architecture_Review_f1fbefe.md`, HEAD `f1fbefe`) found three P1 correctness issues and three P2 issues in the Milestone 12 work above. All six are fixed:

| ID | Finding | Fix | Commit |
|---|---|---|---|
| R1 | Provider callback validation raced finalization (unlocked read, separate later lock) | `ProviderCallbackSink` now takes one continuous `FOR UPDATE` lock for the whole call; provider-mismatch check and attempt correlation both run under it | `4e8edf5` |
| R2 | Callback-resolved attempts never got their provider reference/STAN/RRN persisted | The sink now calls `TransactionAttemptService.RecordOutcomeAsync` (ADR-005 exactly-once) before finalizing, so callback evidence is available to GET and to child (refund/reversal) provider requests | `4e8edf5` |
| R3 | A retry could fail (`ProductNotAvailable`/`WalletNotFound`/conflict) if reference data drifted after the original request | `IdempotencyService.PeekActiveAsync` runs before any reference-data resolution; a replay's fingerprint is anchored to the original transaction's own product/currency snapshot, never live "currently active" data (ADR-025) | `93f6396` |
| R4 | A replay of a completed transaction always returned empty `data` (e.g. inquiry `billAmount` lost) | Business response data is now a promoted canonical field, `transaction_attempts.response_data` (migration `0009`, ADR-026), and replay returns the latest resolved attempt's data | `bd80418` |
| R5 | `AttemptRecoveryService` marked a transaction IN_DOUBT without ever publishing a `TRANSACTION_IN_DOUBT` outbox event | Recovery now shares `TransactionStatusEvents.Build` with `TransactionFinalizationService` and enqueues the same event, in the same session, exactly once | `a2b9039` |
| R6 | CLAUDE.md/README.md contained stale/contradictory passages (this section, the ADR index, the milestone table, the DB-unavailable claim, the posting-key example, ADR status categorization) | This documentation pass | — |

Two new ADRs came out of this: ADR-025 (idempotency replay anchors to the original transaction's snapshot) and ADR-026 (persisted response-data snapshot for replay), both Accepted as correctness fixes under already-established rules (ADR-006, ADR-009, and the "fields that come into common use get promoted to canonical fields" convention), not new open product decisions.

Final verification for this milestone: `dotnet build Ransys.sln` — 0 warnings, 0 errors; `dotnet test Ransys.sln` against real PostgreSQL (`RANSYS_TEST_PG`) — **1,446 passed, 0 failed, 0 skipped** across all 8 test projects (`.NET 10.0.401`, local PostgreSQL 18), at commit `a2b9039`. Not pushed to GitHub pending review.

## Architecture decisions

All decisions are recorded in [`docs/decisions/`](docs/decisions/) (ADR-001 … ADR-026).

Decided and implemented (product owner accepted):

- ADR-012: reversal as a child transaction (`ReversalService`, migration `0006`); the original is never overwritten while the reversal runs and becomes REVERSED when the child is confirmed (ADR-003 superseded).
- ADR-014: `FeeComponent.RefundPolicy` (migration `0005`) and `RefundFeeCalculator` (FULL on completion, PRO_RATA cumulative truncated). Refund finalization (M12c) calls it and passes the result to `PostRefundAsync`.
- ADR-016: wallet status semantics and `WalletStatusService` freeze/unfreeze/close (maker-checker requirement for status changes not specified; approval reference carried when supplied).
- ADR-017: TRANSFER / VOID capabilities official; VOID never mapped to reversal/refund.
- ADR-018: capability codes are the Provider Adapter Contract v1 catalog (`PAYMENT`, `BALANCE_CHECK`, `VOID`, …); migration `0007` renames the old `supports_*` rows and adds transport status `PROTOCOL_ERROR` (never proves not-sent).
- ADR-019 (interim): VOID is a child transaction; `Transaction.RecordVoidConfirmed` only sets the original's reconciliation to EXCEPTION (`VOID_CONFIRMED_REQUIRES_REVIEW`). VOID financial semantics are still open.
- ADR-020 (interim): minimal fee resolver (`FeeResolver`): active FEE version, FIXED or PERCENTAGE (decimal rate) × amount, clamp min/max, half away from zero to scale, merchant-specific rule wins, ambiguous ⇒ fail closed, no rule/version ⇒ zero fee, refund policy NONE.
- ADR-021: API error mapping — HTTP status per business/validation outcome (ADR-021's own file: Status Accepted).
- ADR-025: idempotent replay anchors to the original transaction's own product/currency snapshot; the existing-claim lookup now runs before any reference-data resolution. Correctness fix, not a new open decision.
- ADR-026: business response data (e.g. inquiry `billAmount`) is a promoted canonical field, `transaction_attempts.response_data` — distinct from `Metadata`'s provider-extension purpose — so a replay returns the same data the original response had.

Implemented, pending product-owner acceptance (each ADR file's own Status line still says "Proposed" — do not read the implementation as a decision already signed off):

- ADR-022: request authentication abstractions (`IRequestAuthenticationService`/`ISignatureVerifier`/`IReplayProtectionService`); production fails closed, a Development/Test-only authenticator exists. The RANSYS signed-message profile itself is still undecided.
- ADR-023: a refund child completes the original directly (`AuthorizeRefund`, `ApplyRefundCompleted`: SUCCESS → PARTIALLY_REFUNDED / REFUNDED, no REFUND_PENDING). `TransactionFinalizationService` posts the refund (fee via `RefundFeeCalculator`, cumulative over earlier successful refund children) and completes the original in the same DB transaction.
- ADR-024: `RefundAuthorization` distinguishes a manual refund (maker-checker `ApprovedRequest`) from a merchant API refund bound to its own REFUND child (`MerchantApiRequest`, no approval id).

Decided, no Phase 1 code change needed:

- ADR-015: semantic account types are authoritative; Finance GL codes are configurable mappings (finance export, not the posting path).

Still open:

- Mapping fee components' accounting amounts to provider cost / tax / margin accounts on payment success (Ledger Matrix §13); baseline split implemented, explicit split supported.
- VOID financial semantics (ADR-019).
- Response Code Catalog v1: processing uses only 0000/1001/1002/2001/2003/3001/4001/5001; a frozen wallet and unmapped failures fall back to 1001, non-final states to 1002 (`RansysResponseCodes`).
- Per-provider call policy (timeouts) and product default currency for inquiries (`ProviderCallPolicy`, `TransactionProcessingOptions`) until Configuration Schema v1.
- Minimum age before recovering an outcome-less attempt must exceed the longest provider timeout; the recovery worker schedule is not yet configured (service implemented, no hosted loop).
- Outbox consumers: Backoffice projection with inbox/dedup + `source_version`, optional RabbitMQ publisher, DEAD-event alerting and retention of published rows.
- Circuit breaker (state transitions, half-open probe limits) and provider health measurement are not implemented; routing only reads their state.
- Items deferred to later design documents: the SIGNED_API contract (signature profile), response code catalog, configuration schema, SOAP/ISO8583 profiles. (OpenAPI v1 and Provider Adapter Contract v1 are implemented, Milestone 12 — see above.)
