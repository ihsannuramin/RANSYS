# RANSYS Provider Adapter Contract v1.0

**Document Type:** Internal Service Contract  
**Version:** 1.0  
**Wire Contract:** `RANSYS_Provider_Adapter_v1.proto`  
**Reference C# Contract:** `RANSYS_Provider_Adapter_Contracts_v1.cs`

## 1. Principle

Provider-specific protocol logic stops at the adapter boundary.

Baseline:

```text
1 logical provider = 1 independently deployable adapter
```

Lite deployments may bind the same semantic contract in-process.

Remote adapter deployments use gRPC/Protobuf v1 by default.

## 2. Capability Catalog

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

VOID is explicit and is not automatically mapped to reversal or refund.

## 3. Core-to-Adapter Operations

```text
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
GetCapabilitiesAsync
HealthCheckAsync
GetProviderBalanceAsync
```

Unsupported operations return normalized capability failure.

## 4. Reversal / Refund Identity

Reversal and refund are child transactions.

Requests carry:

```text
child ransysTransactionId
originalTransactionId
original provider references if available
```

Adapter never overwrites original transaction state.

## 5. Provider Request

Normalized request includes:

```text
ransysTransactionId
originalTransactionId?
attemptId
transactionType
product
amount?
customer?
source?
destination?
references
executionContext
correlation
metadata
```

## 6. Adapter Does Not Own

Adapter has no direct authority over:

```text
merchant wallet balance
reservation
ledger
maker-checker
settlement
reconciliation decision
routing/failover selection
```

## 7. Provider Result

```text
ProviderResult
├── outcome
├── finality
├── transport
├── ransysResponseCode
├── providerResponseCode?
├── providerResponseMessage?
├── references
├── data
├── retryHint?
├── raw request/response references?
└── receivedAt
```

## 8. Outcome

```text
SUCCESS
FAILED
PENDING
IN_DOUBT
NOT_SENT
```

## 9. Finality

```text
DEFINITIVE
NON_FINAL
AMBIGUOUS
NOT_APPLICABLE
```

Recommended combinations:

```text
SUCCESS   + DEFINITIVE
FAILED    + DEFINITIVE
PENDING   + NON_FINAL
IN_DOUBT  + AMBIGUOUS
NOT_SENT  + NOT_APPLICABLE
```

Core validates impossible combinations.

## 10. requestSent

`requestSent` is financially safety-critical.

```text
false
=> safe failover may be possible

true + unknown final result
=> no blind financial failover
```

If adapter cannot prove a request was not sent, it must use the conservative ambiguous interpretation.

## 11. Transport Status

```text
NOT_SENT
SENT
RESPONSE
TIMEOUT
CONNECTION_ERROR
PROTOCOL_ERROR
```

Transport status does not equal financial status.

## 12. Retry Ownership

Adapter may return technical hints:

```text
safeToRetryTransport
suggestedDelay
```

Core owns actual financial retry/failover decision.

Hidden retries after ambiguous send are prohibited.

## 13. Timeout

Adapter should distinguish, when technically possible:

```text
connect failure before send
send completed
read timeout after send
connection reset after possible send
```

## 14. Provider Mapping

Adapter maps provider-specific responses into normalized:

```text
RANSYS response code
outcome/finality
raw provider code/message
references
data
```

Raw provider code is retained but not directly exposed as merchant canonical response code.

## 15. Callback

Provider callback ingress terminates at the adapter.

Adapter:

```text
validates provider callback security
parses provider message
normalizes result
correlates transaction
forwards normalized callback to Core callback sink
```

Adapter does not directly mutate Core DB or Ledger.

Callbacks are at-least-once and Core processing is idempotent.

## 16. Duplicate / Conflicting Callback

Same already-finalized result:

```text
no second financial posting
```

Conflicting result:

```text
record provider contradiction
raise reconciliation exception
```

## 17. Health

Health reports technical/provider connectivity facts:

```text
HEALTHY
DEGRADED
UNHEALTHY
```

Routing Engine owns eligibility decisions.

Manual disable remains authoritative even if health is healthy.

## 18. Provider Balance

Optional provider balance returns actual observed provider balance only.

Estimated balance remains a RANSYS-side model and must not be labeled as actual provider balance.

## 19. Backpressure

When capacity is exhausted before provider send:

```text
Outcome = NOT_SENT
requestSent = false
```

Core may choose another eligible route.

## 20. Wallet Status Independence

Adapter does not know wallet `ACTIVE/FROZEN/CLOSED`.

Core enforces wallet semantics.

## 21. Error Categories

```text
CONNECTION
TIMEOUT
PROTOCOL
AUTHENTICATION
MAPPING
PROVIDER_DECLINE
CAPABILITY_UNSUPPORTED
BACKPRESSURE
UNKNOWN
```

## 22. Secrets

Core passes secret/authentication profile references, not plaintext secrets.

Adapter resolves actual secret material through the configured secret provider.

## 23. Raw Messages

Adapter may persist raw provider messages in restricted raw-message storage and return opaque URI references.

Never include PIN, PIN block, CVV, private keys, or credentials in normal raw-message storage.

## 24. Correlation

Every call propagates:

```text
ransysTransactionId
attemptId
correlationId
traceId
```

## 25. gRPC

Remote adapters use package:

```text
ransys.provider.v1
```

Breaking wire changes require a new major package/contract version.

## 26. Mandatory Adapter Tests

At minimum:

```text
success
definitive decline
explicit pending
connect failure before send
timeout after send
connection reset after possible send
malformed response
provider authentication failure
unsupported capability
backpressure before send
status check
reversal
refund
TRANSFER
VOID
duplicate callback
late callback
callback authentication failure
```

## 27. Acceptance Criteria

1. No provider-specific DTO leaks into Core.
2. `requestSent` is explicit.
3. Ambiguous timeout can be represented without FAILED.
4. Adapter cannot mutate wallet/ledger.
5. Reversal/refund child identity is preserved.
6. TRANSFER/VOID are explicit.
7. Callback processing is normalized and idempotent.
8. Secrets are referenced, not transported in plaintext.
9. Financial retry/failover remains Core-owned.
10. gRPC and in-process bindings share identical semantics.
