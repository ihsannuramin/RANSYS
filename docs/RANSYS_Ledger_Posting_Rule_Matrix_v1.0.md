# RANSYS Ledger Posting Rule Matrix v1.0

**Document Type:** Detailed Engineering Design — Financial Ledger Rules  
**Product:** RANSYS Universal Transaction Switching & Processing Platform  
**Primary Database:** PostgreSQL  
**Status:** Draft v1 for engineering review  
**Depends On:** RANSYS PRD v1.0, RANSYS Database ERD v1.0

---

# 1. Purpose

Dokumen ini mendefinisikan bagaimana setiap business event yang mengubah posisi financial RANSYS harus diterjemahkan menjadi:

1. perubahan wallet projection;
2. balance reservation;
3. immutable `ledger_transaction`;
4. balanced debit/credit `ledger_entries`;
5. transaction/financial status;
6. audit/outbox event.

Dokumen ini adalah penghubung antara PRD, ERD, dan implementasi Ledger Posting Service.

---

# 2. Financial Model Baseline

RANSYS menggunakan:

```text
PREFUND ONLY
```

Merchant menyetor dana terlebih dahulu. Dana merchant direpresentasikan sebagai liability RANSYS kepada merchant.

Baseline wallet:

```text
ledger_balance
available_balance
reserved_balance
```

Financial rule utama:

```text
available_balance >= 0
reserved_balance  >= 0

available_balance + reserved_balance
= merchant prefund liability represented by wallet
```

Tidak ada credit facility atau overdraft pada baseline.

---

# 3. Accounting Perspective

Agar double-entry konsisten, wallet merchant tidak diperlakukan sebagai "cash milik RANSYS".

Jika merchant memiliki saldo Rp1.000.000, secara accounting concept RANSYS memiliki kewajiban sebesar Rp1.000.000 kepada merchant sampai saldo tersebut digunakan untuk transaksi.

Canonical account groups:

```text
ASSET
├── CASH_CLEARING
├── PROVIDER_RECEIVABLE
└── OTHER_RECEIVABLE

LIABILITY
├── MERCHANT_AVAILABLE
├── MERCHANT_RESERVED
├── PROVIDER_PAYABLE
├── TAX_PAYABLE
└── OTHER_PAYABLE

REVENUE
├── RANSYS_FEE_REVENUE
└── OTHER_REVENUE

EXPENSE
├── PROVIDER_FEE_EXPENSE
└── OTHER_EXPENSE

CONTROL / SUSPENSE
├── TOPUP_CLEARING
├── REFUND_CLEARING
└── ADJUSTMENT_CLEARING
```

Exact Chart of Accounts code akan dikunci pada versi berikutnya, tetapi semantic account type wajib mengikuti prinsip di dokumen ini.

---

# 4. Debit / Credit Convention

Untuk account type utama:

| Account Type | Debit | Credit |
|---|---|---|
| Asset | Increase | Decrease |
| Liability | Decrease | Increase |
| Revenue | Decrease | Increase |
| Expense | Increase | Decrease |

Contoh merchant top-up Rp1.000.000:

```text
DR CASH_CLEARING                 1,000,000
CR MERCHANT_AVAILABLE           1,000,000
```

RANSYS menerima/recognize dana dan menambah kewajiban saldo kepada merchant.

---

# 5. Core Posting Invariants

Semua posting financial wajib memenuhi:

```text
SUM(DEBIT) = SUM(CREDIT)
```

Selain itu:

1. Historical ledger entry immutable.
2. Correction dilakukan dengan compensating transaction.
3. Tidak boleh direct edit balance tanpa ledger transaction.
4. Setiap financial posting memiliki business reference.
5. Posting operation harus idempotent.
6. Satu business event tidak boleh menghasilkan posting kedua jika `posting_key` sama.
7. Wallet projection dan ledger posting diubah dalam DB transaction yang sama.
8. Provider network call tidak boleh dilakukan saat wallet row lock masih ditahan.
9. `IN_DOUBT` tidak me-release reservation.
10. Manual financial action harus menghasilkan audit event dan maker-checker jika diwajibkan policy.

---

# 6. Proposed Ledger Posting Idempotency

