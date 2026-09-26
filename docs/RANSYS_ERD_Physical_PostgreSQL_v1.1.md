# RANSYS ERD v1.1 & Physical PostgreSQL Schema Design

**Document Type:** Detailed Engineering Design — Physical Database  
**Product:** RANSYS Universal Transaction Switching & Processing Platform  
**Database:** PostgreSQL  
**Version:** 1.1  
**Status:** Engineering Baseline Draft  
**Depends On:** PRD v1.0, Database ERD v1.0, Ledger Posting Rule Matrix v1.0, Transaction State Transition Matrix v1.0, Sequence Diagram Pack v1.0

---

# 1. Purpose

Dokumen ini mengubah logical ERD RANSYS menjadi physical PostgreSQL design yang dapat dijadikan dasar migration/DDL.

Scope:

- exact schema/table names;
- PostgreSQL data types;
- primary/foreign keys;
- unique/partial indexes;
- monetary precision;
- state columns;
- idempotency;
- transaction attempts;
- wallet/reservation;
- double-entry ledger;
- posting idempotency;
- fee components;
- top-up;
- provider/routing/configuration;
- transactional outbox;
- Backoffice replication;
- reconciliation/settlement;
- IAM/maker-checker;
- audit;
- database roles;
- locking conventions;
- partitioning/retention strategy;
- zero-downtime migration conventions.

---

# 2. Locked Product Decisions vs Engineering Defaults

## 2.1 Locked

```text
PostgreSQL financial source of truth
Prefund only
No negative merchant balance
Atomic wallet reserve
Immutable ledger
Double-entry posting
Provider call after reserve COMMIT
IN_DOUBT keeps hold
TransactionAttempt separate from Transaction
Transactional Outbox mandatory
Backoffice DB separate
Idempotency window 24h
RabbitMQ optional
Financial failure = fail closed
```

## 2.2 Engineering Defaults in v1.1

These are defaults and may be revised by benchmark/security review without changing product semantics:

```text
ID type                    UUID
ID generation              UUIDv7 from application/common library
Money storage              NUMERIC(30,8)
Rate/ratio storage         NUMERIC(30,12)
Timestamp                  TIMESTAMPTZ
State storage              VARCHAR + named CHECK constraints
JSON extension             JSONB
IP                         INET
Transaction table          initially non-partitioned reference DDL
Enterprise partitioning    range partition extension after benchmark
```

---

# 3. Why PostgreSQL Native ENUM Is Not the Baseline

RANSYS needs backward-compatible database migration.

Using:

```text
VARCHAR + named CHECK constraint
```

makes expand/deploy/contract easier than coupling every application rollout to native enum alteration.

Example:

```sql
processing_status varchar(32) NOT NULL
```

with a named check constraint.

When a new state is introduced:

```text
EXPAND constraint
-> deploy compatible app
-> migrate/backfill
-> CONTRACT old behavior later
```

---

# 4. Monetary Precision

Physical baseline:

```text
Money         NUMERIC(30,8)
Rates/Ratios  NUMERIC(30,12)
```

Reasons:

- never use float/double for financial value;
- supports current IDR;
- does not hardcode zero-decimal IDR;
- allows currency definition scale changes;
- supports future redenomination/conversion;
- offers large headroom for enterprise transaction values.

Currency master controls the valid scale used by a currency definition.

Application must reject an amount whose fractional precision exceeds `currency_definitions.scale`.

---

# 5. ID Strategy

Use PostgreSQL `uuid` columns.

Recommended generation:

```text
UUIDv7 generated in RANSYS application/common library
```

Reason:

- globally unique;
- time-sortable;
- no DB round trip to allocate ID;
- works across independently deployed services.

Database does not assume the UUID version for relational integrity.

---

# 6. Physical Database Boundaries

## Transaction Database

Schemas:

```text
core
ledger
integration
config
async
```

## Backoffice Database

Schemas:

```text
bo
recon
settlement
iam
audit
```

No cross-database FK.

Cross-database references are logical IDs.

---

# 7. Transaction Database — Core Tables

## 7.1 `core.merchants`

```text
merchant_id       uuid PK
merchant_code     varchar(64) UNIQUE NOT NULL
merchant_name     varchar(200) NOT NULL
status            varchar(32) NOT NULL
created_at        timestamptz NOT NULL
updated_at        timestamptz NOT NULL
```

---

## 7.2 `core.channels`

```text
channel_id        uuid PK
merchant_id       uuid FK -> core.merchants
channel_code      varchar(64) NOT NULL
channel_type      varchar(32) NOT NULL
auth_profile      varchar(64) NOT NULL
status            varchar(32) NOT NULL
created_at        timestamptz NOT NULL
updated_at        timestamptz NOT NULL

UNIQUE(merchant_id, channel_code)
```

