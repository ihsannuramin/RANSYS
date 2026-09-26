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
| `Ransys.Adapter.Contracts` / `Ransys.Adapter.Sdk` | Provider adapter boundary (placeholders pending Provider Adapter Contract v1). |
| `Ransys.Workers` | Background host: transactional outbox worker, idempotency expiry sweep. |
| `Ransys.Api` | API host / composition root (public endpoints pending OpenAPI v1). |

Dependency direction: `Domain ← Application ← Infrastructure / API`, enforced by `tests/Ransys.IntegrationTests/Architecture/DependencyRulesTests.cs`.

## Implementation status (Phase 1)

| Milestone | Status |
|---|---|
| 1 Repository foundation | Done |
| 2 Canonical domain types | Done |
| 3 Transaction aggregate | Done |
| 4 PostgreSQL persistence | Pending |
| 5 Ledger posting service | Pending |
| 6 Idempotency service | Pending |
| 7 Transaction attempts | Pending |
| 8 Transactional outbox | Pending |
| 9 Routing foundation | Pending |
| 10 Concurrency scenarios | Pending |

## Unresolved architecture decisions

Accepted ADRs are listed in [`docs/decisions/`](docs/decisions/). Items still open:

- Final Chart of Accounts codes (placeholders per ADR-007).
- Refund fee policy (ADR-010).
- Original provider result arriving while a reversal of an unposted transaction is pending (ADR-012, interim: rejected without mutation).
- Original-side refund summary transitions (PS-11..PS-13) wait for the refund use case.
- Items deferred to later design documents: OpenAPI v1, SIGNED_API contract, Provider Adapter Contract v1, response code catalog, configuration schema, SOAP/ISO8583 profiles.
