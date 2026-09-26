# RANSYS Sequence Diagram Pack v1.0

**Document Type:** Detailed Engineering Design — Sequence Diagrams  
**Product:** RANSYS Universal Transaction Switching & Processing Platform  
**Status:** Draft v1 for engineering review  
**Depends On:** RANSYS PRD v1.0, Database ERD v1.0, Ledger Posting Rule Matrix v1.0, Transaction State Transition Matrix v1.0

---

# 1. Purpose

Dokumen ini menunjukkan execution flow end-to-end RANSYS pada skenario utama dan abnormal.

Diagram sengaja menampilkan:

- protocol/API boundary;
- Transaction Core;
- Routing Engine;
- Ledger/Wallet logical module;
- PostgreSQL Transaction DB;
- Provider Adapter;
- Provider;
- Transactional Outbox;
- asynchronous workers;
- Backoffice / Reconciliation;
- DB transaction boundary;
- row locking;
- `TransactionAttempt`;
- state transition;
- ledger posting;
- callback behavior.

Tujuannya adalah memastikan bahwa implementasi service, database transaction, dan financial state konsisten dengan PRD serta ERD.

---

# 2. Logical Participants

Sequence diagrams menggunakan participant berikut.

```text
Merchant / Channel
        |
        v
API / Protocol Gateway
        |
        v
Transaction Core
   |          |
   |          +--> Routing Engine
   |
   +--> Ledger / Wallet Module
   |
   +--> PostgreSQL Transaction DB
        |
        +--> Transactional Outbox
                  |
                  v
              Async Worker
                  |
          +-------+--------+
          |                |
          v                v
      Backoffice       Callback
```

Provider integration:

```text
Transaction Core
      |
      v
Routing Engine
      |
      v
Provider Adapter
      |
      v
Provider
```

**Important:** `Ledger / Wallet Module` adalah logical responsibility. Pada baseline, Transaction Core + Ledger berada dalam satu strong-consistency boundary dan tidak harus menjadi network microservice terpisah.

---

# 3. Database Transaction Notation

Dalam diagram:

```text
BEGIN TX
...
COMMIT
```

berarti satu PostgreSQL transaction.

Provider/network I/O tidak boleh terjadi di dalam DB transaction yang memegang wallet row lock.

Critical financial locking:

```text
Transaction Row
Wallet Row
Reservation Row
```

harus menggunakan consistent lock ordering pada finalization path.

---

# 4. SD-01 — Payment SUCCESS

## Objective

Happy path financial payment:

```text
Request
-> Validate
-> Reserve
-> Route
-> Provider Success
-> Financial Post
-> Response
-> Async Replication
```

## Mermaid