Tambahkan logical posting key pada `ledger.ledger_transactions`:

```text
posting_key
```

Contoh:

```text
TX123:RESERVE
TX123:POST
TX123:RELEASE
TX123:REVERSAL:01
TX123:REFUND:RF001
TOPUP:TP001
ADJUSTMENT:ADJ001
```

Recommended constraint:

```text
UNIQUE(posting_key)
```

Tujuan: retry application/service tidak boleh menghasilkan duplicate ledger posting.

---

# 7. Core Ledger Accounts per Wallet

Untuk setiap wallet merchant minimal terdapat dua logical liability account:

```text
MERCHANT_AVAILABLE
MERCHANT_RESERVED
```

Contoh:

```text
wallet_id = WALLET-A

account_code:
MERCHANT:WALLET-A:AVAILABLE
MERCHANT:WALLET-A:RESERVED
```

Provider memiliki:

```text
PROVIDER:<provider_id>:PAYABLE
```

RANSYS:

```text
RANSYS:FEE_REVENUE
```

External fund movement:

```text
CASH_CLEARING
```

---

# 8. Important Distinction — Wallet Projection vs Ledger Entries

Wallet projection:

```text
available_balance
reserved_balance
ledger_balance
```

digunakan untuk keputusan realtime.

Ledger entries:

```text
debit / credit
```

merupakan immutable financial record.

Wallet projection harus dapat direkonstruksi/divalidasi dari ledger.

---

# 9. Operation Matrix Overview

| Operation | Wallet Available | Wallet Reserved | Ledger Effect |
|---|---:|---:|---|
| Top-up Confirmed | + | 0 | Increase Merchant Available Liability |
| Payment Reserve | - | + | Available Liability -> Reserved Liability |
| Payment Success | 0 | - | Reserved Liability -> Provider Payable + Revenue |
| Payment Failed | + | - | Reserved Liability -> Available Liability |
| Payment IN_DOUBT | 0 | 0 | No new financial posting |
| Reversal before final posting | + | - | Release reservation |
| Reversal after successful posting | + | 0 | Compensating posting |
| Refund | + | 0 | Provider/Refund receivable -> Merchant Available |
| Manual Credit Adjustment | + | 0 | Clearing/Adjustment -> Merchant Available |
| Manual Debit Adjustment | - | 0 | Merchant Available -> Clearing/Adjustment |

---

# 10. OP-01 — Merchant Top-up Confirmed

## Preconditions

- top-up request exists;
- external transfer/fund confirmation sudah diterima;
- maker-checker completed if manual;
- reference unique;
- amount > 0.

Example:

```text
Top-up = Rp1,000,000
```

## Wallet

Before:

```text
available = 0
reserved  = 0
ledger    = 0
```

After:

```text
available = 1,000,000
reserved  = 0
ledger    = 1,000,000
```

## Ledger

```text
DR CASH_CLEARING                 1,000,000
CR MERCHANT_AVAILABLE           1,000,000
```

## Posting

```text
operation_type = TOPUP
posting_key    = TOPUP:<topup_reference>
```

## Backoffice mutation

```text
TOPUP CONFIRMED
Available +1,000,000
Reason: VERIFIED_TOPUP
```

## Failure rule

Jika top-up belum verified, jangan menambah `available_balance`.

---

# 11. OP-02 — Payment Reserve

Example:

```text
Principal        = 100,000
Guaranteed Fee   =   2,500
Total Reserve    = 102,500
```

## Preconditions

```text
available_balance >= 102,500
```

Wallet row harus di-lock:

```sql
SELECT *
FROM ledger.wallets
WHERE wallet_id = :wallet_id
FOR UPDATE;
```

## Wallet

Before:

```text
available = 1,000,000
reserved  = 0
ledger    = 1,000,000
```

After:

```text
available = 897,500
reserved  = 102,500
ledger    = 1,000,000
```

Notice:

```text
ledger_balance tidak berubah saat reserve
```

Karena dana belum benar-benar consumed; hanya berpindah state.

## Ledger

```text
DR MERCHANT_AVAILABLE             102,500
CR MERCHANT_RESERVED              102,500
```

## Reservation

```text
status        = ACTIVE
amount        = 100,000
fee_amount    = 2,500
total_reserved_amount = 102,500
```

