# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository state

Phase 1 implementation is in progress. `main.md` is the implementation handoff: it defines the milestones, the non-negotiable invariants, and the "do not implement yet" list. The design documents in `docs/` are authoritative and are written in mixed Indonesian and English. Keep that style when editing them. Milestone status is tracked in `README.md`.

## Commands

```bash
dotnet build Ransys.sln                     # TreatWarningsAsErrors is on; must stay at 0 warnings
dotnet test Ransys.sln
dotnet test tests/Ransys.Domain.Tests       # single project
dotnet test Ransys.sln --filter "FullyQualifiedName~DependencyRulesTests"   # single class/test
```

DB-backed tests use a real local PostgreSQL 18, database `RANSYS_PG`. The connection string comes from the env var `RANSYS_TEST_PG` (see `tests/Ransys.Testing.PostgreSql/TestDatabaseSettings.cs`). Never substitute in-memory/SQLite for locking, unique-index, trigger, or `SKIP LOCKED` tests. Docker is not installed on this machine.

## Implementation rules (decided, see `docs/decisions/ADR-*.md`)

- **No EF Core.** Use Npgsql + Dapper with explicit SQL and explicit `NpgsqlTransaction`. Apply the schema from SQL migration scripts derived from the v1.1 DDL (ADR-011).
- Package versions live only in `Directory.Packages.props` (central package management). Shared build settings are in `Directory.Build.props`; test packages are added automatically by `tests/Directory.Build.props`.
- Dependency direction (`Domain ← Application ← Infrastructure/API`) and "adapters cannot reach Ledger/TransactionCore/Persistence" are enforced by `tests/Ransys.IntegrationTests/Architecture/DependencyRulesTests.cs`. Update that test when adding project references.
- Posting keys follow ADR-001 (`TX:<txId>:RESERVE|POST|RELEASE|REVERSAL_RELEASE|REVERSAL:<ref>|REFUND:<ref>`, `TOPUP:<ref>`, `ADJUSTMENT:<ref>`).
- Only Transaction Core writes `transaction_attempts`. An attempt with no recorded outcome counts as possibly sent, so the transaction goes IN_DOUBT and never fails over (ADR-005).
- If implementation conflicts with the docs, write a new `docs/decisions/ADR-xxx-<topic>.md` (Context, Existing RANSYS rule, Technical issue, Options, Recommended option, Consequences) instead of choosing silently. Mark gaps `TODO / Architecture Decision Required`.
- Expected business outcomes (`INSUFFICIENT_BALANCE`, `DUPLICATE_REFERENCE_CONFLICT`, `INVALID_STATE_TRANSITION`, `POSTING_ALREADY_EXISTS`) are returned as results, not thrown. `Result`/`Result<T>`/`RansysError` live in `Ransys.Domain.Common` (the Domain can't reference Application). Programming errors (empty GUID IDs, currency mismatch in `Money` arithmetic) throw.

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

Next planned artifacts (Sequence Pack §29): OpenAPI v1, Provider Adapter Contract v1, Response Code Catalog v1, Configuration Schema v1.

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
- Ledger: immutable, double-entry (`SUM(debit) = SUM(credit)`). Corrections use compensating entries. Every posting has a `UNIQUE posting_key` (e.g. `TX123:RESERVE`, `TX123:POST`, `TX123:REFUND:RF001`). The wallet projection (`ledger_balance`, `available_balance`, `reserved_balance`) is updated in the same DB transaction and must be reconstructible from the ledger.
- Idempotency: 24h window scoped to client/channel + `client_reference`. Same reference with the same fingerprint returns the existing transaction. Same reference with a different fingerprint returns `2003 DUPLICATE_REFERENCE_CONFLICT`. ClientReference, IdempotencyKey, Fingerprint, and Nonce are distinct concepts.
- Response codes: 4-digit canonical namespaces (0xxx provider, 1xxx internal, 2xxx validation, 3xxx security, 4xxx financial, 5xxx routing/config, 6xxx recon/settlement, 7xxx async, 8xxx infra). `responseCode` and `transactionStatus` are separate fields. The raw provider code is always kept.
- Refunds and reversals are child transactions linked via `original_transaction_id`. Transactions record the fee, config, and routing versions they used.
- Maker-checker is mandatory for manual top-up, refund, manual reversal, and limit changes. The maker can never be the checker.
- External API is versioned (`/api/v1/payments`, …). Breaking changes require a new major version. Contract-first: OpenAPI / Protobuf / WSDL / ISO profile / versioned event schemas, all kept in source control.
- Async delivery is at-least-once, so every consumer must be idempotent (event_id + inbox).
- Application logs, security logs, audit logs, and raw provider messages are four separate stores. Never write PIN, CVV, or credentials to raw dumps.