```mermaid
sequenceDiagram
    autonumber
    actor M as Merchant/Channel
    participant G as API/Protocol Gateway
    participant C as Transaction Core
    participant R as Routing Engine
    participant L as Ledger/Wallet Module
    database DB as PostgreSQL Transaction DB
    participant A as Provider Adapter
    participant P as Provider
    participant W as Outbox Worker
    database BO as Backoffice DB

    M->>G: POST /api/v1/payments<br/>clientReference, amount, product
    G->>G: Verify SIGNED_API / mTLS / schema
    G->>C: Canonical PaymentRequest<br/>correlation_id + trace_id

    C->>DB: Check active idempotency record
    alt Existing ref + same fingerprint
        DB-->>C: Existing transaction
        C-->>G: Existing result
        G-->>M: Idempotent response
    else New request
        DB-->>C: No duplicate

        C->>R: Resolve route using active config
        R-->>C: Primary provider + routing_rule_version

        C->>DB: BEGIN TX
        C->>DB: INSERT core.transactions (RECEIVED/VALIDATED)
        C->>DB: INSERT core.idempotency_records
        C->>DB: SELECT ledger.wallets FOR UPDATE
        DB-->>C: Wallet balance
        C->>L: Calculate reserve = amount + guaranteed fee
        L->>DB: INSERT balance_reservations ACTIVE
        L->>DB: UPDATE wallet projection<br/>available -, reserved +
        L->>DB: INSERT ledger_transaction<br/>posting_key=TX:RESERVE
        L->>DB: INSERT balanced ledger_entries
        C->>DB: UPDATE transaction<br/>PROCESSING + financial=RESERVED
        C->>DB: INSERT transaction_state_history
        C->>DB: INSERT outbox event WALLET_RESERVED
        C->>DB: COMMIT
        Note over C,DB: Wallet row lock released here

        C->>DB: INSERT transaction_attempt<br/>attempt=1, request_sent=false
        C->>A: Provider PaymentRequest
        A->>P: Provider-specific request
        A->>DB: Update attempt request_sent=true / sent_at
        P-->>A: SUCCESS + provider reference
        A-->>C: Normalized ProviderResult SUCCESS

        C->>DB: BEGIN TX
        C->>DB: Lock transaction row
        C->>DB: SELECT wallet FOR UPDATE
        C->>DB: Lock reservation row
        C->>DB: Verify posting_key TX:POST not exists
        C->>DB: UPDATE transaction_attempt SUCCESS
        L->>DB: reservation ACTIVE -> COMMITTED
        L->>DB: UPDATE wallet<br/>reserved -, ledger_balance -
        L->>DB: INSERT ledger_transaction POST
        L->>DB: INSERT ledger_entries<br/>DR Merchant Reserved<br/>CR Provider Payable + Revenue/Tax
        C->>DB: UPDATE transaction<br/>SUCCESS + financial=POSTED
        C->>DB: INSERT state_history
        C->>DB: INSERT outbox TRANSACTION_SUCCEEDED
        C->>DB: COMMIT

        C-->>G: responseCode=0000<br/>transactionStatus=SUCCESS
        G-->>M: SUCCESS

        W->>DB: Claim outbox with SKIP LOCKED
        DB-->>W: TRANSACTION_SUCCEEDED
        W->>BO: UPSERT transaction projection
        BO-->>W: Commit
        W->>DB: Mark outbox published
    end
```

## Important Rules

1. Fee/routing/config version used is persisted before provider call.
2. Reserve happens before network call.
3. Wallet lock is released before provider I/O.
4. Provider success is finalized in a second DB transaction.
5. `TX:POST` is idempotent.
6. Backoffice is asynchronous and not required for merchant response.

---

# 5. SD-02 — Provider Timeout -> IN_DOUBT

## Objective

Show why provider read timeout does not release balance and does not become `FAILED`.

```mermaid
sequenceDiagram
    autonumber
    actor M as Merchant
    participant G as Gateway
    participant C as Transaction Core
    participant L as Ledger/Wallet
    database DB as Transaction DB
    participant A as Provider Adapter
    participant P as Provider
    participant W as Outbox Worker

    M->>G: Payment
    G->>C: Canonical request

    C->>DB: BEGIN TX
    C->>DB: Create transaction + idempotency
    C->>DB: SELECT wallet FOR UPDATE
    L->>DB: Create ACTIVE reservation
    L->>DB: Available -> Reserved
    L->>DB: Ledger RESERVE
    C->>DB: PROCESSING / RESERVED
    C->>DB: COMMIT
    Note over C,DB: Lock released

    C->>DB: Create transaction_attempt
    C->>A: Payment request
    A->>P: Send financial request
    A->>DB: request_sent=true
    Note over P,A: Provider may have processed request
    P--xA: Response not received
    A-->>C: READ_TIMEOUT / ambiguous

    C->>DB: BEGIN TX
    C->>DB: Lock transaction
    C->>DB: Update attempt transport_status=TIMEOUT
    C->>DB: PROCESSING -> IN_DOUBT
    C->>DB: reason=PROVIDER_READ_TIMEOUT
    C->>DB: Keep reservation ACTIVE
    Note over C,L: No RELEASE posting
    C->>DB: Insert state history
    C->>DB: Insert outbox TRANSACTION_IN_DOUBT
    C->>DB: COMMIT

    C-->>G: responseCode=1002<br/>transactionStatus=IN_DOUBT
    G-->>M: Timeout result is unknown

    W->>DB: Read TRANSACTION_IN_DOUBT
    W->>W: Schedule recovery/status check if capability allows
```