## Transaction

```text
processing_status = PROCESSING
financial_status  = RESERVED
```

## Posting

```text
operation_type = RESERVE
posting_key    = TX123:RESERVE
```

---

# 12. OP-03 — Payment Success

Assumption baseline example:

```text
Principal payable to provider = 100,000
RANSYS merchant fee revenue   =   2,500
Total consumed                = 102,500
```

## Preconditions

- transaction has active reservation;
- provider result is definitive SUCCESS;
- success finalization posting does not yet exist.

## Wallet

Before:

```text
available = 897,500
reserved  = 102,500
ledger    = 1,000,000
```

After:

```text
available = 897,500
reserved  = 0
ledger    = 897,500
```

## Ledger

```text
DR MERCHANT_RESERVED               102,500
CR PROVIDER_PAYABLE                100,000
CR RANSYS_FEE_REVENUE                2,500
```

Balanced:

```text
Debit  = 102,500
Credit = 102,500
```

## Reservation

```text
ACTIVE -> COMMITTED
```

## Transaction

```text
processing_status = SUCCESS
financial_status  = POSTED
```

## Posting

```text
operation_type = POST
posting_key    = TX123:POST
```

## Backoffice mutation

```text
PAYMENT POSTED
Principal      -100,000
Merchant Fee     -2,500
Reserved       -102,500
Ledger Balance 897,500
```

---

# 13. Fee Composition Refinement

`merchant_fee_amount` tidak selalu identik dengan RANSYS revenue.

Future/provider rule dapat memiliki:

```text
merchant_charge
provider_cost
tax
commission
ransys_margin
```

Contoh:

```text
Principal charged merchant       100,000
Merchant service fee               2,500
Provider payable                  99,500
Tax payable                          250
RANSYS net revenue                 2,750
Total merchant consumption       102,500
```

Possible posting:

```text
DR MERCHANT_RESERVED              102,500
CR PROVIDER_PAYABLE                99,500
CR TAX_PAYABLE                        250
CR RANSYS_FEE_REVENUE               2,750
```

Therefore ERD v1.1 should consider:

```text
core.transaction_fee_components
```

instead of assuming one fee column is the final accounting split.

---

# 14. OP-04 — Payment Failed Before Provider Financial Effect

Provider definitively returns failed/declined.

## Wallet

Before:

```text
available = 897,500
reserved  = 102,500
ledger    = 1,000,000
```

After:

```text
available = 1,000,000
reserved  = 0
ledger    = 1,000,000
```

## Ledger

```text
DR MERCHANT_RESERVED              102,500
CR MERCHANT_AVAILABLE             102,500
```

## Reservation

```text
ACTIVE -> RELEASED
```

## Transaction

```text
processing_status = FAILED
financial_status  = RELEASED
```

## Posting

```text
operation_type = RELEASE
posting_key    = TX123:RELEASE
```

---

# 15. OP-05 — Provider Timeout / IN_DOUBT

Provider request may have been processed but response was not obtained.

## Wallet

No new change:

```text
available remains 897,500
reserved remains 102,500
ledger remains 1,000,000
```

## Ledger

```text
NO NEW LEDGER POSTING
```

The existing `RESERVE` posting remains valid.

## Reservation

```text
status      = ACTIVE
hold_reason = IN_DOUBT
```

## Transaction

```text
processing_status = IN_DOUBT
financial_status  = RESERVED
```

## Important

Do not:

```text
release reserve
post success
retry purchase to another provider
```

until financial truth is resolved.

---

# 16. OP-06 — IN_DOUBT Resolved SUCCESS

If status check/callback/reconciliation determines original transaction succeeded:

Execute **OP-03 Payment Success**.

Posting key remains:

```text
TX123:POST
```

even if resolution source is RECON/CALLBACK.

Additional metadata:

```text
resolution_source = STATUS_CHECK | CALLBACK | RECONCILIATION | ADVICE
```

No duplicate posting is allowed.

---

# 17. OP-07 — IN_DOUBT Resolved FAILED / NOT PROCESSED

If definitive evidence proves provider did not process the transaction:

Execute **OP-04 Payment Release**.

