# RANSYS — Claude Code Implementation Handoff

You are acting as a **Senior .NET Software Architect and Backend Engineer** implementing **RANSYS**, a universal transaction switching and payment processing platform.

Your job is to implement the existing architecture faithfully.

## IMPORTANT

Do **not redesign the product**.

The architecture and financial principles in the supplied RANSYS documents are authoritative.

If implementation details are missing:

1. Prefer the simplest implementation compatible with the existing architecture.
2. Do not invent new business rules.
3. Do not weaken financial safety requirements.
4. Mark uncertain decisions clearly as `TODO / Architecture Decision Required`.
5. Do not silently change state transitions, ledger behavior, timeout behavior, idempotency semantics, or provider failover semantics.

---

# 1. Read These Documents First

Before writing code, read all RANSYS design documents in `/docs`.

Required documents:

```text
RANSYS_PRD_v1.0.md

RANSYS_Database_ERD_v1.0.md

RANSYS_Ledger_Posting_Rule_Matrix_v1.0.md

RANSYS_Transaction_State_Transition_Matrix_v1.0.md

RANSYS_Sequence_Diagram_Pack_v1.0.md

RANSYS_ERD_Physical_PostgreSQL_v1.1.md

RANSYS_PostgreSQL_Reference_DDL_v1.1.sql

RANSYS_Canonical_Data_Model_v1.0.md

RANSYS_Canonical_Contracts_v1.0.cs
```

Treat these documents as the current source of truth.

Before implementation, produce a short summary containing:

```text
Architecture understood
Financial invariants
Transaction invariants
Implementation assumptions
Open questions / TODOs
```

Do not start implementation until you understand the documents.

---

# 2. Technology Baseline

Use:

```text
C#
Modern .NET LTS
ASP.NET Core
PostgreSQL
EF Core where appropriate
Npgsql
Linux-compatible runtime
xUnit for tests
```

Architecture must remain:

```text
cloud agnostic
on-premise compatible
container ready
```

RabbitMQ must NOT be required.

Default async architecture:

```text
PostgreSQL Transactional Outbox
+
.NET Background Worker
```

---

# 3. Fundamental Financial Invariants

These rules are non-negotiable.

## 3.1 Fail Closed

Financial transactions must not reach a provider unless RANSYS has successfully persisted the internal transaction and balance reservation.

```text
Transaction DB unavailable
or
Ledger unavailable

=> financial transaction must fail closed
```

Never:

```text
call provider first
save transaction later
```

---

## 3.2 Wallet Cannot Be Negative

RANSYS uses:

```text
PREFUND ONLY
```

No overdraft.

Must always guarantee:

```text
available_balance >= 0
reserved_balance >= 0
```

---

## 3.3 Atomic Reservation

Balance check and reserve must occur atomically using PostgreSQL row locking.

Concept:

```sql
BEGIN;

SELECT wallet
FOR UPDATE;

CHECK available_balance;

CREATE transaction;
CREATE reservation;
CREATE ledger posting;
CREATE outbox event;

COMMIT;
```

Provider I/O must happen **after COMMIT**.

Never hold wallet locks while calling a provider.

---

## 3.4 Immutable Ledger

Never directly modify historical posted ledger entries.

Financial corrections require:

```text
compensating ledger transaction
```

All journal postings must satisfy:

```text
SUM(DEBIT) = SUM(CREDIT)
```

---

## 3.5 Unique Posting Key

Ledger posting must be idempotent.

Examples:

```text
TX:<transaction-id>:RESERVE
TX:<transaction-id>:POST
TX:<transaction-id>:RELEASE
TX:<transaction-id>:REVERSAL:<reference>
TX:<transaction-id>:REFUND:<reference>

TOPUP:<reference>
ADJUSTMENT:<reference>
```

A duplicate posting key must never cause duplicate balance mutation.

---

## 3.6 Timeout Is Not Failure

Critical rule:

```text
TIMEOUT != FAILED
```

If a financial request may have reached the provider but RANSYS did not receive a definitive response:

```text
processing_status = IN_DOUBT
financial_status = RESERVED
```

The reservation remains held.

Do not automatically:

```text
release wallet balance
mark transaction FAILED
send the same purchase to another provider
```

---

# 4. Provider Failover Rule

Safe failover is allowed only when RANSYS knows the financial request was not sent.

Critical field:

```text
TransactionAttempt.RequestSent
```

Rule:

```text
RequestSent = false
=> failover may be allowed

RequestSent = true
+ final result unknown
=> IN_DOUBT
=> no financial failover
```

---

# 5. Transaction Status Model

Do not collapse status into one field.

RANSYS has:

```text
ProcessingStatus
FinancialStatus
ReconciliationStatus
SettlementStatus
```

Example:

```text
ProcessingStatus      = SUCCESS
FinancialStatus       = POSTED
ReconciliationStatus  = EXCEPTION
SettlementStatus      = PENDING
```

This is valid.

---

# 6. Canonical Model Boundary

Do not use the same object as:

```text
EF Entity
API DTO
Provider DTO
Event Payload
Domain Aggregate
```

Keep them separated.

Expected flow:

```text
API DTO
  ↓
Application Command
  ↓
Domain / Canonical Model
  ↓
Persistence Mapping

and

Canonical Model
  ↓
Provider Contract
  ↓
Provider-specific Mapping
```

---

# 7. Initial Solution Structure

Create:

```text
Ransys.sln

src/
  Ransys.Domain/
  Ransys.Application/
  Ransys.Contracts/
  Ransys.Infrastructure/
  Ransys.Persistence.PostgreSql/
  Ransys.TransactionCore/
  Ransys.Ledger/
  Ransys.Routing/
  Ransys.Configuration/
  Ransys.Adapter.Contracts/
  Ransys.Adapter.Sdk/
  Ransys.Workers/
  Ransys.Api/

tests/
  Ransys.Domain.Tests/
  Ransys.Application.Tests/
  Ransys.Ledger.Tests/
  Ransys.Persistence.Tests/
  Ransys.TransactionCore.Tests/
  Ransys.IntegrationTests/

docs/
```

Do not create unnecessary microservices yet.

These are logical/component boundaries.

RANSYS Lite must still be able to package multiple components into one deployment.

---

# 8. Dependency Direction

Target dependency direction:

```text
Domain
↑
Application
↑
Infrastructure / API
```

`Ransys.Domain` must not reference:

```text
ASP.NET Core
Entity Framework Core
Npgsql
RabbitMQ
Redis
HTTP clients
```

---

# 9. Phase 1 Implementation Scope

Implement only the stable architecture first.

## Milestone 1 — Repository Foundation

Create:

```text
solution
projects
project references
Directory.Build.props
.editorconfig
nullable enabled
warnings configuration
test projects
basic README
```

Use consistent namespace:

```text
Ransys.*
```

---

# 10. Milestone 2 — Canonical Domain Types

Implement types defined in:

```text
RANSYS_Canonical_Data_Model_v1.0.md
RANSYS_Canonical_Contracts_v1.0.cs
```

Including:

```text
Money

TransactionId
MerchantId
ChannelId
ProductId
ProviderId
WalletId
AttemptId

TransactionIdentity
TransactionFingerprint

TransactionType

ProcessingStatus
FinancialStatus
ReconciliationStatus
SettlementStatus

FeeComponent
TransactionReferences
TransactionEndpoint
Customer
RoutingDecision
TransactionAttempt
```

Do not merely copy records without validation.

For example `Money` must enforce:

```text
currency code required
currency scale valid
amount precision valid
no incompatible implicit arithmetic
```

---

# 11. Milestone 3 — Transaction Aggregate

Create behavior-rich Transaction aggregate.

Avoid:

```csharp
transaction.ProcessingStatus = ProcessingStatus.Success;
```

from arbitrary code.

Prefer:

```csharp
transaction.Validate(...);
transaction.MarkReserved(...);
transaction.BeginProcessing(...);
transaction.MarkPending(...);
transaction.MarkInDoubt(...);
transaction.CompleteSuccess(...);
transaction.CompleteFailure(...);
transaction.BeginReversal(...);
transaction.CompleteReversal(...);
```

Each method must enforce the State Transition Matrix.

Invalid transition should produce a controlled domain failure.

---

# 12. Milestone 4 — PostgreSQL Persistence

Implement mappings corresponding to:

```text
RANSYS_ERD_Physical_PostgreSQL_v1.1.md
```

Tables include at minimum:

```text
core.merchants
core.channels
core.products
core.currency_definitions

core.transactions
core.transaction_fee_components
core.idempotency_records
core.transaction_attempts
core.transaction_state_history
core.topup_requests

ledger.wallets
ledger.balance_reservations
ledger.ledger_accounts
ledger.ledger_transactions
ledger.ledger_entries

integration.providers

config.config_versions

async.outbox_events
async.outbox_delivery_attempts
```

Do not create a new incompatible schema if the supplied physical design already defines it.

---

# 13. EF Core Guidance

Use EF Core where it improves maintainability.

However, critical financial operations must explicitly control:

```text
database transaction
row locking
posting idempotency
concurrency
```

It is acceptable to use raw SQL through Npgsql/EF for:

```sql
SELECT ... FOR UPDATE
```

Do not pretend EF optimistic concurrency alone solves wallet double spending.

---

# 14. Milestone 5 — Ledger Posting Service

Implement controlled:

```text
ILedgerPostingService
```

Operations should initially support:

```text
Reserve
PostPayment
ReleaseReservation
ReversePostedPayment
PostTopUp
PostRefund
CreditAdjustment
DebitAdjustment
```

All operations must:

```text
be atomic
use posting_key idempotency
create balanced ledger entries
update wallet projection consistently
produce outbox event where required
```

---

# 15. Required Ledger Tests

Implement tests for:

### Same Wallet Race

```text
Wallet available = 100,000

Transaction A wants 80,000
Transaction B wants 80,000
```

Expected:

```text
only one reservation succeeds
other transaction receives insufficient balance
wallet never negative
```

### Duplicate Reserve

Same posting key twice.

Expected:

```text
one reservation effect only
```

### Duplicate Success

Expected:

```text
one POST effect only
```

### Failure Release

Expected:

```text
available restored exactly
reserved becomes zero
```

### IN_DOUBT

Expected:

```text
reservation remains active
balance is not released
```

### Reversal

Expected:

```text
new compensating journal
original journal unchanged
```

---

# 16. Milestone 6 — Idempotency Service

Implement:

```text
24-hour active client reference window
```

Behavior:

```text
same reference + same fingerprint
=> existing transaction

same reference + different fingerprint
=> DUPLICATE_REFERENCE_CONFLICT
```

Do not confuse business idempotency with security nonce.

---

# 17. Milestone 7 — Transaction Attempt Model

Implement persistence and domain/application handling for:

```text
AttemptNumber
AttemptType
ProviderId
RequestSent
TransportStatus
ProviderResponseCode
ProviderReference
STAN
RRN
Latency
RawMessageReference
CorrelationId
TraceId
```

Make `RequestSent` explicit and tested.

---

# 18. Milestone 8 — Transactional Outbox

Implement:

```text
OutboxEvent
Outbox Worker
```

Claim pattern:

```sql
SELECT ...
FOR UPDATE SKIP LOCKED
```

Delivery semantics:

```text
AT LEAST ONCE
```

Consumer must eventually support inbox/dedup.

No RabbitMQ dependency is required.

---

# 19. Milestone 9 — Routing Foundation

Implement only baseline routing:

```text
Primary
Secondary
Fallback
```

based on priority.

Respect:

```text
provider enabled
manual disable
circuit state
capability
```

Do not implement:

```text
weighted routing
least cost
smart routing
hierarchical routing
```

yet.

---

# 20. Do Not Implement Yet

Do NOT permanently implement these contracts until their design documents are completed:

```text
Public OpenAPI v1
full SIGNED_API HTTP contract
Provider Adapter Contract final interface
final response code catalog
full configuration schema API
SOAP external contract
ISO8583 message profiles
Backoffice UI
Settlement payout integration
advanced reconciliation rules
RabbitMQ deployment
fraud engine
risk engine
weighted routing
multi-currency merchant wallet
credit facility
```

You may create placeholders/interfaces if required, but mark them clearly.

---

# 21. Test Infrastructure

Use a real PostgreSQL instance for integration tests where database semantics matter.

Do not rely exclusively on:

```text
EF InMemory provider
SQLite
mocked repository
```

for tests involving:

```text
SELECT FOR UPDATE
concurrent wallet reservation
unique posting key
PostgreSQL transaction isolation
partial unique indexes
outbox SKIP LOCKED
```

Use Docker/Testcontainers if appropriate.

---

# 22. Concurrency Tests Are Mandatory

Especially test:

```text
multiple threads/tasks
same wallet
same client reference
duplicate provider result
callback vs recovery worker
status check vs reconciliation resolution
```

---

# 23. Observability Foundation

Every application operation should be capable of propagating:

```text
ransys_transaction_id
correlation_id
trace_id
```

Use structured logging.

Do not put secrets or sensitive raw payloads into normal application log.

---

# 24. Coding Standards

Use:

```text
nullable reference types
async/await
CancellationToken
dependency injection
small focused services
explicit domain results
structured logging
```

Avoid:

```text
God services
static mutable global state
generic Dictionary<string, object> domain models
catch(Exception) and ignore
hidden retries
silent financial correction
```

---

# 25. Financial Error Handling

Expected financial/business outcomes should return controlled result.

Examples:

```text
INSUFFICIENT_BALANCE
DUPLICATE_REFERENCE_CONFLICT
INVALID_STATE_TRANSITION
POSTING_ALREADY_EXISTS
```

Do not use exceptions as ordinary business branching.

Unexpected database/programming failure may use exceptions and should cause rollback/fail closed.

---

# 26. Commit Strategy

Work incrementally.

Recommended commits:

```text
chore: initialize ransys solution

feat(domain): add canonical money and transaction identities

feat(domain): implement transaction state machine

feat(persistence): add postgres entity mappings

feat(ledger): implement atomic balance reservation

feat(ledger): implement payment finalization

feat(core): add idempotency protection

feat(core): add transaction attempts

feat(async): add transactional outbox

feat(routing): add priority routing baseline

test(financial): add wallet concurrency scenarios
```

Keep commits reviewable.

---

# 27. Documentation During Coding

When implementation discovers a conflict with the architecture documents:

Do not silently choose a solution.

Create:

```text
/docs/decisions/ADR-xxx-<topic>.md
```

with:

```text
Context
Existing RANSYS rule
Technical issue
Options
Recommended option
Consequences
```

Do not treat the ADR as approved architecture until reviewed.

---

# 28. Definition of Done — Phase 1

Phase 1 is complete when:

- solution builds successfully;
- domain project has no infrastructure dependency;
- canonical types are implemented;
- Transaction aggregate enforces valid transitions;
- PostgreSQL schema/migrations exist;
- wallet reserve is atomic;
- ledger postings balance;
- duplicate posting keys are safe;
- idempotency behavior works;
- transaction attempts work;
- outbox works brokerless;
- same-wallet concurrent spend cannot create negative balance;
- timeout can remain `IN_DOUBT` with reserve held;
- integration tests use actual PostgreSQL semantics;
- documentation lists any unresolved architecture decisions.

---

# 29. First Action

Do not immediately generate hundreds of files.

First:

1. inspect the repository;
2. read all RANSYS documents;
3. produce an implementation plan;
4. map each planned component back to its authoritative RANSYS document;
5. identify unresolved decisions;
6. then implement Milestone 1.

After Milestone 1, run:

```text
dotnet build
dotnet test
```

Fix errors before progressing.

Continue milestone by milestone while keeping the repository buildable.