## Financial Result

```text
available_balance = unchanged after initial reserve
reserved_balance  = remains held
ledger_balance    = unchanged since reserve
```

There is **no new ledger posting** when moving to `IN_DOUBT`.

---

# 6. SD-03 — Status Check Recovery: IN_DOUBT -> SUCCESS

## Objective

Resolve ambiguous payment via provider status check.

```mermaid
sequenceDiagram
    autonumber
    participant W as Recovery Worker
    participant C as Transaction Core
    database DB as Transaction DB
    participant A as Provider Adapter
    participant P as Provider
    participant L as Ledger/Wallet
    participant CB as Merchant Callback Worker

    W->>DB: Find eligible IN_DOUBT transaction
    DB-->>W: TX123 + provider capability/status-check policy
    W->>C: ExecuteStatusCheck(TX123)

    C->>DB: Insert transaction_attempt<br/>type=STATUS_CHECK
    C->>A: StatusCheck request
    A->>P: Provider inquiry/status request
    P-->>A: Original TX = SUCCESS
    A-->>C: Normalized SUCCESS

    C->>DB: BEGIN TX
    C->>DB: Lock transaction
    C->>DB: SELECT wallet FOR UPDATE
    C->>DB: Lock ACTIVE reservation
    C->>DB: Check TX123:POST
    alt POST does not exist
        L->>DB: reservation -> COMMITTED
        L->>DB: reserved -, ledger_balance -
        L->>DB: Insert POST ledger transaction
        L->>DB: Insert balanced ledger entries
        C->>DB: IN_DOUBT -> SUCCESS
        C->>DB: financial RESERVED -> POSTED
        C->>DB: reason=STATUS_CHECK_SUCCESS
        C->>DB: Insert state history
        C->>DB: Insert outbox TRANSACTION_RESOLVED_SUCCESS
    else POST already exists
        Note over C,DB: Idempotent no-op financial result
    end
    C->>DB: COMMIT

    CB->>DB: Consume resolved-success outbox
    CB-->>CB: Build merchant callback
    CB-->>C: Callback delivery tracked asynchronously
```

## Duplicate Safety

If reconciliation or callback already posted the success before status check completes:

```text
posting_key TX123:POST already exists
```

then the status check result must not create a second financial posting.

---

# 7. SD-04 — Failover Before Request Is Sent

## Objective

Differentiate safe provider failover from ambiguous timeout.

```mermaid
sequenceDiagram
    autonumber
    actor M as Merchant
    participant C as Transaction Core
    participant R as Routing Engine
    database DB as Transaction DB
    participant A1 as Provider A Adapter
    participant A2 as Provider B Adapter
    participant P2 as Provider B

    M->>C: Payment
    C->>DB: Reserve balance and COMMIT
    C->>R: Route
    R-->>C: Provider A primary, Provider B secondary

    C->>DB: Create Attempt 1 / Provider A
    C->>A1: Send Payment
    A1-->>C: CONNECTION_ERROR<br/>request_sent=false
    C->>DB: Update Attempt 1<br/>NOT_SENT / CONNECTION_ERROR

    Note over C: Safe failover because Provider A did not receive request

    C->>DB: Create Attempt 2 / Provider B
    C->>A2: Send Payment
    A2->>P2: Provider B request
    P2-->>A2: SUCCESS
    A2-->>C: SUCCESS
    C->>DB: Finalize POST
    C-->>M: SUCCESS
```

## Rule

```text
request_sent=false
=> failover may be safe

request_sent=true + no final response
=> IN_DOUBT, do NOT failover purchase
```

---

# 8. SD-05 — Duplicate Request / Idempotency

## Scenario A — Same Reference + Same Fingerprint