Metadata:

```text
resolution_source = STATUS_CHECK | REVERSAL | RECONCILIATION
```

---

# 18. OP-08 — Reversal While Reservation Still Active

Scenario:

```text
Transaction = IN_DOUBT
Reservation = ACTIVE
Provider confirms successful reversal / original effect cancelled
```

Financially this is release of reservation, not reversal of a posted transaction.

## Ledger

```text
DR MERCHANT_RESERVED              102,500
CR MERCHANT_AVAILABLE             102,500
```

## Wallet

```text
available += 102,500
reserved  -= 102,500
ledger unchanged
```

## Reservation

```text
ACTIVE -> RELEASED
```

## Transaction

```text
processing_status = REVERSED
financial_status  = RELEASED
```

## Posting

```text
operation_type = RELEASE
posting_key    = TX123:REVERSAL_RELEASE
```

Reason:

```text
PROVIDER_REVERSAL_CONFIRMED
```

---

# 19. OP-09 — Reversal After Payment Was Posted

Scenario:

```text
Original payment already SUCCESS + POSTED
Provider later confirms reversal
```

Original ledger must not be edited.

Original:

```text
DR MERCHANT_RESERVED               102,500
CR PROVIDER_PAYABLE                100,000
CR RANSYS_FEE_REVENUE                2,500
```

Compensating reversal:

```text
DR PROVIDER_PAYABLE                100,000
DR RANSYS_FEE_REVENUE                2,500
CR MERCHANT_AVAILABLE              102,500
```

## Wallet

Before:

```text
available = 897,500
reserved  = 0
ledger    = 897,500
```

After:

```text
available = 1,000,000
reserved  = 0
ledger    = 1,000,000
```

## Posting

```text
operation_type = REVERSAL
posting_key    = TX123:REVERSAL:<reversal_id>
compensates_ledger_transaction_id = <original POST>
```

---

# 20. OP-10 — Full Refund, Merchant Fee Non-refundable

Original success:

```text
Principal = 100,000
Fee       =   2,500
```

Refund policy:

```text
Principal refunded = 100,000
Fee refunded       = 0
```

Assuming provider owes/returns refund amount:

```text
DR PROVIDER_RECEIVABLE / REFUND_CLEARING    100,000
CR MERCHANT_AVAILABLE                       100,000
```

## Wallet

```text
available += 100,000
ledger    += 100,000
```

Merchant final consumption remains:

```text
2,500 fee
```

## Posting

```text
operation_type = REFUND
posting_key    = TX123:REFUND:<refund_reference>
```

---

# 21. OP-11 — Full Refund Including Merchant Fee

If business policy refunds fee:

```text
DR PROVIDER_RECEIVABLE / REFUND_CLEARING    100,000
DR RANSYS_FEE_REVENUE                         2,500
CR MERCHANT_AVAILABLE                       102,500
```

Wallet:

```text
available += 102,500
ledger    += 102,500
```

Historical original payment remains intact.

---

# 22. OP-12 — Partial Refund

Example:

```text
Original principal = 100,000
Partial refund     =  40,000
Fee refundable     = 0
```

Posting:

```text
DR PROVIDER_RECEIVABLE / REFUND_CLEARING     40,000
CR MERCHANT_AVAILABLE                        40,000
```

RANSYS must enforce:

```text
SUM(successful refunds) <= refundable original amount
```

unless a specific adjustment workflow explicitly overrides it.

---

# 23. Refund State Safety

Refund request should be modeled as its own business action/transaction and reference:

```text
original_transaction_id
```

Refund must be idempotent.

Suggested unique business key:

```text
original_transaction_id + refund_reference
```

Manual refund requires maker-checker according to baseline policy.

---

# 24. OP-13 — Manual Credit Adjustment

Example: Finance determines merchant must receive +50,000 due to validated exception.

Must have:

```text
reason
supporting_reference
maker
checker
```

Possible posting:

```text
DR ADJUSTMENT_CLEARING              50,000
CR MERCHANT_AVAILABLE               50,000
```

Wallet:

```text
available += 50,000
ledger    += 50,000
```

Posting:

```text
operation_type = ADJUSTMENT
posting_key    = ADJ:<adjustment_reference>
```

