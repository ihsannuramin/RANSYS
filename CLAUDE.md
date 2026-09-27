# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository state

Phase 1 (main.md milestones 1–10) is implemented; see the Definition of Done table in `README.md`. `main.md` is the implementation handoff: it defines the milestones, the non-negotiable invariants, and the "do not implement yet" list. Milestone 12 (OpenAPI v1 + Provider Adapter Contract v1) follows a second handoff, `20260927-handoff-part2.md`, at repo root. The design documents in `docs/` are authoritative and are written in mixed Indonesian and English. Keep that style when editing them. Milestone status is tracked in `README.md`.

## Commands

```bash
dotnet build Ransys.sln                     # TreatWarningsAsErrors is on; must stay at 0 warnings
dotnet test Ransys.sln
dotnet test tests/Ransys.Domain.Tests       # single project
dotnet test Ransys.sln --filter "FullyQualifiedName~DependencyRulesTests"   # single class/test
```

To run DB tests from a shell that predates the user-level env var: `export RANSYS_TEST_PG="$(powershell.exe -NoProfile -Command "[Environment]::GetEnvironmentVariable('RANSYS_TEST_PG','User')" | tr -d '\r')"`. Don't echo it; it contains the password.

DB-backed tests use a real local PostgreSQL 18, database `RANSYS_PG`. The connection string comes from the env var `RANSYS_TEST_PG` (see `tests/Ransys.Testing.PostgreSql/TestDatabaseSettings.cs`). Never substitute in-memory/SQLite for locking, unique-index, trigger, or `SKIP LOCKED` tests. Docker is not installed on this machine.

## Implementation rules (decided, see `docs/decisions/ADR-*.md`)