```mermaid
sequenceDiagram
    autonumber
    actor M as Merchant
    participant G as Gateway
    participant C as Transaction Core
    database DB as Transaction DB

    M->>G: Payment clientReference=INV001
    G->>C: Canonical request + fingerprint F1
    C->>DB: Lookup active idempotency<br/>(channel, INV001)
    DB-->>C: TX123, fingerprint F1

    C->>DB: Load current TX123 status
    DB-->>C: SUCCESS / responseCode=0000
    C-->>G: Existing transaction result
    G-->>M: Same ransysTransactionId TX123
```

No new:

```text
transaction
reservation
ledger posting
provider attempt
```

## Scenario B — Same Reference + Different Fingerprint

```mermaid
sequenceDiagram
    autonumber
    actor M as Merchant
    participant C as Transaction Core
    database DB as Transaction DB

    M->>C: INV001 amount=200000<br/>fingerprint=F2
    C->>DB: Lookup active idempotency INV001
    DB-->>C: Existing TX123 fingerprint=F1
    C->>C: F1 != F2
    C-->>M: 2003 DUPLICATE_REFERENCE_CONFLICT
```

---

# 9. SD-06 — Reversal After IN_DOUBT, Reservation Still Active

## Objective

Original provider result is ambiguous and financial reserve has not been committed.

```mermaid
sequenceDiagram
    autonumber
    actor O as Ops / Recovery Worker
    participant C as Transaction Core
    database DB as Transaction DB
    participant A as Provider Adapter
    participant P as Provider
    participant L as Ledger/Wallet

    O->>C: Request reversal TX123
    C->>DB: Validate TX123 IN_DOUBT / financial=RESERVED
    C->>DB: Update processing=REVERSAL_PENDING
    C->>DB: Create reversal transaction_attempt
    C->>A: Reversal request
    A->>P: Provider reversal
    P-->>A: REVERSAL SUCCESS
    A-->>C: Normalized reversal success

    C->>DB: BEGIN TX
    C->>DB: Lock transaction
    C->>DB: SELECT wallet FOR UPDATE
    C->>DB: Lock reservation
    L->>DB: DR Merchant Reserved
    L->>DB: CR Merchant Available
    L->>DB: Wallet reserved -, available +
    L->>DB: Reservation ACTIVE -> RELEASED
    C->>DB: REVERSAL_PENDING -> REVERSED
    C->>DB: financial=RELEASED
    C->>DB: Insert outbox TRANSACTION_REVERSED
    C->>DB: COMMIT
```

This is financially a **release of reservation**, because original payment was not yet posted in RANSYS ledger.

---

# 10. SD-07 — Reversal After Payment Was Already Posted

## Objective

Reverse a completed financial posting without editing historical ledger rows.

```mermaid
sequenceDiagram
    autonumber
    actor O as Ops / Recovery
    participant C as Transaction Core
    database DB as Transaction DB
    participant A as Provider Adapter
    participant P as Provider
    participant L as Ledger/Wallet

    O->>C: Reversal TX123
    C->>DB: Verify original SUCCESS / POSTED
    C->>DB: Set REVERSAL_PENDING
    C->>A: Provider reversal
    A->>P: Reversal
    P-->>A: SUCCESS
    A-->>C: SUCCESS

    C->>DB: BEGIN TX
    C->>DB: Lock transaction + wallet
    C->>DB: Check unique reversal posting_key
    L->>DB: INSERT compensating ledger transaction
    L->>DB: DR Provider Payable
    L->>DB: DR Revenue reversal where applicable
    L->>DB: CR Merchant Available
    L->>DB: Increase wallet available/ledger balance
    C->>DB: processing=REVERSED
    C->>DB: financial=REVERSED
    C->>DB: Link compensates original POST
    C->>DB: State history + outbox
    C->>DB: COMMIT
```

Original `POST` ledger transaction stays immutable.

---

# 11. SD-08 — Refund

## Objective

Refund is a separate transaction referencing original transaction.

