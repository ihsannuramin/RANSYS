# RANSYS — Implementation Handoff: OpenAPI v1 + Provider Adapter Contract v1

You are continuing implementation of **RANSYS**, a universal transaction switching and payment-processing platform.

Act as a **Senior .NET Backend Engineer**.

Your responsibility is to implement the architecture and contracts already approved.

## IMPORTANT RULE

Do **not redesign RANSYS**.

The documents under `/docs/ransys` are the architecture source of truth.

If implementation conflicts with a document:

1. Do not silently change the architecture.
2. Stop only the affected implementation area.
3. Create an ADR under `/docs/decisions`.
4. Explain the conflict and propose options.
5. Continue unrelated work where safe.

Financial safety rules always take priority.

---

# 1. Read These Documents First

Read all existing RANSYS documentation before making changes.

At minimum:

```text
/docs/ransys/RANSYS_PRD_v1.0.md
/docs/ransys/RANSYS_Database_ERD_v1.0.md
/docs/ransys/RANSYS_Ledger_Posting_Rule_Matrix_v1.0.md
/docs/ransys/RANSYS_Transaction_State_Transition_Matrix_v1.0.md
/docs/ransys/RANSYS_Sequence_Diagram_Pack_v1.0.md
/docs/ransys/RANSYS_ERD_Physical_PostgreSQL_v1.1.md
/docs/ransys/RANSYS_PostgreSQL_Reference_DDL_v1.1.sql
/docs/ransys/RANSYS_Canonical_Data_Model_v1.0.md
/docs/ransys/RANSYS_Canonical_Contracts_v1.0.cs

/docs/ransys/RANSYS_OpenAPI_v1.md
/docs/ransys/RANSYS_OpenAPI_v1.yaml

/docs/ransys/RANSYS_Provider_Adapter_Contract_v1.md
/docs/ransys/RANSYS_Provider_Adapter_v1.proto
/docs/ransys/RANSYS_Provider_Adapter_Contracts_v1.cs
```

Also read all approved ADRs currently present in:

```text
/docs/decisions/
```

---

# 2. Before Coding

First inspect the repository and report:

```text
1. Current implemented milestones
2. Current project structure
3. Existing API implementation
4. Existing Provider Adapter implementation
5. Conflicts between code and current RANSYS contracts
6. Exact files you plan to modify
7. Test plan
```

Do not generate a new architecture if an existing implementation already exists.

Prefer incremental modification.

---

# 3. Current Architecture Decisions

The following decisions are now approved.

## Reversal

Reversal is a **child transaction**.

Example:

```text
Original Payment:
TX-PAY-001

Reversal:
TX-REV-001
originalTransactionId = TX-PAY-001
```

Starting a reversal must **not overwrite the processing status of the original transaction**.

This must allow:

```text
Original Payment = IN_DOUBT
Reversal Child   = PROCESSING
```

to exist simultaneously.

If the original SUCCESS becomes definitive while reversal is still processing:

```text
Original payment may be financially POSTED.
```

If reversal later succeeds:

```text
create compensating REVERSAL ledger posting.
```

Never edit historical ledger entries.

---

# 4. Refund Fee Policy

Refundability is defined per original transaction fee component.

Supported policies:

```text
NONE
PRO_RATA
FULL
```

Default:

```text
NONE
```

Refund calculation must use the **fee-component snapshot captured by the original transaction**.

Never recalculate historical refund fee using the current fee configuration.

---

# 5. Chart of Accounts

RANSYS business logic uses **semantic ledger accounts**.

Examples:

```text
MERCHANT_AVAILABLE
MERCHANT_RESERVED
PROVIDER_PAYABLE
PROVIDER_RECEIVABLE
RANSYS_FEE_REVENUE
TAX_PAYABLE
CASH_CLEARING
REFUND_CLEARING
ADJUSTMENT_CLEARING
```

External Finance GL codes are configurable mappings.

Do not hardcode customer/company-specific Finance GL account numbers into Domain or Ledger logic.