---

## 7.3 `core.products`

```text
product_id        uuid PK
product_code      varchar(64) UNIQUE NOT NULL
product_name      varchar(200) NOT NULL
category          varchar(64) NOT NULL
status            varchar(32) NOT NULL
metadata_schema   jsonb NULL
created_at        timestamptz NOT NULL
updated_at        timestamptz NOT NULL
```

---

## 7.4 `core.currency_definitions`

```text
currency_definition_id uuid PK
currency_code          char(3) NOT NULL
version_no             integer NOT NULL
scale                  smallint NOT NULL
conversion_ratio       numeric(30,12) NULL
effective_from         timestamptz NOT NULL
effective_until        timestamptz NULL
status                 varchar(32) NOT NULL
created_at             timestamptz NOT NULL

UNIQUE(currency_code, version_no)
CHECK(scale BETWEEN 0 AND 8)
```

Historical transaction points to the exact currency definition used at transaction creation.

---

# 8. `core.transactions`

Primary business transaction aggregate.

```text
ransys_transaction_id       uuid PK
merchant_id                 uuid NOT NULL FK
channel_id                  uuid NOT NULL FK
product_id                  uuid NOT NULL FK
transaction_type            varchar(32) NOT NULL

client_reference            varchar(128) NOT NULL
idempotency_key             varchar(128) NULL
transaction_fingerprint     varchar(128) NOT NULL

original_transaction_id     uuid NULL
amount                      numeric(30,8) NOT NULL
currency_definition_id      uuid NOT NULL FK

merchant_charge_amount      numeric(30,8) NOT NULL DEFAULT 0
total_reserve_amount        numeric(30,8) NOT NULL DEFAULT 0

processing_status           varchar(32) NOT NULL
financial_status            varchar(32) NOT NULL
reconciliation_status       varchar(32) NOT NULL
settlement_status           varchar(32) NOT NULL

ransys_response_code        char(4) NULL
reason_code                 varchar(64) NULL
reason_description          varchar(500) NULL

initial_selected_provider_id uuid NULL
actual_provider_id          uuid NULL

config_version_id           uuid NULL
routing_config_version_id   uuid NULL
fee_config_version_id       uuid NULL
provider_policy_version     bigint NULL

metadata                    jsonb NOT NULL DEFAULT '{}'

received_at                 timestamptz NOT NULL
validated_at                timestamptz NULL
financial_posted_at         timestamptz NULL
completed_at                timestamptz NULL
updated_at                  timestamptz NOT NULL

row_version                 bigint NOT NULL DEFAULT 1
```

## Checks

```text
amount >= 0
merchant_charge_amount >= 0
total_reserve_amount >= 0
row_version > 0
```

## Indexes

```text
(merchant_id, received_at DESC)
(channel_id, client_reference)
(product_id, received_at DESC)
(processing_status, received_at)
(financial_status, received_at)
(actual_provider_id, received_at)
(original_transaction_id)
(ransys_response_code, received_at)
```

Do not add GIN metadata index by default. Add only when a production query requires it.

---

# 9. Transaction Status Constraints

## Processing

```text
RECEIVED
VALIDATED
PROCESSING
PENDING
IN_DOUBT
SUCCESS
FAILED
REVERSAL_PENDING
REVERSED
REFUND_PENDING
PARTIALLY_REFUNDED
REFUNDED
```

## Financial

```text
NONE
RESERVED
POSTED
RELEASED
REVERSAL_PENDING
REVERSED
REFUND_PENDING
PARTIALLY_REFUNDED
REFUNDED
ADJUSTED
```

## Reconciliation

```text
UNMATCHED
PENDING
MATCHED
EXCEPTION
RESOLVED
```

## Settlement

```text
NOT_APPLICABLE
PENDING
INCLUDED
APPROVED
READY_TO_PAY
SETTLED
ADJUSTED
```

Application still validates allowed **transition**, not only allowed enum value.

---

# 10. `core.transaction_fee_components`

Refinement from Ledger Matrix.

A single merchant charge may be composed of several accounting/business components.

```text
fee_component_id             uuid PK
ransys_transaction_id        uuid NOT NULL FK
component_type               varchar(64) NOT NULL
charged_amount               numeric(30,8) NOT NULL
accounting_amount            numeric(30,8) NOT NULL
currency_definition_id       uuid NOT NULL FK
beneficiary_type             varchar(32) NOT NULL
beneficiary_id               uuid NULL
refundable                   boolean NOT NULL DEFAULT false
calculation_rule_version     bigint NULL
created_at                   timestamptz NOT NULL
```

Examples:

```text
MERCHANT_SERVICE_FEE
PROVIDER_FEE
COMMISSION
TAX
RANSYS_MARGIN
OTHER
```