```mermaid
sequenceDiagram
    autonumber
    actor U as Backoffice User / Client
    participant I as IAM / Approval
    participant C as Transaction Core
    database DB as Transaction DB
    participant A as Provider Adapter
    participant P as Provider
    participant L as Ledger/Wallet

    U->>I: Create refund request
    I->>DB: Create approval_request (maker)
    Note over I: Manual refund requires checker approval
    I-->>U: Pending approval
    U->>I: Checker approves
    I->>DB: approval=APPROVED

    I->>C: Execute Refund
    C->>DB: Load original SUCCESS transaction
    C->>DB: Validate remaining refundable amount
    C->>DB: Create child transaction<br/>type=REFUND<br/>original_transaction_id=TX123
    C->>DB: REFUND_PENDING

    C->>A: Refund request
    A->>P: Provider refund
    P-->>A: REFUND SUCCESS
    A-->>C: SUCCESS

    C->>DB: BEGIN TX
    C->>DB: Lock refund/original financial context
    C->>DB: Check refund posting_key
    L->>DB: DR Provider Receivable / Refund Clearing
    L->>DB: Optional DR Revenue if fee refundable
    L->>DB: CR Merchant Available
    L->>DB: Increase merchant wallet
    C->>DB: Refund child -> SUCCESS
    C->>DB: Original aggregate -> PARTIALLY_REFUNDED or REFUNDED
    C->>DB: Insert state history + outbox
    C->>DB: COMMIT
```

## Safety

```text
SUM(successful refunds)
<= refundable original amount
```

unless an explicitly approved adjustment exists.

---

# 12. SD-09 — Deterministic Reconciliation Resolution

## Objective

Reconciliation resolves `IN_DOUBT` only when matching is deterministic.

```mermaid
sequenceDiagram
    autonumber
    participant RA as Recon Adapter
    participant RE as Recon Engine
    database BO as Backoffice DB
    participant C as Transaction Core
    database DB as Transaction DB
    participant L as Ledger/Wallet

    RA->>RE: Provider recon record<br/>providerRef, amount, status=SUCCESS
    RE->>BO: Match provider record against RANSYS projection
    BO-->>RE: TX123 = IN_DOUBT<br/>amount/reference match

    RE->>RE: Apply provider-specific deterministic rule
    alt Deterministic SUCCESS
        RE->>BO: recon_item MATCHED / resolution pending
        RE->>C: Resolve TX123 as SUCCESS<br/>source=RECONCILIATION

        C->>DB: BEGIN TX
        C->>DB: Lock transaction + wallet + reservation
        C->>DB: Check posting_key TX123:POST
        L->>DB: Commit ACTIVE reservation
        L->>DB: POST balanced ledger entries
        C->>DB: IN_DOUBT -> SUCCESS
        C->>DB: financial RESERVED -> POSTED
        C->>DB: outbox RESOLVED_SUCCESS
        C->>DB: COMMIT

        C-->>RE: Resolution committed
        RE->>BO: recon resolution_source=RECONCILIATION
    else Mismatch / non-deterministic
        RE->>BO: recon_status=EXCEPTION
        Note over RE,L: No ledger adjustment
    end
```

Recon Engine never directly writes Core ledger entries.

---

# 13. SD-10 — Merchant Top-up with Maker-Checker

## Objective

Manual top-up is controlled, audited, and posted only after approval/verification.

```mermaid
sequenceDiagram
    autonumber
    actor F as Finance Maker
    actor S as Finance Checker
    participant BO as Backoffice
    database BODB as Backoffice DB
    participant C as Financial Command/Core
    database DB as Transaction DB
    participant L as Ledger/Wallet

    F->>BO: Create Top-up request<br/>merchant, amount, bank reference
    BO->>BODB: Insert approval_request<br/>maker=F, PENDING
    BO-->>F: Awaiting approval

    S->>BO: Review top-up
    BO->>BODB: Verify checker != maker
    S->>BO: Approve
    BO->>BODB: APPROVED

    BO->>C: Execute approved top-up
    C->>C: Verify external fund evidence / business verification

    C->>DB: BEGIN TX
    C->>DB: SELECT wallet FOR UPDATE
    C->>DB: Verify topup posting_key unique
    L->>DB: DR CASH_CLEARING
    L->>DB: CR MERCHANT_AVAILABLE
    L->>DB: available_balance +
    L->>DB: ledger_balance +
    C->>DB: Insert outbox TOPUP_POSTED
    C->>DB: COMMIT

    C-->>BO: Top-up posted
    BO->>BODB: Update approval/action projection + audit
```