No direct wallet edit.

---

# 25. OP-14 — Manual Debit Adjustment

Example: validated over-credit correction of Rp50,000.

Precondition:

```text
available_balance >= 50,000
```

Posting:

```text
DR MERCHANT_AVAILABLE               50,000
CR ADJUSTMENT_CLEARING              50,000
```

Wallet:

```text
available -= 50,000
ledger    -= 50,000
```

If available balance is insufficient:

```text
REJECT / INVESTIGATE
```

Baseline prefund model must not create negative balance.

---

# 26. Adjustment Clearing Account

`ADJUSTMENT_CLEARING` is a control/suspense account.

It must not become a permanent unexplained balance.

Operational control should monitor:

```text
ADJUSTMENT_CLEARING outstanding balance
```

Each adjustment should eventually map to proper business/accounting classification according to company accounting policy.

---

# 27. OP-15 — Top-up Correction / Top-up Reversal

If confirmed top-up was later proven invalid and merchant has sufficient available balance:

Original:

```text
DR CASH_CLEARING
CR MERCHANT_AVAILABLE
```

Compensating:

```text
DR MERCHANT_AVAILABLE
CR CASH_CLEARING
```

Wallet decreases.

If merchant already consumed the balance and available funds are insufficient:

```text
DO NOT FORCE NEGATIVE BALANCE
```

Create financial exception for controlled investigation/adjustment.

---

# 28. OP-16 — Provider Settlement Confirmation

Settlement calculation is asynchronous and actual payout is outside baseline scope.

If an external payment system later confirms payment of provider payable, optional accounting integration can post:

```text
DR PROVIDER_PAYABLE
CR CASH_CLEARING
```

This is **not** part of realtime payment success path.

If RANSYS does not own actual payout accounting, this operation remains outside core ledger scope and Provider Payable may instead be exported to external finance/accounting system.

---

# 29. OP-17 — Provider Refund Cash Received

When external confirmation shows provider has returned refund cash:

Earlier refund recognition:

```text
DR PROVIDER_RECEIVABLE
CR MERCHANT_AVAILABLE
```

Cash receipt:

```text
DR CASH_CLEARING
CR PROVIDER_RECEIVABLE
```

This separates customer credit timing from provider cash settlement timing.

---

# 30. Transaction Status vs Ledger Status

Do not infer ledger posting only from transaction status.

Example:

```text
processing_status = SUCCESS
financial_status  = POSTED
```

For IN_DOUBT:

```text
processing_status = IN_DOUBT
financial_status  = RESERVED
```

For failed pre-provider:

```text
processing_status = FAILED
financial_status  = RELEASED
```

For reversal after posting:

```text
processing_status = REVERSED
financial_status  = REVERSED
```

---

# 31. Recommended Financial Status Enum

Baseline:

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

This is separate from `processing_status`.

---

# 32. Ledger Transaction Operation Types

Recommended baseline:

```text
TOPUP
RESERVE
POST
RELEASE
REVERSAL
REFUND
ADJUSTMENT_CREDIT
ADJUSTMENT_DEBIT
SETTLEMENT_CLEAR
REFUND_CLEAR
```

---

# 33. Balance Reservation Status

```text
ACTIVE
COMMITTED
RELEASED
```

Rules:

```text
ACTIVE -> COMMITTED
ACTIVE -> RELEASED
```

No:

```text
COMMITTED -> ACTIVE
RELEASED  -> ACTIVE
```

Any correction after commit/release uses a new financial action.

---

# 34. Ledger Transaction Immutability

Allowed:

```text
INSERT new ledger transaction
INSERT compensating ledger transaction
```

Not allowed:

```text
UPDATE amount on posted ledger transaction
DELETE posted ledger transaction
UPDATE ledger entry side
```

Application role should not have generic permission for destructive change on posted ledger data.

---

# 35. Controlled Posting Function

Recommended engineering model:

```text
LedgerPostingService
        |
        v
PostgreSQL transaction
        |
        +--> validate posting_key
        +--> lock wallet
        +--> validate reservation/state
        +--> insert ledger_transaction
        +--> insert ledger_entries
        +--> verify debit == credit
        +--> update wallet projection
        +--> update reservation
        +--> insert outbox
        |
      COMMIT
```