This prevents:

```text
merchant fee == automatically RANSYS revenue
```

---

# 11. `core.idempotency_records`

```text
idempotency_record_id       uuid PK
channel_id                  uuid NOT NULL FK
client_reference            varchar(128) NOT NULL
idempotency_key             varchar(128) NULL
fingerprint                 varchar(128) NOT NULL
ransys_transaction_id       uuid NOT NULL
active                      boolean NOT NULL DEFAULT true
created_at                  timestamptz NOT NULL
expires_at                  timestamptz NOT NULL
expired_at                  timestamptz NULL
```

Critical index:

```sql
CREATE UNIQUE INDEX ux_idempotency_active_reference
ON core.idempotency_records(channel_id, client_reference)
WHERE active = true;
```

The 24-hour rule is enforced by lifecycle:

```text
create active record
expires_at = created_at + 24h
safe expiry worker sets active=false
```

Do not simply delete immediately at 24h; retain briefly for operations if desired.

---

# 12. `core.transaction_attempts`

```text
transaction_attempt_id      uuid PK
ransys_transaction_id       uuid NOT NULL FK
attempt_no                  integer NOT NULL
attempt_type                varchar(32) NOT NULL
provider_id                 uuid NOT NULL

request_sent                boolean NOT NULL DEFAULT false
transport_status            varchar(32) NOT NULL
provider_transaction_status varchar(32) NULL

ransys_response_code        char(4) NULL
provider_response_code      varchar(64) NULL
provider_response_message   varchar(500) NULL

provider_reference          varchar(128) NULL
provider_stan               varchar(32) NULL
provider_rrn                varchar(64) NULL

latency_ms                  integer NULL
raw_request_reference       varchar(1000) NULL
raw_response_reference      varchar(1000) NULL

correlation_id              varchar(128) NOT NULL
trace_id                    varchar(128) NOT NULL

provider_sent_at            timestamptz NULL
provider_response_at        timestamptz NULL
created_at                  timestamptz NOT NULL

metadata                    jsonb NOT NULL DEFAULT '{}'

UNIQUE(ransys_transaction_id, attempt_no)
```

`request_sent` is safety-critical:

```text
false -> failover may be safe
true + unknown result -> IN_DOUBT
```

---

# 13. `core.transaction_state_history`

Append-only.

```text
history_id                  uuid PK
ransys_transaction_id       uuid NOT NULL FK
transaction_attempt_id      uuid NULL FK
previous_status             varchar(32) NULL
new_status                  varchar(32) NOT NULL
reason_code                 varchar(64) NULL
reason_description          varchar(500) NULL
change_source               varchar(64) NOT NULL
created_at                  timestamptz NOT NULL
```

No UPDATE/DELETE by normal application role.

---

# 14. `core.topup_requests`

Refinement from Ledger Matrix.

```text
topup_id                    uuid PK
merchant_id                 uuid NOT NULL FK
wallet_id                   uuid NOT NULL
topup_reference             varchar(128) NOT NULL
amount                      numeric(30,8) NOT NULL
currency_definition_id      uuid NOT NULL FK

status                      varchar(32) NOT NULL
external_fund_reference     varchar(256) NULL
approval_request_id         uuid NULL

verified_at                 timestamptz NULL
posted_at                   timestamptz NULL
created_at                  timestamptz NOT NULL
updated_at                  timestamptz NOT NULL

UNIQUE(merchant_id, topup_reference)
CHECK(amount > 0)
```

Lifecycle:

```text
DRAFT
PENDING_APPROVAL
APPROVED
VERIFIED
POSTED
REJECTED
FAILED
```

Approval ID is a logical cross-database reference to Backoffice IAM.

---

# 15. Ledger Schema

## 15.1 `ledger.wallets`

```text
wallet_id                   uuid PK
merchant_id                 uuid NOT NULL FK
product_id                  uuid NULL FK
currency_definition_id      uuid NOT NULL FK

ledger_balance              numeric(30,8) NOT NULL DEFAULT 0
available_balance           numeric(30,8) NOT NULL DEFAULT 0
reserved_balance            numeric(30,8) NOT NULL DEFAULT 0

status                      varchar(32) NOT NULL
version_no                  bigint NOT NULL DEFAULT 1

created_at                  timestamptz NOT NULL
updated_at                  timestamptz NOT NULL

CHECK(ledger_balance >= 0)
CHECK(available_balance >= 0)
CHECK(reserved_balance >= 0)
CHECK(version_no > 0)
```

Recommended business uniqueness:

Main wallet:

```text
one wallet per merchant + currency definition
```

Product-specific wallet is future.

Because PostgreSQL treats NULL values as distinct in normal unique constraints, use partial indexes:

```text
UNIQUE merchant main wallet when product_id IS NULL
UNIQUE merchant/product wallet when product_id IS NOT NULL
```

---

# 16. `ledger.balance_reservations`

```text
reservation_id              uuid PK
wallet_id                   uuid NOT NULL FK
ransys_transaction_id       uuid NOT NULL

principal_amount            numeric(30,8) NOT NULL
fee_amount                  numeric(30,8) NOT NULL DEFAULT 0
total_reserved_amount       numeric(30,8) NOT NULL

status                      varchar(32) NOT NULL
hold_reason                 varchar(64) NOT NULL

created_at                  timestamptz NOT NULL
committed_at                timestamptz NULL
released_at                 timestamptz NULL
warning_at                  timestamptz NULL
critical_at                 timestamptz NULL

CHECK(principal_amount >= 0)
CHECK(fee_amount >= 0)
CHECK(total_reserved_amount = principal_amount + fee_amount)
```

One active business reservation per financial transaction.

No automatic release based only on age.

---

# 17. `ledger.ledger_accounts`

```text
ledger_account_id           uuid PK
account_code                varchar(128) UNIQUE NOT NULL
account_name                varchar(200) NOT NULL

owner_type                  varchar(32) NOT NULL
owner_id                    uuid NULL
wallet_id                   uuid NULL FK

account_class               varchar(32) NOT NULL
account_type                varchar(64) NOT NULL
currency_definition_id      uuid NOT NULL FK

normal_side                 char(1) NOT NULL
status                      varchar(32) NOT NULL
created_at                  timestamptz NOT NULL

CHECK(normal_side IN ('D','C'))
```

Account classes:

```text
ASSET
LIABILITY
REVENUE
EXPENSE
CONTROL
```

Examples:

```text
MERCHANT_AVAILABLE
MERCHANT_RESERVED
PROVIDER_PAYABLE
PROVIDER_RECEIVABLE
RANSYS_FEE_REVENUE
TAX_PAYABLE
CASH_CLEARING
ADJUSTMENT_CLEARING
```

---

# 18. `ledger.ledger_transactions`

```text
ledger_transaction_id            uuid PK
posting_key                      varchar(200) UNIQUE NOT NULL

ransys_transaction_id            uuid NULL
reservation_id                   uuid NULL FK
approval_request_id              uuid NULL

operation_type                   varchar(64) NOT NULL
business_reference               varchar(128) NOT NULL
description                      varchar(500) NOT NULL

status                           varchar(32) NOT NULL
effective_at                     timestamptz NOT NULL
created_at                       timestamptz NOT NULL

created_by_type                  varchar(32) NOT NULL
created_by_id                    uuid NULL

compensates_ledger_transaction_id uuid NULL FK
```

`posting_key` is the financial idempotency barrier.

Examples:

```text
TX:<id>:RESERVE
TX:<id>:POST
TX:<id>:RELEASE
TX:<id>:REVERSAL:<ref>
TX:<id>:REFUND:<ref>
TOPUP:<ref>
ADJUSTMENT:<ref>
```

---

# 19. `ledger.ledger_entries`

```text
ledger_entry_id             uuid PK
ledger_transaction_id       uuid NOT NULL FK
ledger_account_id           uuid NOT NULL FK

entry_sequence              smallint NOT NULL
entry_side                  char(1) NOT NULL
amount                      numeric(30,8) NOT NULL
currency_definition_id      uuid NOT NULL FK
created_at                  timestamptz NOT NULL

UNIQUE(ledger_transaction_id, entry_sequence)

CHECK(entry_side IN ('D','C'))
CHECK(amount > 0)
```

All entries in one posting use the same currency definition in baseline.

---

# 20. Ledger Balance Enforcement

Cross-row balance cannot be represented by a normal CHECK constraint.

Reference implementation uses a **DEFERRABLE CONSTRAINT TRIGGER**.

At transaction commit, for every posted journal:

```text
SUM(debit) == SUM(credit)
number_of_entries >= 2
single currency_definition_id
```

If not:

```text
RAISE EXCEPTION
ROLLBACK
```

Production posting should still go through controlled `LedgerPostingService`.

The DB trigger is the last line of defense, not the business API.

---

# 21. Ledger Immutability Enforcement

For `status='POSTED'`:

```text
ledger_transactions cannot be deleted
ledger_entries cannot be updated
ledger_entries cannot be deleted
```

Correction:

```text
new compensating ledger_transaction
```

Database role privileges + trigger protection both apply.

---

# 22. Wallet Update Strategy

Baseline:

```text
Application transaction orchestrated by Transaction Core/LedgerPostingService
```

Not a generic stored procedure for every business flow.

Rationale:

- business rules remain testable in C#;
- PostgreSQL enforces final integrity;
- avoids putting provider/state-machine business logic in stored procedures.

Critical DB atomicity still required:

```text
lock wallet
update wallet projection
insert reservation
insert journal
insert entries
insert outbox
commit
```

---

# 23. Required Locking Convention

Reserve path:

```sql
SELECT *
FROM ledger.wallets
WHERE wallet_id = :wallet_id
FOR UPDATE;
```

Finalization recommended lock order:

```text
1. core.transactions
2. ledger.wallets
3. ledger.balance_reservations
4. ledger posting_key check/insert
```

All financial code paths must follow the same order.

Provider call is outside that database transaction.

---

# 24. Integration Schema

## `integration.providers`

```text
provider_id                uuid PK
provider_code              varchar(64) UNIQUE NOT NULL
provider_name              varchar(200) NOT NULL
provider_type              varchar(64) NOT NULL
adapter_service_name       varchar(128) NOT NULL
protocol_type              varchar(32) NOT NULL
status                     varchar(32) NOT NULL
created_at                 timestamptz NOT NULL
updated_at                 timestamptz NOT NULL
```

---

## `integration.provider_endpoints`

```text
provider_endpoint_id       uuid PK
provider_id                uuid NOT NULL FK
environment                varchar(32) NOT NULL
endpoint_type              varchar(32) NOT NULL
host_or_url                varchar(1000) NOT NULL
port                       integer NULL
secret_reference           varchar(256) NULL
status                     varchar(32) NOT NULL
created_at                 timestamptz NOT NULL
updated_at                 timestamptz NOT NULL
```

Actual secret value is not stored here.

---

## `integration.provider_capabilities`

```text
provider_id                uuid NOT NULL FK
capability_code            varchar(64) NOT NULL
enabled                    boolean NOT NULL
config                     jsonb NOT NULL DEFAULT '{}'
updated_at                 timestamptz NOT NULL

PK(provider_id, capability_code)
```

---

## `integration.provider_transaction_policies`

```text
policy_id                  uuid PK
provider_id                uuid NOT NULL FK
product_id                 uuid NOT NULL FK
transaction_type           varchar(32) NOT NULL

connect_timeout_ms         integer NOT NULL
read_timeout_ms            integer NOT NULL
max_retry                  smallint NOT NULL DEFAULT 0
retry_delay_ms             integer NOT NULL DEFAULT 0
retry_backoff_type         varchar(32) NOT NULL

status_check_after_timeout boolean NOT NULL
reversal_after_timeout     boolean NOT NULL

max_concurrent_requests    integer NULL
max_queue_depth            integer NULL
rate_limit_per_second      integer NULL

effective_from             timestamptz NOT NULL
effective_until            timestamptz NULL
version_no                 bigint NOT NULL

CHECK(connect_timeout_ms > 0)
CHECK(read_timeout_ms > 0)
CHECK(max_retry >= 0)
```

---

## `integration.provider_response_mappings`

```text
mapping_id                 uuid PK
provider_id                uuid NOT NULL FK
transaction_type           varchar(32) NULL
provider_response_code     varchar(64) NOT NULL
ransys_response_code       char(4) NOT NULL
normalized_status          varchar(32) NOT NULL
description                varchar(500) NULL
config_version_id          uuid NOT NULL
effective_from             timestamptz NOT NULL
effective_until            timestamptz NULL
created_at                 timestamptz NOT NULL
```

---

## `integration.provider_operational_state`

```text
provider_id                uuid PK FK
manual_enabled             boolean NOT NULL DEFAULT true
manual_disable_reason      varchar(500) NULL
manual_disabled_until      timestamptz NULL

health_state               varchar(32) NOT NULL
circuit_state              varchar(32) NOT NULL

state_changed_at           timestamptz NOT NULL
updated_at                 timestamptz NOT NULL
```

Time-series metrics stay in monitoring platform, not this table.

---

# 25. Configuration Schema

## `config.config_versions`

```text
config_version_id          uuid PK
config_domain              varchar(64) NOT NULL
version_no                 bigint NOT NULL
status                     varchar(32) NOT NULL
effective_from             timestamptz NULL
effective_until            timestamptz NULL

created_by                 uuid NOT NULL
approved_by                uuid NULL

created_at                 timestamptz NOT NULL
approved_at                timestamptz NULL
activated_at               timestamptz NULL

UNIQUE(config_domain, version_no)
```

---

## `config.routing_routes`

```text
routing_route_id           uuid PK
config_version_id          uuid NOT NULL FK
product_id                 uuid NOT NULL FK
transaction_type           varchar(32) NULL
provider_id                uuid NOT NULL FK
priority                   smallint NOT NULL
enabled                    boolean NOT NULL
created_at                 timestamptz NOT NULL

CHECK(priority > 0)
```