No direct `UPDATE wallet SET balance = ...` is allowed from Backoffice.

---

# 14. SD-11 — Manual Financial Adjustment

```mermaid
sequenceDiagram
    autonumber
    actor M as Maker
    actor K as Checker
    participant BO as Backoffice
    database BODB as Backoffice DB
    participant C as Core Financial Command
    database DB as Transaction DB
    participant L as Ledger/Wallet

    M->>BO: Create adjustment request<br/>reason + evidence
    BO->>BODB: approval_request PENDING
    K->>BO: Approve adjustment
    BO->>BODB: Verify maker != checker
    BO->>BODB: APPROVED

    BO->>C: Execute Adjustment
    C->>DB: BEGIN TX
    C->>DB: SELECT wallet FOR UPDATE
    alt Credit Adjustment
        L->>DB: DR ADJUSTMENT_CLEARING
        L->>DB: CR MERCHANT_AVAILABLE
        L->>DB: available + / ledger +
    else Debit Adjustment
        C->>DB: Validate available >= adjustment amount
        L->>DB: DR MERCHANT_AVAILABLE
        L->>DB: CR ADJUSTMENT_CLEARING
        L->>DB: available - / ledger -
    end
    C->>DB: Insert immutable ledger transaction
    C->>DB: Insert outbox ADJUSTMENT_POSTED
    C->>DB: COMMIT

    C-->>BO: Executed
    BO->>BODB: Audit final result
```

---

# 15. SD-12 — Outbox Replication to Backoffice

## Objective

Guarantee Backoffice eventually receives status updates without becoming synchronous dependency.

```mermaid
sequenceDiagram
    autonumber
    database DB as Transaction DB
    participant W as Replication Worker
    database BO as Backoffice DB

    Note over DB: Financial TX already committed<br/>with async.outbox_events

    W->>DB: BEGIN
    W->>DB: SELECT PENDING events<br/>FOR UPDATE SKIP LOCKED
    DB-->>W: Event batch
    W->>DB: Mark PROCESSING / claim
    W->>DB: COMMIT

    loop Each event
        W->>BO: BEGIN
        W->>BO: Check inbox event_id
        alt Event already processed
            BO-->>W: Duplicate / no-op
        else New event
            W->>BO: Compare source_version
            alt Newer version
                W->>BO: UPSERT bo.transactions / projections
            else Older out-of-order event
                Note over W,BO: Do not overwrite newer projection
            end
            W->>BO: Insert/mark inbox processed
        end
        W->>BO: COMMIT
    end

    W->>DB: Mark outbox PUBLISHED
```

At-least-once delivery is expected; idempotent consumer behavior is mandatory.

---

# 16. SD-13 — Duplicate / Late Provider Callback After Recon Already Posted

## Objective

Protect against double financial effect from out-of-order asynchronous result.

```mermaid
sequenceDiagram
    autonumber
    participant P as Provider
    participant A as Provider Adapter
    participant C as Transaction Core
    database DB as Transaction DB

    Note over C,DB: Earlier reconciliation already resolved TX123 SUCCESS<br/>TX123:POST exists

    P->>A: Delayed SUCCESS callback
    A->>C: Normalized SUCCESS TX123
    C->>DB: Load transaction + posting_key
    DB-->>C: SUCCESS / POSTED<br/>TX123:POST already exists

    C->>DB: Record callback/attempt history
    Note over C,DB: No new state transition<br/>No new ledger posting
    C-->>A: Idempotent acknowledgement
    A-->>P: ACK
```

---