---

# 6. Wallet Status

Wallet states:

```text
ACTIVE
FROZEN
CLOSED
```

## ACTIVE

Normal financial processing allowed.

## FROZEN

Block new financial consumption:

```text
new PAYMENT reserve
new PURCHASE reserve
new TRANSFER reserve
other new merchant-initiated debit
```

But allow recovery/finalization of existing activity:

```text
existing reserved transaction SUCCESS finalization
reservation RELEASE
REVERSAL
REFUND
reconciliation resolution
authorized financial correction
read/query
```

Do not reject a definitive provider SUCCESS merely because the wallet became FROZEN after reservation.

## CLOSED

Terminal wallet.

Before closing require:

```text
available_balance = 0
reserved_balance = 0
ledger_balance = 0

no ACTIVE reservation
no unresolved financial IN_DOUBT transaction
```

Closed wallets cannot accept new transactions/topups/adjustments.

Historical read/audit remains allowed.

---

# 7. Provider Capabilities

Baseline capability codes now include:

```text
INQUIRY
PAYMENT
PURCHASE
TRANSFER
VOID
STATUS_CHECK
REVERSAL
REFUND
ADVICE
CALLBACK
BALANCE_CHECK
RECONCILIATION
SETTLEMENT_FILE
```

Important:

```text
VOID != REVERSAL
VOID != REFUND
```

Do not silently translate unsupported VOID into another operation.

---

# 8. Implement OpenAPI v1

The machine-readable contract is:

```text
RANSYS_OpenAPI_v1.yaml
```

Implement endpoints matching it exactly.

Required endpoints:

```text
POST /api/v1/inquiries
POST /api/v1/payments
POST /api/v1/transfers
POST /api/v1/refunds
POST /api/v1/reversals
POST /api/v1/voids

GET /api/v1/transactions/{ransysTransactionId}
```

Do not merge all transactions into one generic:

```text
POST /transactions
```

---

# 9. API DTO Boundary

Create dedicated API DTOs.

Do NOT expose:

```text
EF entities
CanonicalTransaction directly
Ledger entities
ProviderResult directly
```

Expected flow:

```text
API Request DTO
    ↓
Application Command
    ↓
Canonical/Domain Model
```

and:

```text
Application Result
    ↓
API Response DTO
```

---

# 10. Money Serialization

OpenAPI v1 defines money as:

```json
{
    "value": "100000.00",
    "currency": "IDR"
}
```

Amount is an exact decimal string in the public API.

Internally convert using:

```text
decimal
```

Never use:

```text
float
double
```

Reject malformed decimal values.

Currency-definition version is resolved internally and must not be trusted from public input.

---

# 11. API Security Headers

Prepare API middleware/contracts for:

```text
X-Ransys-Client-Id
X-Ransys-Timestamp
X-Ransys-Nonce
Content-Digest
Signature-Input
Signature
```

plus mTLS client identity.

If complete cryptographic verification is not implemented yet, create a clean abstraction:

```text
IRequestAuthenticationService
IReplayProtectionService
ISignatureVerifier
```

Do not fake successful authentication.

Unimplemented security must fail closed or be explicitly restricted to Development/Test mode.

---

# 12. Nonce vs Idempotency

Do not mix:

```text
X-Ransys-Nonce
Idempotency-Key
clientReference
transaction fingerprint
```

They have different purposes.

Nonce:

```text
security anti-replay
```

Idempotency Key:

```text
optional retry identity
```

Client Reference:

```text
business transaction identity
```

Fingerprint:

```text
RANSYS duplicate-payload detector
```

---

# 13. API HTTP Semantics

Follow the OpenAPI decision:

```text
HTTP 200
```

means the request entered normal transaction processing.

Business result can still be:

```text
SUCCESS
FAILED
PENDING
IN_DOUBT
```

Examples:

```text
provider decline
insufficient merchant balance
provider timeout resulting in IN_DOUBT
```

Use:

```text
400 malformed/schema validation
401 auth/signature/certificate/nonce failure
403 permission failure
409 duplicate reference conflict
429 rate limiting before provider financial send
503 mandatory financial dependency unavailable
```

If PostgreSQL/Ledger durability cannot be established:

```text
return failure
DO NOT call provider
```

---

# 14. Transaction Response

Return the approved contract:

```json
{
    "ransysTransactionId": "...",
    "clientReference": "...",
    "responseCode": "0000",
    "responseMessage": "Success",
    "transactionStatus": "SUCCESS",
    "references": {},
    "data": {},
    "timestamp": "..."
}
```

Do not equate:

```text
responseCode
```

with:

```text
transactionStatus
```

Example:

```text
responseCode = 1002
transactionStatus = IN_DOUBT
```

is valid.

---

# 15. Status Endpoint

`GET /api/v1/transactions/{id}` may expose:

```text
processingStatus
financialStatus
reconciliationStatus
settlementStatus
```

Do not expose public/internal-sensitive fields such as:

```text
actual provider topology
routing rule version
wallet reservation ID
ledger account IDs
posting key
raw-message URI
row version
secret references
```

Authorization must ensure merchant/channel cannot retrieve another client's transaction.

---

# 16. Refund API Implementation

Refund creates a new business transaction:

```text
transactionType = REFUND
originalTransactionId = original payment
```

Do not mutate the original transaction into a refund transaction.

Validate:

```text
original transaction exists
original transaction is refundable
refund amount <= remaining refundable principal
fee refund according to original fee-component policy
```

Manual Backoffice maker-checker behavior remains separate from merchant API authorization.

---

# 17. Reversal API Implementation

Reversal creates:

```text
new ransysTransactionId
transactionType = REVERSAL
originalTransactionId = original transaction
```

Do not perform:

```text
original.ProcessingStatus = REVERSAL_PENDING
```

merely because child reversal started.

Update any old implementation that still uses the superseded model.

---

# 18. Implement Provider Adapter Contract v1

Use:

```text
RANSYS_Provider_Adapter_Contract_v1.md
RANSYS_Provider_Adapter_v1.proto
RANSYS_Provider_Adapter_Contracts_v1.cs
```

as authoritative inputs.

---

# 19. Provider Contract Semantic Interface

Core-facing operations:

```text
GetCapabilitiesAsync
HealthCheckAsync

InquiryAsync
PaymentAsync
PurchaseAsync
TransferAsync
VoidAsync
StatusCheckAsync
ReversalAsync
RefundAsync
AdviceAsync
BalanceInquiryAsync

GetProviderBalanceAsync
```

Do not require each provider to support every operation.

---

# 20. Provider Result

Provider Adapter returns normalized:

```text
ProviderResult

Outcome
Finality
Transport
RansysResponseCode
ProviderResponseCode
ProviderResponseMessage
References
Data
RetryHint
ReceivedAt
RawMessageReferences
```

Outcome:

```text
SUCCESS
FAILED
PENDING
IN_DOUBT
NOT_SENT
```

Finality:

```text
DEFINITIVE
NON_FINAL
AMBIGUOUS
NOT_APPLICABLE
```

---

# 21. requestSent Is Financially Critical

Never remove or hide:

```text
ProviderTransportResult.RequestSent
```

Rules:

```text
RequestSent=false
=> Core may consider failover

RequestSent=true
+ ambiguous result
=> Core must not blindly fail over
```

Adapter should be conservative.

If it cannot prove the request was not transmitted:

```text
do not report RequestSent=false
```

---

# 22. Adapter Retry Rule

Adapter must not perform invisible financial retries.

No hidden:

```text
Payment retry
Purchase retry
Transfer retry
```

after ambiguous send.

Adapter may return:

```text
SafeToRetryTransport
SuggestedDelay
```

but Core makes the final retry/failover decision.

---

# 23. Provider Adapter Boundary

Adapters may:

```text
serialize provider request
perform HTTP/SOAP/ISO/TCP communication
authenticate to provider
parse provider response
map provider response codes
store restricted raw message
return ProviderResult
```

Adapters may NOT:

```text
update wallet
insert ledger entries
release reservation
set original transaction financial status
choose another provider
perform reconciliation resolution
```

---

# 24. gRPC / Protobuf

Use:

```text
RANSYS_Provider_Adapter_v1.proto
```

for remote adapter deployment.

Do not require network gRPC for every local/Lite execution.

Use the same semantic interface for an in-process adapter implementation.

Avoid duplicating business semantics between in-process and gRPC variants.

---

# 25. Callback Ingress

Implement/prepare:

```text
IProviderCallbackSink
```

Provider-specific inbound callbacks must be normalized by Adapter before Core processing.

Callbacks are:

```text
at least once
```

Core must be idempotent.

Duplicate SUCCESS callback must not create second financial posting.

Conflicting callback after final SUCCESS creates an exception/investigation path, not a silent rollback.

---

# 26. Provider Health

Health result:

```text
HEALTHY
DEGRADED
UNHEALTHY
```

Health is informational input.

Routing Engine owns routing decisions.

Manual-disabled provider remains disabled even if health is HEALTHY.

---

# 27. Provider Balance

If provider supports actual balance endpoint:

```text
source = ACTUAL_PROVIDER
```

Do not fake internal estimated balance as actual provider balance.

---

# 28. Tests Required for This Milestone

## OpenAPI / API tests

Test:

```text
valid payment
IN_DOUBT response
malformed amount
missing clientReference
invalid UUID
duplicate reference same fingerprint
duplicate reference conflicting fingerprint
wallet insufficient balance
DB unavailable fail-closed
transaction status ownership/authorization
reversal child transaction
refund child transaction
void endpoint
```

## Adapter contract tests

Test:

```text
SUCCESS + DEFINITIVE
FAILED + DEFINITIVE
PENDING + NON_FINAL
IN_DOUBT + AMBIGUOUS
NOT_SENT + requestSent=false

timeout after send
connect error before send
unsupported capability
TRANSFER
VOID
duplicate callback
conflicting callback
```

---

# 29. Contract Tests

Add automated validation that:

```text
OpenAPI file remains valid
protobuf compiles
C# contracts compile
API output matches OpenAPI DTO
```

If possible, add CI breaking-change detection later.

---

# 30. Response Code Catalog

The complete response-code catalog is not yet finalized.

Use the codes already explicitly approved in RANSYS docs only.

Do not invent dozens of new permanent codes.

If implementation needs an undefined canonical code:

```text
create TODO / ADR candidate
```

The next architecture artifact will finalize Response Code Catalog v1.

---

# 31. Definition of Done

This milestone is complete when:

- OpenAPI v1 endpoints are implemented or cleanly scaffolded.
- Public DTOs match `RANSYS_OpenAPI_v1.yaml`.
- API DTOs do not leak persistence/domain internals.
- Decimal string amounts safely map to decimal.
- Reversal is a child transaction.
- Refund is a child transaction.
- VOID exists independently.
- Provider Adapter Contract v1 compiles.
- Protobuf contract compiles.
- `requestSent` safety semantics are preserved.
- Adapter cannot mutate financial state.
- Callback ingress is idempotency-ready.
- Tests pass.
- `dotnet build` passes.
- `dotnet test` passes.
- architecture conflicts are documented as ADRs instead of silently resolved.

---

# 32. Work Style

Do this incrementally.

Suggested order:

```text
1. repository/code audit
2. reconcile existing contracts
3. API DTOs
4. API mapping
5. transaction endpoints
6. child reversal/refund correction
7. adapter contracts
8. protobuf integration
9. callback contract
10. tests
11. documentation
```

Keep repository buildable after each logical milestone.

At the end, report:

```text
Implemented
Changed
Tests added
Architecture conflicts found
ADRs created
Remaining TODOs
```