- **No EF Core.** Use Npgsql + Dapper with explicit SQL and explicit `NpgsqlTransaction`. Apply the schema from SQL migration scripts derived from the v1.1 DDL (ADR-011).
- Package versions live only in `Directory.Packages.props` (central package management). Shared build settings are in `Directory.Build.props`; test packages are added automatically by `tests/Directory.Build.props`.
- Dependency direction (`Domain ← Application ← Infrastructure/API`) and "adapters cannot reach Ledger/TransactionCore/Persistence" are enforced by `tests/Ransys.IntegrationTests/Architecture/DependencyRulesTests.cs`. Update that test when adding project references.
- Posting keys follow ADR-001 (`TX:<txId>:RESERVE|POST|RELEASE|REVERSAL_RELEASE|REVERSAL:<ref>|REFUND:<ref>`, `TOPUP:<ref>`, `ADJUSTMENT:<ref>`).
- Only Transaction Core writes `transaction_attempts`. An attempt with no recorded outcome counts as possibly sent, so the transaction goes IN_DOUBT and never fails over (ADR-005).
- If implementation conflicts with the docs, write a new `docs/decisions/ADR-xxx-<topic>.md` (Context, Existing RANSYS rule, Technical issue, Options, Recommended option, Consequences) instead of choosing silently. Mark gaps `TODO / Architecture Decision Required`.
- Expected business outcomes (`INSUFFICIENT_BALANCE`, `DUPLICATE_REFERENCE_CONFLICT`, `INVALID_STATE_TRANSITION`, `POSTING_ALREADY_EXISTS`) are returned as results, not thrown. `Result`/`Result<T>`/`RansysError` live in `Ransys.Domain.Common` (the Domain can't reference Application). Programming errors (empty GUID IDs, currency mismatch in `Money` arithmetic) throw.

## Persistence conventions (`src/Ransys.Persistence.PostgreSql`)

- Schema changes are new files in `Migrations/Scripts/NNNN_*.sql` (embedded, applied in name order by `DatabaseMigrator` via DbUp, journal `public.ransys_schema_versions`). Never edit a released script. `0001` must stay byte-identical (modulo line endings) to the reference DDL, and `MigrationTests` checks this. Additive changes use expand migrations (`0002` = ADR-004, `0003` = ADR-013).
- All DB work goes through a `PostgresSession` (one connection + one explicit transaction). Stores take the session as their first argument so multiple stores commit atomically. Disposing without commit rolls back; after a rollback, reload aggregates because stores advance `RowVersion` optimistically.
- Timestamps are written as UTC (`DbValues.ToDb`), because Npgsql rejects non-zero offsets for `timestamptz`. jsonb params need `CAST(@X AS jsonb)`. Dapper maps snake_case columns via `DbValues.EnsureConfigured()`.
- Loading maps rows back through domain factories and `Transaction.Rehydrate`. Invalid persisted data returns `PERSISTED_STATE_INVALID` (fail closed) instead of loading.
- DB test assemblies use `PostgresDatabaseFixture` through an xUnit collection defined in each test assembly (e.g. `tests/Ransys.Persistence.Tests/PostgresCollection.cs`). The fixture refuses databases not prefixed `RANSYS_PG`, holds a global advisory lock across assemblies, drops `core/ledger/integration/config/async`, migrates, and seeds `TestSeed`.

## Ledger conventions

- Balances change **only** through `ILedgerPostingService` (`src/Ransys.Ledger`). It runs inside the caller's `IDatabaseSession`, so the caller can also update the Transaction aggregate and commit once. Pure rules live in `src/Ransys.Domain/Ledger`: `PostingKey` (ADR-001), `LedgerAccounts` (ADR-007, currency rendered `IDR-V1`), a balanced `Journal`, the `Wallet` projection and the `Reservation` lifecycle.
- Every operation follows lock order: caller locks the transaction row → service locks the wallet (`FOR UPDATE`) → the reservation → checks the posting key. A repeated key with the same terms returns `LedgerPostingOutcome.AlreadyPosted`; different terms return `POSTING_KEY_CONFLICT`. No network I/O inside the session.
- Wallet status (ADR-016): FROZEN blocks only new consumption (reserve, debit adjustment); CLOSED is terminal. Change status only through `WalletStatusService`, which re-checks close preconditions under the wallet lock and emits `WALLET_STATUS_CHANGED`.
- Refund fee amounts come only from `RefundFeeCalculator` over the original transaction's captured fee components and the cumulative refunded principal (ADR-014). Never use current fee configuration.
- Adjustments and manual refunds require an approval request id (maker-checker). A refund carries an explicit `RefundAuthorization` (ADR-024): `ApprovedRequest(approvalId)` for manual refunds, or `MerchantApiRequest` bound to its own REFUND child (created only by finalization). Total refunds can never exceed the posted amount. Debit adjustments can never make a balance negative.
- `PostgresLedgerStore` only inserts journals and never updates them (ADR-002). Ledger DB tests (`tests/Ransys.Ledger.Tests`) assert that the wallet projection equals the projection rebuilt from ledger entries. Keep that check in new scenarios.

## Merchant API (OpenAPI v1)

- `docs/RANSYS_OpenAPI_v1.yaml` is authoritative. API DTOs (`src/Ransys.Api/Contracts/V1`) must match it exactly, which `tests/Ransys.Api.Tests` contract tests enforce. Never expose domain or persistence types, provider topology, posting keys, reservation ids, row versions or raw-message URIs.
- The flow is DTO → command → `TransactionProcessingService` → result → response DTO. HTTP status follows ADR-021: 200 for every business outcome, 400 validation (2001), 401 auth (3001), 409 duplicate conflict (2003), 503 financial dependency unavailable. Use only approved response codes until the Response Code Catalog exists.
- Authentication fails closed in production (ADR-022). The development authenticator is allowed only in Development/Test behind `Ransys:Auth:AllowDevelopmentAuthentication`. Nonce ≠ Idempotency-Key ≠ clientReference ≠ fingerprint.

## Finalization (applying provider results)

- Every provider result for the original request goes through `TransactionFinalizationService.ApplyAsync` (`src/Ransys.TransactionCore/Finalization`), whatever the source: sync response, callback, status check, advice, reconciliation. It locks the transaction row, applies the aggregate transition, executes the requested ledger action (POST / RELEASE, hold reason for IN_DOUBT), updates with `row_version`, and enqueues a `TRANSACTION` status event, all in one session. Don't write parallel paths that call the ledger directly for provider results.

## Reversal (ADR-012)

- A reversal is a **child transaction** (type REVERSAL, own id, `original_transaction_id`, idempotency, attempts, history) started by `ReversalService.StartAsync`. Never change the original's state when starting a reversal; the original has no REVERSAL_PENDING any more.
- Lock order is parent → child: `TransactionFinalizationService` locks the original before the child. On child SUCCESS, `Transaction.ApplyReversalConfirmed` makes the original REVERSED in the same DB transaction. The ledger effect follows the original's financial state at that moment (RESERVED → `REVERSAL_RELEASE`, POSTED → compensating `TX:<original>:REVERSAL:<child>`). A declined child fails alone.

## Refund and VOID children (ADR-023, ADR-019)

- A refund is a child transaction too. Check `Transaction.AuthorizeRefund()` on the original; when the child succeeds, call `PostRefundAsync` and `Transaction.ApplyRefundCompleted(child, fullyRefunded, ctx)` in the same DB transaction (it returns `LedgerAction.None`). The original goes SUCCESS → PARTIALLY_REFUNDED / REFUNDED directly and never uses REFUND_PENDING.
- VOID fails closed: `AuthorizeVoid()` allows the same states as a reversal, and `RecordVoidConfirmed` only sets the original's reconciliation to EXCEPTION. Never move money for a VOID until its semantics have an ADR.
- `TransactionFinalizationService` handles REVERSAL, REFUND and VOID children with the same parent → child lock order. Refund child SUCCESS: fee = `RefundFeeCalculator(original fees, principal of earlier SUCCESS refund children, child amount)`, `PostRefundAsync` against the original's actual provider with `MerchantApiRequest`, then `ApplyRefundCompleted(fullyRefunded)`. A failed child never touches the original.

## Transaction attempts (ADR-005)

- Create attempts only via `TransactionAttemptService.StartAsync` while holding the transaction row lock, and **commit before calling the provider**. It runs `Transaction.AuthorizeAttempt`, so there are no new financial requests after a possible send and attempts only target the current routed provider. The primary attempt type must match the transaction type (`Transaction.PrimaryAttemptTypeFor`: Payment/Purchase → PAYMENT, Transfer → TRANSFER, Refund, Reversal, Void, Inquiry, BalanceInquiry). Record results with `RecordOutcomeAsync`, exactly once.
- DB representation: a started row is `request_sent=true, transport_status='SENT', outcome_recorded_at NULL` (migration `0004`). Only `outcome_recorded_at IS NOT NULL` means an outcome exists. Never downgrade `request_sent` except through a recorded adapter outcome.
- Map results with `AttemptResolution.Classify`. `PROTOCOL_ERROR` (ADR-018) never proves not-sent; only NOT_SENT / CONNECTION_ERROR with `request_sent=false` do. `AttemptRecoveryService` turns outcome-less attempts into IN_DOUBT (never failover or release) and, like `TransactionFinalizationService`, enqueues the transaction status event in the same session (shared `TransactionStatusEvents.Build` helper) — recovery must never change a transaction's state without publishing it.
- The recorded outcome's business response data (e.g. an inquiry's `billAmount`) is a promoted canonical field, `response_data` (migration `0009`, ADR-026) — distinct from `Metadata`'s provider-extension purpose. A replay of an already-completed transaction returns the latest resolved attempt's `response_data` instead of an empty object.