Whether debit/credit validation is implemented with stored procedure or application + deferred DB constraint remains an ERD v1.1 decision.

---

# 36. Debit/Credit Validation

Every ledger transaction must balance in the same currency definition:

```text
SUM(DR amount) = SUM(CR amount)
```

Mixed currency posting in one ledger transaction should be prohibited for baseline.

If conversion is required in future:

```text
use explicit FX/conversion transaction
```

with separate balanced legs.

This supports future redenomination/multi-currency expansion without hidden conversions.

---

# 37. Concurrency Rule — Reserve

Only one DB transaction may mutate a wallet projection at a time.

```sql
SELECT ...
FOR UPDATE;
```

Sequence:

```text
lock wallet
check balance
reserve
post ledger
commit
release lock
```

Network/provider I/O occurs after commit.

---

# 38. Concurrency Rule — Finalization

Success/failure/reversal finalization must lock:

```text
transaction
reservation
wallet
```

in a consistent order to minimize deadlock risk.

Recommended lock order:

```text
1. transaction
2. wallet
3. reservation
```

or another fixed order agreed by implementation team.

The key requirement is:

```text
all code paths use the same lock order
```

---

# 39. Duplicate Provider Response

If provider callback/status response is delivered twice:

First:

```text
posting_key TX123:POST
-> SUCCESS
```

Second:

```text
posting_key TX123:POST
-> already exists
-> NO NEW POSTING
```

Application returns idempotent result.

---

# 40. Out-of-order Result

Example:

```text
1. request timeout -> IN_DOUBT
2. reconciliation says SUCCESS -> POST
3. delayed original SUCCESS callback arrives
```

Delayed callback must detect transaction already financially POSTED and must not create second POST.

Store callback/audit history but no new financial posting.

---

# 41. Balance Mutation Projection

Backoffice mutation should display business-readable movement.

Example payment reserve:

```text
2026-09-26 20:00:01
TX123 PAYMENT RESERVE

Available:
1,000,000 -> 897,500

Reserved:
0 -> 102,500

Ledger Balance:
1,000,000 -> 1,000,000

Reason:
PAYMENT_PROCESSING
```

Success:

```text
TX123 PAYMENT POSTED

Reserved:
102,500 -> 0

Ledger Balance:
1,000,000 -> 897,500
```

Timeout:

```text
TX123 PROVIDER TIMEOUT

No financial movement.
Reserve remains 102,500.
Status: IN_DOUBT
```

---

# 42. Reconciliation Financial Safety

Recon Engine may trigger automatic financial action only for deterministic rule.

Example:

```text
RANSYS = IN_DOUBT
Provider = SUCCESS
Amount = MATCH
Reference = MATCH
```

Action:

```text
LedgerPostingService.POST(TX123)
```

Recon Engine must not directly insert ledger entries.

For mismatch:

```text
RECON_EXCEPTION
```

No automatic adjustment.

---

# 43. Maker-Checker Financial Operations

Mandatory baseline:

```text
Manual Top-up
Refund
Manual Reversal
Transaction Limit Change
```

Additionally recommended:

```text
Manual financial adjustment
```

Execution occurs only after approval.

Approval record references business action; ledger posting references approved action/reference.

---

# 44. Suggested `transaction_fee_components` Refinement

Because settlement requirements include fee/commission/tax, ERD v1.1 should consider:

```text
core.transaction_fee_components
```

Suggested fields:

```text
fee_component_id
ransys_transaction_id
component_type
charged_amount
accounting_amount
currency_definition_id
beneficiary_type
beneficiary_id
refundable
calculation_rule_version
created_at
```

Possible `component_type`:

```text
MERCHANT_SERVICE_FEE
PROVIDER_FEE
COMMISSION
TAX
RANSYS_MARGIN
OTHER
```

This avoids assuming:

```text
merchant_fee == RANSYS revenue
```

---

# 45. Suggested `topup_requests` Refinement

Manual top-up lifecycle deserves explicit entity rather than only ledger entry:

```text
topup_requests
---------------
topup_id
merchant_id
wallet_id
reference
amount
status
maker_user_id
approval_request_id
verified_at
executed_at
created_at
```

