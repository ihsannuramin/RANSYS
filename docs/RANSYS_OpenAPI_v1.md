# RANSYS OpenAPI v1 — Contract Decisions

**Document Type:** External REST Contract Specification  
**Version:** 1.0  
**Machine-readable source:** `RANSYS_OpenAPI_v1.yaml`

## 1. Scope

RANSYS API v1 exposes:

```text
POST /api/v1/inquiries
POST /api/v1/payments
POST /api/v1/transfers
POST /api/v1/refunds
POST /api/v1/reversals
POST /api/v1/voids
GET  /api/v1/transactions/{ransysTransactionId}
```

The public DTO layer is intentionally separate from the Canonical Transaction model and persistence entities.

## 2. Versioning

Breaking behavior changes require `/api/v2/...`.

Backward-compatible additive optional fields may remain within v1.

## 3. Security

Baseline request security:

```text
mTLS
+
RANSYS signed HTTP message profile
```

Headers:

```text
X-Ransys-Client-Id
X-Ransys-Timestamp
X-Ransys-Nonce
Content-Digest
Signature-Input
Signature
```

`X-Ransys-Nonce` is anti-replay state, not business idempotency.

`Idempotency-Key` is a separate optional retry key.

## 4. Business Idempotency

Baseline:

```text
authenticated channel + clientReference + canonical fingerprint
```

Same active reference + same fingerprint returns the current/existing transaction.

Same active reference + different fingerprint returns HTTP 409 / canonical duplicate-conflict code.

## 5. Money

Public JSON uses exact decimal strings:

```json
{"value":"100000.00","currency":"IDR"}
```

Public clients do not submit internal currency-definition versions. Core resolves and captures the active definition.

## 6. HTTP Status vs Transaction Status

HTTP describes the API/security boundary.

`responseCode` + `transactionStatus` describe business truth.

### HTTP 200

Normal transaction processing occurred, including business results:

```text
SUCCESS
FAILED
PENDING
IN_DOUBT
```

### HTTP 400
Malformed/schema/canonical validation failure before normal transaction processing.

### HTTP 401
Authentication/signature/certificate/timestamp/nonce failure.

### HTTP 403
Authenticated but forbidden.

### HTTP 409
Conflicting duplicate client reference.

### HTTP 429
Gateway/client rate-limit rejection before provider financial execution.

### HTTP 503
Mandatory durable/financial dependency unavailable.

For financial requests:

```text
cannot establish durable transaction + reserve
=> no provider financial request
```

## 7. Response Contract

```json
{
  "ransysTransactionId": "0199aabb-ccdd-7eef-8123-0123456789ab",
  "clientReference": "PAY-001",
  "responseCode": "0000",
  "responseMessage": "Success",
  "transactionStatus": "SUCCESS",
  "references": {},
  "data": {},
  "timestamp": "2026-09-27T10:15:31+07:00"
}
```

Timeout ambiguity:

```json
{
  "ransysTransactionId": "0199aabb-ccdd-7eef-8123-0123456789ab",
  "clientReference": "PAY-001",
  "responseCode": "1002",
  "responseMessage": "Transaction response timeout",
  "transactionStatus": "IN_DOUBT",
  "data": {},
  "timestamp": "2026-09-27T10:15:35+07:00"
}
```

`responseCode` and `transactionStatus` are separate concepts.

## 8. Transaction Detail

The status endpoint may expose:

```text
processingStatus
financialStatus
reconciliationStatus
settlementStatus
```

It does not expose provider topology, ledger account IDs, wallet reservation IDs, raw-message URIs, row versions, or rule internals.

## 9. Refund

Refund is a child transaction with its own RANSYS transaction ID.

Refundable fees come from the original transaction's captured fee-component refund policy:

```text
NONE
PRO_RATA
FULL
```

Default is `NONE`.

Clients cannot arbitrarily provide accounting fee refunds.

## 10. Reversal

Reversal is a child transaction.

Starting reversal does not overwrite original processing state.

This allows:

```text
Original Payment = IN_DOUBT
Reversal Child   = PROCESSING
```

at the same time.

## 11. VOID

VOID is explicit and must not be silently translated to reversal/refund unless provider/product configuration explicitly declares equivalent semantics.

## 12. Metadata

Metadata is extension-only.

Do not put canonical fields or secrets in metadata.

Forbidden sensitive examples:

```text
PIN
PIN block
CVV
credentials
API secret
private key
```

## 13. Timestamp

All timestamps are RFC 3339 / ISO 8601 timezone-aware values.

## 14. Compatibility

Changing public monetary serialization from decimal string to JSON number is considered a breaking contract change.

## 15. Response Code Dependency

The full four-digit code set remains governed by the future `RANSYS Response Code Catalog v1`.