## Transaction processing (M12d)

- `TransactionProcessingService` (`src/Ransys.TransactionCore/Processing`) is the only orchestration of a merchant request: `InquireAsync`, `PayAsync`, `TransferAsync`, `RefundAsync`, `ReverseAsync`, `VoidAsync`, `GetTransactionAsync` (channel-scoped). Commands are application records (not API DTOs) carrying the authenticated `ChannelId`/`MerchantId`.
- Session 1 (inside `IdempotencyService.ClaimAsync`): route (read first so the routing config version is captured), `FeeResolver` (ADR-020), `Validate`, insert, ledger reserve, `MarkReserved`, `BeginProcessing`, start attempt, commit. Insufficient balance / frozen wallet / no route end the transaction FAILED through finalization (committed, never a provider call). Any DB failure throws `FinancialDependencyUnavailableException` (API 503).
- The provider call happens only after that commit, with `CancellationToken.None` from then on. Session 2 locks parent → child, re-reads the attempt, records the outcome once, then finalizes; NOT_SENT fails over (bounded, originals only), no provider left ⇒ FAILED 5001 with release. If session 2 cannot be applied, a fallback marks IN_DOUBT; if nothing can be written, the result is IN_DOUBT 1002 and recovery handles the outcome-less attempt.
- Children: reversal via `ReversalService`; refund/void via `ChildTransactionService` (capability REFUND/VOID on the original's provider; refunds capped by the sum of non-FAILED refund children; one open VOID per original, migration `0008`). An unknown or other-channel original is `ORIGINAL_TRANSACTION_INVALID`. A replay whose existing child references a different original is a duplicate conflict (the fingerprint does not include the original id).

## Routing

- `RoutingService.RouteAsync` (`src/Ransys.Routing`) reads the active ROUTING config version (`ConfigurationService`, fails closed with `CONFIGURATION_NOT_AVAILABLE`) and applies `RoutingPolicy` (`src/Ransys.Domain/Routing`). It picks the lowest priority number among eligible providers. Excluded are: route disabled, provider inactive, no operational state row, manual disable (a `manual_disabled_until` in the past means re-enabled), circuit OPEN, UNHEALTHY, missing capability (`ProviderCapabilities.RequiredFor`; codes are the uppercase adapter-contract catalog, ADR-018), or already tried (`ExcludedProviders`). DEGRADED and HALF_OPEN stay eligible. Type-specific routes override wildcard (`transaction_type IS NULL`) routes.
- Routing never decides whether a failover is safe; that is `Transaction.RecordFailover` / `AuthorizeAttempt`. Capture the result with `RoutingResult.ToInitialDecision()` and store `ConfigVersionId` in the transaction's configuration snapshot. No weighted, least-cost or smart routing.

## Transactional outbox

- Producers write events only through `IOutboxWriter.EnqueueAsync` inside the same `IDatabaseSession` as the change they describe. The Ledger Posting Service already does this for every posting.
- Delivery is `OutboxProcessor` (`src/Ransys.Application/Outbox`) hosted by `OutboxWorker`, using a lease pattern on the DDL columns. It claims with `FOR UPDATE SKIP LOCKED` → `PROCESSING` + `locked_by` → commit, **publishes outside any DB transaction**, then completes each event in its own transaction as `PUBLISHED`, `PENDING` + exponential `next_retry_at`, or `DEAD`, plus one `outbox_delivery_attempts` row. Completion is guarded by `locked_by`, so a worker whose lease expired cannot overwrite the new owner. Publisher exceptions and timeouts count as failed deliveries.
- Delivery is at least once. Consumers must dedup by `event_id` and order by `source_version`. `IDatabaseSession` now includes Commit/Rollback/Dispose; open sessions via `IDatabaseSessionFactory` (`PostgresSessionFactory`).

## Idempotency

- New transactions are created only through `IdempotencyService.ClaimAsync(session, channel, identity, createTransaction)` (`src/Ransys.TransactionCore/Idempotency`). It returns `New` (your callback inserted the row), `ExistingTransaction` (return that transaction; discard the aggregate you built), or `DUPLICATE_REFERENCE_CONFLICT`. The callback runs under a savepoint, because losing the `ux_idempotency_active_reference` race aborts the PG transaction. Don't catch unique violations elsewhere to replicate this.
- A retry must not depend on reference data that can drift after the original request (ADR-025). `TransactionProcessingService.PrepareOriginalAsync` calls `IdempotencyService.PeekActiveAsync` for an existing claim **before** resolving product/currency/wallet; when a claim exists, the candidate fingerprint is anchored to the *original transaction's own* `ProductId` and hydrated `Amount.Currency` (never a fresh "currently active" lookup), so a deactivated product or a rolled-over currency version never breaks a legitimate replay. Only a genuinely new transaction resolves live reference data.
- `IDatabaseSession` supports savepoints for this purpose. `Ransys.Workers` hosts `IdempotencyExpiryWorker` (ADR-009 sweep) and requires `ConnectionStrings:TransactionDb` (set locally as a user secret, `UserSecretsId=ransys-workers-dev`; never commit it).

## Provider Adapter contract (M12b)

- C# contract: `Ransys.Adapter.Contracts.V1` (`src/Ransys.Adapter.Contracts/V1`, shapes verbatim from `docs/RANSYS_Provider_Adapter_Contracts_v1.cs`). Wire contract: `Protos/ransys_provider_adapter_v1.proto` → generated `Ransys.Provider.V1`; it must stay identical to the doc proto (`ProtoBaselineTests`). Change neither without a new contract version.
- Rules live in Adapter.Contracts: `ProviderResultRules` (valid Outcome/Finality pairs, `RequestSent`/transport consistency, `Normalize` → IN_DOUBT, never NOT_SENT/FAILED), `ProviderResults` (CapabilityUnsupported / NotSent / InDoubt factories), `ProviderCapabilityCodes` (= `Domain.Routing.ProviderCapabilities`), `ProviderErrorCategories`, `ProviderResultCodes` (interim codes reusing approved examples; TODO Response Code Catalog v1).
- `Ransys.Adapter.Sdk`: `ProtoMapper` is the only proto ↔ C# mapping (money as exact decimal strings; mapping choices documented on the class; throws `ProtoMappingException`, never guesses). `GrpcProviderAdapterClient` / `ProviderAdapterGrpcService` and `GrpcProviderCallbackSinkClient` / `ProviderCallbackSinkGrpcService` are thin bindings with no business logic.
- No hidden retries anywhere: one gRPC call per operation, and never configure a gRPC retry/hedging policy on adapter channels.
- Core provider gateway (`src/Ransys.TransactionCore/Providers`, M12c; Core references only `Adapter.Contracts`): `IProviderAdapterResolver` (`InProcessProviderAdapterRegistry` for Lite/tests), `ProviderRequestFactory` (children carry the original's provider references), `ProviderInvoker` (dispatch by transaction type, time budget; missing adapter ⇒ NOT_SENT; exception/timeout after start ⇒ IN_DOUBT `RequestSent=true`; never throws), `ProviderResultInterpreter` (`Normalize` first; values that do not fit attempt columns go to `extension.adapter.*` metadata; never drops an outcome).
- `ProviderCallbackSink` (V1 `IProviderCallbackSink`): `OriginalRansysTransactionId` is the transaction the provider acted on (the **child** id for reversal/refund/void). It locks the transaction row once (`FOR UPDATE`) and keeps that single lock for the whole call — the provider-match check, attempt correlation, and the finalization it calls all run under it, so a concurrent failover cannot land between the check and the update. It correlates the callback to the attempt for that provider and, if that attempt has no recorded outcome yet, persists the callback's provider reference/STAN/RRN via `TransactionAttemptService.RecordOutcomeAsync` (ADR-005 exactly-once) before calling finalization with `ChangeSource.Callback`. Duplicate/conflict/stale ⇒ accepted; unknown, provider mismatch, NOT_SENT or DB failure ⇒ not accepted.
- `RequestSent` is conservative: a gRPC failure that may have reached the adapter becomes IN_DOUBT + AMBIGUOUS with `RequestSent=true`. NOT_SENT only when non-delivery is proven (request not mappable, connection could not be established). TRANSFER/VOID call `TransferAsync`/`VoidAsync`, never reversal/refund.

## Domain conventions (`src/Ransys.Domain`)

- Value objects are created only through static `Create(...)` factories returning `Result<T>`. They validate and never trim or truncate input. The one exception is the provider response message, which is truncated to 500 characters so an outcome is never lost.
- `Money` lives in `Ransys.Domain.Monetary` (not `.Money`, to avoid a namespace/type clash). It is non-negative, carries a `CurrencyDefinition` (code + version + scale) and has no operators. Use `Add`/`Subtract`/`IsGreaterThan`.
- Persisted/wire enum values come only from `CanonicalCodes` (explicit SCREAMING_SNAKE maps). `CanonicalCodesTests` checks them against the DDL CHECK constraints, so update the DDL-derived migration and the map together.
- `Transaction` (aggregate) changes status only through transition methods. Each returns `Result<TransitionOutcome>` whose `LedgerAction` (Reserve/Post/Release/ReversalRelease/CompensatingReversal) the application must execute in the same DB transaction. Its `PendingStateChanges` become history rows, one per changed dimension. Every edge is checked against `TransactionTransitions` (the State Matrix tables), and `ExhaustiveTransitionTests` runs every method against every reachable state, so extend `TransactionBuilder.ReachableStates()` when adding states or methods.
- `TransactionAttempt.MayHaveReachedProvider` is the only failover guard. It is true unless a recorded outcome proves the request was not sent (ADR-005).

RANSYS is a universal transaction switching and processing platform. It receives financial transactions from merchants and channels (REST/JSON, SOAP/XML, ISO 8583, TCP/proprietary), then routes them to banks, billers, and suppliers. It is a full processing platform with its own wallet and ledger, not just a message router.

Stack (Architecture Spec §67, pinned to .NET 10 via `global.json`): C# / modern .NET LTS on Linux, ASP.NET Core/Kestrel, `System.IO.Pipelines` for TCP/ISO8583, PostgreSQL as the financial source of truth, Redis (cache only), a PostgreSQL transactional outbox with .NET background workers, and RabbitMQ as an optional add-on.

## Document map and precedence

Read these in this order. Later, more detailed documents refine earlier ones.

1. `RANSYS_PRD_v1.0.md`: product requirements, actors, deployment profiles (Lite = single VM, HA = N nodes).
2. `RANSYS Architecture Specification v1.0.md`: principles, component boundaries, and the **18 fundamental invariants (§71)**.
3. `RANSYS_Sequence_Diagram_Pack_v1.0.md`: Mermaid flows SD-01…SD-15, lock order, outbox event catalog, design refinements (§28).
4. `RANSYS_Transaction_State_Transition_Matrix_v1.0.md`: transitions PS-xx per state dimension.
5. `RANSYS_Ledger_Posting_Rule_Matrix_v1.0.md`: double-entry postings OP-01…OP-xx (reserve, post, release, reversal, refund, top-up, adjustment).
6. `RANSYS_ERD_Physical_PostgreSQL_v1.1.md` + `RANSYS_PostgreSQL_Reference_DDL_v1.1.sql`: physical schema. **v1.1 supersedes `RANSYS_Database_ERD_v1.0.md`.**
7. `RANSYS_Canonical_Data_Model_v1.0.md` + `RANSYS_Canonical_Contracts_v1.0.cs`: canonical domain model and reference C# contracts (namespace `Ransys.Contracts.Canonical`).

Known refinement: the Architecture Spec §6 describes a single transaction status. The later documents split status into **four independent dimensions**: `processing_status`, `financial_status`, `reconciliation_status`, `settlement_status`. Follow the four-dimension model. `RECON_PENDING`/`RECON_EXCEPTION` belong to the reconciliation dimension, not processing.

OpenAPI v1 and Provider Adapter Contract v1 (Sequence Pack §29) are implemented as of Milestone 12 — see `docs/RANSYS_OpenAPI_v1.yaml` and `docs/RANSYS_Provider_Adapter_Contracts_v1.cs`/`.proto`, both now authoritative alongside the documents above. Next planned artifacts: Response Code Catalog v1, Configuration Schema v1.

## Architecture essentials (cross-document)

**Two physically separate PostgreSQL databases, with no cross-DB foreign keys** (only logical IDs cross the boundary):
- Transaction DB (online, about 3 weeks retention): schemas `core`, `ledger`, `integration`, `config`, `async`.
- Backoffice DB (at least 2 years): schemas `bo`, `recon`, `settlement`, `iam`, `audit`. It is fed by outbox events with inbox/dedup plus `source_version`, so later status changes (e.g. IN_DOUBT → SUCCESS) update existing rows.

**Financial flow (the core pattern):**
1. DB transaction: create transaction → lock wallet (`FOR UPDATE`) → check available balance → reserve (amount + guaranteed merchant fee) → ledger HOLD → outbox event → COMMIT.
2. Call the provider **outside** any DB transaction or row lock.
3. A new DB transaction finalizes the result (post / release / keep hold).
4. Lock order for finalization: transaction row → wallet row → reservation row → unique ledger `posting_key` check. Use optimistic row version plus row locks.

**Timeout ≠ failure:** an ambiguous timeout moves the transaction to `IN_DOUBT`. The reservation stays held, it is never auto-released or auto-failed, and it is **never failed over** to another provider. Resolution comes through status check, callback, advice, reversal, or reconciliation. Failover is allowed only when the request was provably not sent: `transaction_attempts.request_sent` is safety-critical.

**Component boundaries:**
- Provider adapters: one logical provider = one independently deployable adapter, built on a shared "Ransys Adapter SDK". Adapters handle only protocol, mapping, and provider auth. They must never touch the wallet or ledger, or decide final financial state.
- Transaction Core must not depend on Backoffice, Recon, Settlement, RabbitMQ, or Redis availability. If the Transaction DB or Ledger is unavailable, it **fails closed** (rejects).
- Reconciliation auto-resolves only deterministic cases, through controlled Transaction Core/Ledger operations and never by direct DB mutation. Anything non-deterministic becomes a recon exception.

## Conventions locked by the specs

- Money: C# `decimal`, PostgreSQL `NUMERIC(30,8)` (rates `NUMERIC(30,12)`). Never float or double. The Money value carries `currency_code`, scale, and `currency_definition_version`. Reject amounts whose precision exceeds the currency scale.
- IDs: `uuid` columns, UUIDv7 generated in the application (not by the DB).
- Timestamps: `timestamptz`, timezone-aware.
- State columns: `VARCHAR` + named `CHECK` constraints (not native PG ENUM), so migrations can follow expand → deploy → migrate → contract.
- JSON extensions in `JSONB`. `Metadata` is only for provider/product-specific extensions. Fields that come into common use get promoted to canonical fields.
- Ledger: immutable, double-entry (`SUM(debit) = SUM(credit)`). Corrections use compensating entries. Every posting has a `UNIQUE posting_key` (ADR-001, e.g. `TX:<txId>:RESERVE`, `TX:<txId>:POST`, `TX:<txId>:REFUND:<ref>`). The wallet projection (`ledger_balance`, `available_balance`, `reserved_balance`) is updated in the same DB transaction and must be reconstructible from the ledger.
- Idempotency: 24h window scoped to client/channel + `client_reference`. Same reference with the same fingerprint returns the existing transaction. Same reference with a different fingerprint returns `2003 DUPLICATE_REFERENCE_CONFLICT`. ClientReference, IdempotencyKey, Fingerprint, and Nonce are distinct concepts.
- Response codes: 4-digit canonical namespaces (0xxx provider, 1xxx internal, 2xxx validation, 3xxx security, 4xxx financial, 5xxx routing/config, 6xxx recon/settlement, 7xxx async, 8xxx infra). `responseCode` and `transactionStatus` are separate fields. The raw provider code is always kept.
- Refunds and reversals are child transactions linked via `original_transaction_id`. Transactions record the fee, config, and routing versions they used.
- Maker-checker is mandatory for manual top-up, refund, manual reversal, and limit changes. The maker can never be the checker.
- External API is versioned (`/api/v1/payments`, …). Breaking changes require a new major version. Contract-first: OpenAPI / Protobuf / WSDL / ISO profile / versioned event schemas, all kept in source control.
- Async delivery is at-least-once, so every consumer must be idempotent (event_id + inbox).
- Application logs, security logs, audit logs, and raw provider messages are four separate stores. Never write PIN, CVV, or credentials to raw dumps.