Possible states:

```text
DRAFT
PENDING_APPROVAL
APPROVED
VERIFIED
POSTED
REJECTED
FAILED
```

Posting only occurs at `VERIFIED/POSTED` boundary.

---

# 46. Suggested Refund Aggregate

For safe partial refund tracking, either:

1. treat each refund as `core.transactions` with `transaction_type = REFUND`; or
2. add `refunds` aggregate.

Preferred baseline:

```text
REFUND is a transaction
```

with:

```text
original_transaction_id
```

This reuses transaction identity, state history, attempts, idempotency, maker-checker, and adapter framework.

---

# 47. Reversal vs Refund

RANSYS must distinguish:

```text
REVERSAL
```

A compensating/cancellation flow for the original transaction, typically due to timeout, failed completion, or network/business reversal semantics.

versus:

```text
REFUND
```

A new post-success business action returning all/part of value after original transaction was validly completed.

They must not share the same posting key or operational semantics.

---

# 48. Provider Fee / Cost Edge Case

If provider cost is recognized only after provider response, but merchant charge was reserved before provider call:

Reserve:

```text
merchant amount + maximum guaranteed merchant charge
```

Final posting must use the pricing/provider rule version captured by the transaction.

Do not query current fee configuration during finalization if config may have changed after transaction started.

Persist:

```text
fee_rule_version
provider_policy_version
```

with transaction.

---

# 49. Fee Change During Transaction

Example:

```text
22:59 transaction reserves fee = 2,500
23:00 new fee becomes 3,000
23:01 original provider returns SUCCESS
```

Original transaction posts:

```text
2,500
```

not 3,000.

Reason:

```text
financial terms are fixed at transaction validation/reservation
```

The captured fee/config version is authoritative for that transaction.

---

# 50. Currency Definition Rule

Every ledger entry participating in one posting must use the same `currency_definition_id` for baseline.

Historical posting never changes even if IDR definition changes later.

Example:

```text
IDR definition v1 transaction
```

stays v1 forever.

Future redenomination conversion should use an explicit conversion business event, not rewrite history.

---

# 51. Business Date and Effective Date

Ledger posting carries:

```text
created_at
effective_at
```

`created_at` = when RANSYS wrote the ledger.

`effective_at` = financial effective time according to business rule.

Settlement/reconciliation may use:

```text
financial_posted_at
business_date
settlement_date
```

without rewriting ledger creation history.

---

# 52. Financial Event Audit Fields

Each `ledger_transaction` should be traceable through:

```text
ledger_transaction_id
posting_key
ransys_transaction_id
operation_type
business_reference
created_by_type
created_by_id
approval_request_id
compensates_ledger_transaction_id
effective_at
created_at
```

Proposed ERD v1.1 refinement:

```text
approval_request_id
posting_key
```

on `ledger.ledger_transactions`.

---

# 53. Posting Failure Behavior

If ledger posting transaction fails:

```text
ROLLBACK EVERYTHING in the same DB transaction
```

Do not leave:

```text
wallet updated but ledger missing
```

or:

```text
ledger inserted but wallet projection unchanged
```

Financial transaction result must fail closed if consistent posting cannot be committed.

---

# 54. Outbox Atomicity

Every financial mutation should emit appropriate outbox event in same DB transaction.

Examples:

```text
WALLET_RESERVED
TRANSACTION_POSTED
RESERVATION_RELEASED
TOPUP_POSTED
REFUND_POSTED
REVERSAL_POSTED
ADJUSTMENT_POSTED
```

If outbox transport is unavailable, transaction commit may still succeed because durable outbox row is the async guarantee.

---

# 55. Integrity Checker

Periodic financial integrity worker should verify:

```text
wallet available >= 0
wallet reserved >= 0

active reservations sum agrees with reserved projection

ledger transaction debit == credit

wallet projection agrees with reconstructable ledger state

no duplicate posting_key

no COMMITTED reservation without corresponding POST/reversal outcome

no RELEASED reservation with active reserved amount
```

Any mismatch:

```text
CRITICAL FINANCIAL INTEGRITY ALERT
```

No automatic "fix balance" job.

---

# 56. Recommended Monitoring Metrics