# 17. SD-14 — Provider Returns Conflicting Result After SUCCESS

## Objective

Prevent destructive state rollback.

```mermaid
sequenceDiagram
    autonumber
    participant P as Provider
    participant A as Adapter
    participant C as Transaction Core
    database DB as Transaction DB
    participant RE as Recon/Exception Workflow

    Note over C,DB: TX123 = SUCCESS / POSTED
    P->>A: Late FAILED result
    A->>C: FAILED result
    C->>DB: Load current state
    DB-->>C: SUCCESS / POSTED

    C->>C: Detect contradictory result
    C->>DB: Record provider attempt/event
    C->>DB: reconciliation_status=EXCEPTION
    C->>DB: reason=CONFLICTING_PROVIDER_RESULT
    C->>DB: Insert outbox RECON_EXCEPTION_CREATED
    C-->>A: ACK result received
    C->>RE: Investigation event
```

Not allowed:

```text
SUCCESS -> FAILED
release wallet
delete original ledger
```

---

# 18. SD-15 — Financial Dependency Failure / Fail Closed

## Objective

Show fail-closed behavior when durable financial state cannot be created.

```mermaid
sequenceDiagram
    autonumber
    actor M as Merchant
    participant G as Gateway
    participant C as Transaction Core
    database DB as PostgreSQL
    participant A as Provider Adapter

    M->>G: Payment
    G->>C: Canonical request
    C->>DB: BEGIN / create transaction / lock wallet
    DB--xC: DB unavailable / transaction cannot commit

    C->>C: Financial state is NOT durable
    Note over C,A: Provider must NOT be called
    C-->>G: Controlled internal/infrastructure failure
    G-->>M: FAILED / unavailable response
```

Fundamental invariant:

```text
No durable transaction + reserve
=> no financial provider call
```

---

# 19. Sequence-to-Table Mapping

| Flow | Main Tables |
|---|---|
| Payment reserve | `core.transactions`, `core.idempotency_records`, `ledger.wallets`, `ledger.balance_reservations`, `ledger.ledger_transactions`, `ledger.ledger_entries`, `async.outbox_events` |
| Provider call | `core.transaction_attempts` |
| State transition | `core.transactions`, `core.transaction_state_history` |
| Provider success | reservation + wallet + ledger + outbox |
| IN_DOUBT | transaction + attempt + history + outbox; no new ledger posting |
| Reversal | child/attempt history + ledger compensation/release |
| Refund | child transaction + attempts + refund ledger posting |
| Reconciliation | Backoffice `recon.*` + controlled Core resolution |
| Top-up | IAM approval + Core ledger posting |
| Manual adjustment | IAM approval + immutable adjustment posting |
| Replication | `async.outbox_events` -> `bo.inbox_events` / projections |

---

# 20. Sequence-to-Posting-Key Mapping

Recommended:

```text
Payment Reserve
TX:<ransys_transaction_id>:RESERVE

Payment Final Post
TX:<ransys_transaction_id>:POST

Payment Release
TX:<ransys_transaction_id>:RELEASE

Reversal
TX:<ransys_transaction_id>:REVERSAL:<reversal_reference>

Refund
TX:<original_transaction_id>:REFUND:<refund_reference>

Top-up
TOPUP:<topup_reference>

Adjustment
ADJUSTMENT:<adjustment_reference>
```

Every posting key must be unique.

---

# 21. TransactionAttempt Rules

A `TransactionAttempt` is created for each external/provider action:

```text
PAYMENT
STATUS_CHECK
REVERSAL
REFUND
ADVICE
```

Required fields include:

```text
attempt_no
attempt_type
provider_id
request_sent
transport_status
provider_response_code
provider_reference
provider_sent_at
provider_response_at
raw_request_reference
raw_response_reference
correlation_id
trace_id
```

Important semantic:

```text
request_sent=false
```

means failover may be safe.

```text
request_sent=true + unknown result
```

means potential financial ambiguity.

---

# 22. Lock Order

Final exact lock order remains implementation decision, but all financial finalization paths must use a consistent order.