No weight in v1.

---

## `config.fee_rules`

```text
fee_rule_id                uuid PK
config_version_id          uuid NOT NULL FK
merchant_id                uuid NULL FK
product_id                 uuid NOT NULL FK
transaction_type           varchar(32) NOT NULL

fee_type                   varchar(32) NOT NULL
fee_value                  numeric(30,12) NOT NULL
minimum_fee                numeric(30,8) NULL
maximum_fee                numeric(30,8) NULL
currency_definition_id     uuid NOT NULL FK

effective_from             timestamptz NOT NULL
effective_until            timestamptz NULL

created_at                 timestamptz NOT NULL
```

Actual calculated amount is copied into transaction + fee components.

---

# 26. Secrets

Provider endpoint stores only:

```text
secret_reference
```

A separate encrypted secret store may use a table such as:

```text
config.secret_records
```

but its cryptographic design is intentionally kept outside this ERD baseline because `ISecretProvider` must allow replacement with Vault/HSM/cloud secret manager.

No provider password/API secret appears in ordinary endpoint rows.

---

# 27. Async Schema

## `async.outbox_events`

```text
event_id                   uuid PK
aggregate_type             varchar(64) NOT NULL
aggregate_id               uuid NOT NULL
event_type                 varchar(128) NOT NULL
event_version              integer NOT NULL
source_version             bigint NULL

payload                    jsonb NOT NULL
status                     varchar(32) NOT NULL

retry_count                integer NOT NULL DEFAULT 0
next_retry_at              timestamptz NULL

locked_by                  varchar(128) NULL
locked_at                  timestamptz NULL

created_at                 timestamptz NOT NULL
published_at               timestamptz NULL

CHECK(retry_count >= 0)
```

Critical worker index:

```text
(status, next_retry_at, created_at)
```

Claim via:

```sql
FOR UPDATE SKIP LOCKED
```

---

## `async.outbox_delivery_attempts`

```text
delivery_attempt_id        uuid PK
event_id                   uuid NOT NULL FK
attempt_no                 integer NOT NULL
transport                  varchar(32) NOT NULL
result                     varchar(32) NOT NULL
error_code                 varchar(64) NULL
error_message              varchar(1000) NULL
started_at                 timestamptz NOT NULL
completed_at               timestamptz NULL

UNIQUE(event_id, attempt_no)
```

---

# 28. Backoffice Database

Backoffice is a historical/query projection, not financial truth.

Important:

```text
Backoffice may be rebuilt
Core ledger cannot be reconstructed from Backoffice as an authority
```

---

# 29. `bo.transactions`

Recommended physical columns:

```text
ransys_transaction_id      uuid PK
source_version             bigint NOT NULL

merchant_id                uuid NOT NULL
merchant_code              varchar(64) NOT NULL
channel_id                 uuid NOT NULL
product_id                 uuid NOT NULL
transaction_type           varchar(32) NOT NULL

client_reference           varchar(128) NOT NULL
customer_identifier        varchar(256) NULL

amount                     numeric(30,8) NOT NULL
currency_code              char(3) NOT NULL
currency_definition_version integer NOT NULL
merchant_charge_amount     numeric(30,8) NOT NULL

processing_status          varchar(32) NOT NULL
financial_status           varchar(32) NOT NULL
reconciliation_status      varchar(32) NOT NULL
settlement_status          varchar(32) NOT NULL

ransys_response_code       char(4) NULL
reason_code                varchar(64) NULL

initial_selected_provider_id uuid NULL
actual_provider_id         uuid NULL
provider_reference         varchar(128) NULL
stan                       varchar(32) NULL
rrn                        varchar(64) NULL

received_at                timestamptz NOT NULL
financial_posted_at        timestamptz NULL
completed_at               timestamptz NULL
replicated_at              timestamptz NOT NULL

metadata                   jsonb NOT NULL DEFAULT '{}'
```

Broad search indexes live here, not in Core DB.

---

# 30. `bo.inbox_events`

```text
event_id                   uuid PK
event_type                 varchar(128) NOT NULL
event_version              integer NOT NULL
source_version             bigint NULL
received_at                timestamptz NOT NULL
processed_at               timestamptz NULL
status                     varchar(32) NOT NULL
error_message              varchar(1000) NULL
```

Consumer rule:

```text
deduplicate event_id
compare source_version
never overwrite newer projection with older event
```

---

# 31. Backoffice Transaction Search Indexes

Recommended:

```text
(client_reference)
(provider_reference)
(stan)
(rrn)
(customer_identifier)
(received_at DESC)
(processing_status, received_at DESC)
(product_id, received_at DESC)
(merchant_id, received_at DESC)
(amount, received_at DESC)
```