Financial:

```text
wallet_negative_count
wallet_projection_mismatch_count
ledger_unbalanced_count
duplicate_posting_attempt_count
active_reservation_count
indoubt_reserved_amount
reservation_age_p95
manual_adjustment_value
refund_value
reversal_value
topup_value
```

Operational:

```text
ledger_post_latency
wallet_lock_wait_ms
wallet_lock_timeout_count
posting_failure_rate
```

---

# 57. Financial Test Cases — Mandatory

## Same-wallet race

```text
Available = 100,000

TX A requires 80,000
TX B requires 80,000
```

Expected:

```text
one RESERVE succeeds
one INSUFFICIENT_BALANCE
available never negative
```

## Duplicate reserve retry

Call reserve twice with same `posting_key`.

Expected:

```text
only one ledger transaction
only one reservation effect
```

## Duplicate success callback

Expected:

```text
one POST only
```

## Timeout

Expected:

```text
reservation remains ACTIVE
no release
```

## Failed transaction

Expected:

```text
exact reserve reversal
```

## Reversal after posted

Expected:

```text
compensating entries
original ledger untouched
```

## Partial refund

Expected:

```text
refund cannot exceed remaining refundable amount
```

## Manual adjustment

Expected:

```text
cannot execute before checker approval
```

---

# 58. Posting Matrix — Compact Reference

| Event | Debit | Credit | Wallet Effect |
|---|---|---|---|
| TOPUP | Cash/Clearing | Merchant Available | Available + |
| RESERVE | Merchant Available | Merchant Reserved | Available -, Reserved + |
| PAYMENT SUCCESS | Merchant Reserved | Provider Payable + Revenue/Tax | Reserved -, Ledger - |
| PAYMENT FAILED | Merchant Reserved | Merchant Available | Reserved -, Available + |
| IN_DOUBT | — | — | Hold remains |
| REVERSAL before POST | Merchant Reserved | Merchant Available | Hold released |
| REVERSAL after POST | Provider Payable + Revenue reversal | Merchant Available | Available + |
| REFUND principal | Provider Receivable/Refund Clearing | Merchant Available | Available + |
| REFUND fee | Revenue reversal | Merchant Available | Available + |
| ADJUSTMENT CREDIT | Adjustment Clearing | Merchant Available | Available + |
| ADJUSTMENT DEBIT | Merchant Available | Adjustment Clearing | Available - |
| PROVIDER SETTLEMENT | Provider Payable | Cash/Clearing | No merchant wallet effect |
| PROVIDER REFUND CASH | Cash/Clearing | Provider Receivable | No merchant wallet effect |

---

# 59. Changes Proposed to ERD v1.1

The Ledger Rule Matrix reveals several useful refinements:

1. Add `posting_key` to `ledger.ledger_transactions`.
2. Add `approval_request_id` to manual-origin ledger transaction.
3. Add `transaction_fee_components` to support provider fee/tax/commission split.
4. Consider `topup_requests` aggregate.
5. Formalize `financial_status` enum.
6. Formalize provider receivable / clearing accounts.
7. Add explicit integrity-check/audit strategy.
8. Add refundable tracking for refund safety.
9. Persist exact pricing/config version used when reserving transaction.
10. Treat refund as its own transaction referencing `original_transaction_id`.

These are **engineering refinements**, not changes to the locked product principles.

---

# 60. Next Engineering Artifact

After this matrix, the next recommended artifact is:

```text
RANSYS Transaction State Transition Matrix v1
```

It should define, for every state:

```text
allowed previous state
allowed next state
trigger
financial precondition
ledger action
reservation action
provider capability requirement
manual/automatic source
outbox event
invalid transition behavior
```

After that:

```text
Payment SUCCESS Sequence Diagram
Timeout -> IN_DOUBT Sequence Diagram
Status Check Recovery Sequence Diagram
Reversal Sequence Diagram
Refund Sequence Diagram
```

---

# 61. Final Ledger Principle

> **RANSYS tidak pernah "mengubah angka saldo". RANSYS mengeksekusi business event yang menghasilkan immutable balanced ledger posting, lalu wallet balance menjadi projection konsisten dari seluruh posting tersebut.**