Recommended baseline:

```text
1. transaction row
2. wallet row
3. reservation row
4. unique ledger posting check
```

Provider/network I/O is always outside that DB transaction.

---

# 23. Outbox Events Used by Sequence Pack

Suggested catalog:

```text
TRANSACTION_RECEIVED
TRANSACTION_VALIDATED
WALLET_RESERVED
TRANSACTION_PROCESSING
TRANSACTION_SUCCEEDED
TRANSACTION_FAILED
TRANSACTION_PENDING
TRANSACTION_IN_DOUBT
TRANSACTION_RESOLVED_SUCCESS
TRANSACTION_RESOLVED_FAILED
REVERSAL_REQUESTED
TRANSACTION_REVERSED
REFUND_REQUESTED
TRANSACTION_REFUNDED
TOPUP_POSTED
ADJUSTMENT_POSTED
RECON_EXCEPTION_CREATED
```

Event names remain subject to Event Contract design, but semantics should remain consistent.

---

# 24. Response Timing

Merchant synchronous response should not wait for:

```text
Backoffice replication
Reporting
Reconciliation
Settlement
Notification delivery
RabbitMQ
```

The response waits only for the synchronous transaction result required by the transaction flow and durable financial commit.

---

# 25. Observability in Every Sequence

Every service hop must propagate:

```text
ransys_transaction_id
correlation_id
trace_id
```

For provider attempts, additionally capture:

```text
transaction_attempt_id
provider_id
attempt_type
latency_ms
```

Logs must not be the financial source of truth.

---

# 26. Raw Message Behavior

Provider Adapter may write raw request/response to configured raw-message storage.

DB only stores:

```text
raw_request_reference
raw_response_reference
```

Raw file writing must not allow secret/PIN/CVV leakage and must not make Core DB wait indefinitely on a slow log sink.

---

# 27. Critical Failure Cases Covered

This Sequence Diagram Pack explicitly protects against:

1. duplicate client retry;
2. same reference with changed payload;
3. concurrent wallet spend;
4. provider timeout;
5. safe pre-send failover;
6. unsafe post-send failover;
7. duplicate provider callback;
8. out-of-order callback;
9. reversal before final financial post;
10. reversal after successful post;
11. partial/full refund;
12. reconciliation auto-resolution;
13. reconciliation mismatch;
14. manual financial action without approval;
15. DB outage before financial durability;
16. Backoffice outage;
17. outbox duplicate delivery.

---

# 28. Design Refinements Exposed by Sequence Pack

The diagrams reinforce the following refinements for ERD/API design:

1. `request_sent` in `transaction_attempts` is safety-critical.
2. Ledger `posting_key` must be unique.
3. Refund should be a child transaction using `original_transaction_id`.
4. State dimensions should remain separated.
5. Fee/config/routing versions must be captured on transaction.
6. Backoffice consumer needs inbox/dedup + `source_version`.
7. Finalization paths need optimistic row version plus row locks for financial state.
8. Manual financial commands need approval reference.
9. Recovery Worker should use provider capability and timeout policy.
10. Recon resolution must call controlled Transaction Core/Ledger operation rather than mutate DB directly.

---

# 29. Next Recommended Engineering Phase

After this Sequence Diagram Pack, move to:

```text
ERD v1.1 + Physical PostgreSQL Schema Design
```

Specifically:

```text
exact tables
exact columns
data types
NUMERIC precision
PK/FK
partial unique indexes
posting_key
fee components
top-up aggregate
partitioning
DDL migration strategy
database roles
locking conventions
```

Then:

```text
Canonical Data Model v1
OpenAPI v1
Provider Adapter Contract v1
Response Code Catalog v1
Configuration Schema v1
```

---

# 30. Final Sequence Principle

> **Every financial sequence must first establish durable RANSYS truth, then communicate externally, then finalize using an idempotent second financial transaction. Ambiguity is preserved as IN_DOUBT until evidence resolves it; it is never hidden by an unsafe retry, status overwrite, or silent balance edit.**