Composite/index selection must be confirmed with real query workload.

---

# 32. Reconciliation Tables

Physical entities remain:

```text
recon.recon_batches
recon.recon_source_files
recon.recon_items
recon.recon_resolutions
```

Important constraint:

Recon is not allowed to write Core ledger tables directly.

It invokes controlled Core financial resolution.

---

# 33. Settlement Tables

Physical entities remain:

```text
settlement.settlement_periods
settlement.settlement_batches
settlement.settlement_items
settlement.settlement_adjustments
settlement.business_date_closures
```

Historical settlement corrections use adjustments.

Actual payout is outside baseline scope.

---

# 34. IAM and Approval Tables

```text
iam.users
iam.roles
iam.permissions
iam.user_roles
iam.role_permissions
iam.user_scopes
iam.approval_requests
iam.approval_actions
```

Mandatory maker-checker protection:

```text
maker_user_id != checker_user_id
```

Because approval lives in Backoffice DB and posting lives in Transaction DB:

```text
approval_request_id is a logical immutable reference
```

not a cross-database FK.

---

# 35. Audit

`audit.audit_events` is append-only.

```text
audit_event_id       uuid PK
actor_type           varchar(32)
actor_id             varchar(128)
action               varchar(128)
entity_type          varchar(64)
entity_id            varchar(128)
before_value         jsonb
after_value          jsonb
reason               varchar(1000)
session_id           varchar(128)
source_ip            inet
approval_request_id  uuid
created_at           timestamptz
```

Standard application role has no UPDATE/DELETE.

---

# 36. Customer Identifier

Physical baseline:

```text
core transaction canonical/customer field:
varchar(256)
```

but do not create broad indexes on Core unless required.

Backoffice may index its searchable representation.

Future masking/tokenization can change presentation/storage protection without changing transaction identity semantics.

---

# 37. Raw Message References

Use URI-style opaque reference:

```text
rawmsg://provider-a/2026/09/26/<id>
```

rather than assuming local filesystem path.

This allows:

```text
Lite -> local filesystem
Enterprise -> object storage/NFS/secure archive
```

without schema change.

DB field remains `varchar(1000)`.

---

# 38. Partitioning Decision

## Reference DDL v1.1

Start with **non-partitioned tables** for:

```text
core.transactions
core.transaction_attempts
core.transaction_state_history
async.outbox_events
```

Reason:

- simplest PK/FK semantics;
- easiest development;
- avoids premature partition-key propagation;
- supports RANSYS Lite.

## Enterprise Extension

At 300–1,000 TPS and multi-week hot retention, production benchmark is expected to justify partitioning.

Recommended candidate:

```text
RANGE(received_at/created_at)
daily partitions
```

Before enabling, benchmark:

```text
row size
index size
autovacuum
retention deletion cost
query plans
FK implications
```

Partitioning migration must be designed as an explicit enterprise migration, not hidden in v1 initial DDL.

---

# 39. Retention Jobs

Core hot transaction data:

```text
~3 weeks
```

Purge precondition:

```text
Backoffice copy complete
source version verified
no unresolved operational retention hold
```

Outbox:

```text
published rows short configurable retention
```

Ledger:

```text
separate retention policy
```

Never purge ledger merely because core transaction row passed 3 weeks.

---

# 40. Database Roles

Recommended:

```text
ransys_migrator
ransys_core_rw
ransys_core_ro
ransys_ledger_poster
ransys_outbox_worker
ransys_config_rw
ransys_replication_reader

ransys_bo_rw
ransys_recon_rw
ransys_settlement_rw
ransys_iam_rw
ransys_audit_writer
ransys_audit_reader
```

Principle:

```text
least privilege
```

Provider Adapters receive **no direct wallet/ledger mutation credentials**.

---

# 41. Role Boundary

## `ransys_core_rw`

May:

```text
transactions
attempts
state history
idempotency
```

Cannot perform unrestricted ledger mutation.

## `ransys_ledger_poster`

Used by controlled Core/Ledger module.

May:

```text
lock/update wallet
reservation
insert journal/entries
```

## `ransys_outbox_worker`

May:

```text
claim/update outbox delivery state
```

Cannot mutate wallet.

## Backoffice roles

No direct access to Transaction DB financial tables except read/replication interface explicitly allowed.

---

# 42. Ledger Journal DB Enforcement

Recommended defense:

```text
application posts complete journal in one SQL transaction
+
deferred DB balance trigger
+
immutable posted-row trigger
+
restricted DB roles
```

This gives defense-in-depth without moving business state machine into SQL.

---

# 43. Financial Transaction Atomicity

Reserve DB transaction includes:

```text
transaction
idempotency
wallet lock/update
reservation
ledger journal
state history
outbox
```

Success/failure finalization includes:

```text
transaction lock
wallet lock
reservation lock
posting_key idempotency
journal
wallet projection
state history
outbox
```

If any step fails:

```text
ROLLBACK all
```

---

# 44. Optimistic Concurrency

`core.transactions.row_version` supports concurrent:

```text
callback
status check
reconciliation resolution
recovery worker
```

Pattern:

```sql
UPDATE core.transactions
SET ...,
    row_version = row_version + 1
WHERE ransys_transaction_id = :id
  AND row_version = :expected
  AND processing_status = ANY(:allowed_from_states);
```

Affected row `0`:

```text
reload and evaluate duplicate/conflict
```

---

# 45. Zero-Downtime Migration Rules

Every migration should be classified.

## Expand

Safe examples:

```text
add nullable column
add table
add index CONCURRENTLY
add new compatible check after code supports it
```

## Migrate

```text
backfill in bounded batches
dual-write if necessary
verify counts/invariants
```

## Contract

Only after all running application versions no longer depend on old schema:

```text
drop old column
tighten NOT NULL
remove compatibility view
```

Do not combine destructive contract step with first deployment of new code.

---

# 46. Index Creation

Large production indexes:

```sql
CREATE INDEX CONCURRENTLY ...
```

when supported by the migration context.

Note:

`CREATE INDEX CONCURRENTLY` cannot run inside a normal transaction block.

Migration tooling must support non-transactional migration steps.

---

# 47. Foreign Key Migration

On large populated tables:

```text
ADD CONSTRAINT ... NOT VALID
VALIDATE CONSTRAINT later
```

where appropriate.

This reduces long blocking during expand migration.

---

# 48. Required Database Metrics

```text
transaction commit latency
wallet row lock wait
deadlock count
connection pool saturation
long-running transaction count
outbox pending count
outbox oldest age
autovacuum lag
WAL generation rate
replication lag
table/index size
ledger balance-trigger failures
```

---

# 49. Integrity Jobs

Periodic checker must detect:

```text
available_balance < 0
reserved_balance < 0
duplicate posting_key
unbalanced ledger journal
wallet vs ledger projection mismatch
ACTIVE reservation without valid transaction state
COMMITTED reservation without POST
RELEASED reservation still represented in reserved balance
duplicate active idempotency reference
```

No automatic repair.

Generate critical financial integrity alert.

---

# 50. ERD v1.1 Summary

New/refined entities compared with ERD v1:

```text
core.transaction_fee_components       NEW
core.topup_requests                    NEW

ledger.ledger_transactions.posting_key NEW
ledger.ledger_transactions.approval_request_id NEW

core.transactions separate state dimensions LOCKED
core.transactions captured config/routing/fee policy versions REFINED
transaction_attempt.request_sent emphasized as safety-critical
raw message reference standardized as URI-like opaque reference
```

---

# 51. Physical Design Decisions Closed in v1.1

1. PostgreSQL UUID columns.
2. UUIDv7 recommended from application library.
3. Money = `NUMERIC(30,8)`.
4. Rate = `NUMERIC(30,12)`.
5. `TIMESTAMPTZ` for event timestamps.
6. No float/double.
7. No native PostgreSQL ENUM baseline.
8. Unique ledger `posting_key`.
9. Deferred journal balance constraint.
10. Ledger posted rows immutable.
11. Fee component table added.
12. Dedicated top-up request added.
13. Refund remains a child transaction using `original_transaction_id`.
14. Raw payload DB stores URI reference only.
15. Reference DDL starts unpartitioned.
16. Enterprise range partitioning is benchmark-driven.
17. Core/ledger DB roles separated.
18. Backoffice has no cross-DB FK or financial mutation right.
19. Zero-downtime expand/migrate/contract migration pattern.
20. Integrity checker is mandatory operational control.

---

# 52. Still Open for Later Review

These do not block Canonical Model/API work:

```text
exact enterprise partition threshold
exact ledger retention duration
exact company Chart of Accounts codes
exact secret-record encryption implementation
exact customer identifier tokenization/masking
exact payout accounting integration
```

---

# 53. Next Engineering Step

After ERD v1.1 / Physical Schema:

```text
Canonical Data Model v1
```

Then:

```text
OpenAPI v1
Provider Adapter Contract v1
Response Code Catalog v1
Configuration Schema v1
```

Canonical model should now use the physical/state decisions established here instead of inventing new semantics.

---

# 54. Final Principle

> **Physical schema may evolve, but the financial invariants cannot: one durable business transaction, one controlled reservation, idempotent ledger postings, balanced immutable entries, explicit recovery, and no external financial call before RANSYS has durable internal truth.**
