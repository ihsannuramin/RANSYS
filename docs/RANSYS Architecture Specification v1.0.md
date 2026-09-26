# RANSYS Architecture Specification v1.0

**System Name:** RANSYS  
**System Type:** Universal Transaction Switching & Processing Platform  
**Primary Backend:** C# / Modern .NET LTS  
**Primary Database:** PostgreSQL  
**Target OS:** Linux  
**Architecture Status:** Foundation Baseline v1.0

---

# 1. Executive Overview

RANSYS adalah switching dan transaction processing platform yang menerima transaksi dari berbagai channel dan merchant, kemudian meneruskannya ke provider yang sesuai.

Topologi umum:

```text
Merchant / B2B / POS / Application
                │
                ▼
              RANSYS
                │
       ┌────────┼─────────┐
       ▼        ▼         ▼
      Bank    Biller    Supplier
```

RANSYS harus dapat menangani berbagai domain payment dan transaction processing melalui arsitektur yang generic dan extensible.

Protocol awal yang didukung:

```text
REST / JSON
SOAP / XML
ISO 8583
TCP Socket / Proprietary Protocol
```

Jenis transaksi mencakup:

```text
Inquiry
Payment
Purchase
Transfer
Refund
Reversal
Void
Advice
Balance Inquiry
Status Check
Settlement
```

RANSYS merupakan **full transaction processing platform**, bukan hanya message router.

---

# 2. Architecture Principles

RANSYS menggunakan prinsip berikut.

## 2.1 Financial Consistency First

Financial correctness lebih penting daripada throughput maksimum.

Tidak boleh terjadi:

```text
negative balance
double debit
double posting
untracked adjustment
unexplained balance mutation
```

---

## 2.2 Fail Closed

Jika Transaction Database atau Ledger tidak tersedia:

```text
Financial Transaction
        ↓
      REJECT
```

RANSYS tidak boleh meneruskan transaksi ke provider tanpa berhasil mencatat financial state secara konsisten.

---

## 2.3 Transaction Timeout ≠ Transaction Failed

Timeout hanya menyatakan hasil komunikasi tidak diketahui.

Transaksi dapat menjadi:

```text
IN_DOUBT
```

sampai hasil sebenarnya ditemukan melalui:

```text
status check
callback
webhook
advice
reversal
reconciliation
```

---

## 2.4 Provider Isolation

Setiap logical provider memiliki adapter independen.

Perubahan pada Provider A tidak boleh memerlukan restart atau deployment Provider B.

---

## 2.5 Configuration Driven

Sebisa mungkin business behavior tidak di-hardcode.

Configurable:

```text
Fee
Routing
Timeout
Retry
Circuit Breaker
Settlement
Reconciliation
Provider Capability
Product
Response Mapping
```

---

## 2.6 Contract First

Interface antar-system harus memiliki formal contract.

```text
REST        → OpenAPI
gRPC        → Protobuf
SOAP        → WSDL/XSD
ISO8583     → ISO Profile
Async Event → Event Schema
```

Breaking contract harus menghasilkan major API version baru.

---

# 3. System Context

```text
                       ┌──────────────────────┐
                       │ Merchant / Channel   │
                       │ App / POS / B2B      │
                       └──────────┬───────────┘
                                  │
                  REST / SOAP / ISO / TCP
                                  │
                                  ▼
                      ┌────────────────────┐
                      │      RANSYS        │
                      │                    │
                      │ Transaction Core   │
                      │ Ledger             │
                      │ Routing            │
                      │ Adapters           │
                      └──────────┬─────────┘
                                 │
             ┌───────────────────┼──────────────────┐
             ▼                   ▼                  ▼
           Bank                Biller            Supplier
```

---

# 4. Major Components

RANSYS terdiri dari logical component berikut:

```text
API / Protocol Gateway

Transaction Core
Ledger Module
Wallet Module
Idempotency Engine

Routing Engine

Provider Adapter Services

Configuration Service

Reconciliation Engine

Settlement Engine

Backoffice

Audit Service

Async Worker

Monitoring / Observability
```

---

# 5. Transaction Core Boundary

Transaction Core bertanggung jawab atas:

```text
transaction creation
validation
transaction state machine
idempotency
balance reservation
ledger interaction
provider orchestration
result processing
financial finalization
```

Transaction Core **tidak boleh bergantung pada Backoffice, Settlement, atau Reconciliation agar transaksi online dapat berjalan.**

---

# 6. Transaction State Model

State utama bersifat generic.

| State | Description |
|---|---|
| RECEIVED | Request telah diterima RANSYS |
| VALIDATED | Authentication dan business validation berhasil |
| PROCESSING | Sedang diproses / dikirim ke provider |
| PENDING | Provider menyatakan transaksi masih dalam proses |
| IN_DOUBT | Request kemungkinan diproses tetapi hasil final tidak diketahui |
| SUCCESS | Provider memberi hasil sukses yang valid |
| FAILED | Hasil final menyatakan transaksi gagal |
| REVERSAL_PENDING | Reversal sedang diproses |
| REVERSED | Financial effect berhasil dibalik |
| REFUND_PENDING | Refund sedang diproses |
| REFUNDED | Refund berhasil |
| RECON_PENDING | Menunggu finalisasi reconciliation |
| RECON_EXCEPTION | Terdapat mismatch yang membutuhkan investigation |

State tidak digunakan untuk mendeskripsikan alasan detail.

Digunakan field terpisah:

```text
status
reason_code
reason_description
```

Contoh:

```text
status      = IN_DOUBT
reason_code = PROVIDER_READ_TIMEOUT
```

---

# 7. Transaction Identity

Setiap transaksi memiliki:

```text
ransys_transaction_id
client_reference
client_idempotency_key

STAN
RRN

provider_reference
provider_stan
provider_rrn

transaction_fingerprint
original_transaction_id
```

`ransys_transaction_id` merupakan primary global identifier RANSYS.

`client_reference` mandatory untuk financial transaction.

Idempotency window:

```text
24 hours
```

Scope:

```text
client/channel + client_reference
```

---

# 8. Duplicate Transaction

Jika:

```text
client_reference sama
fingerprint sama
```

maka dianggap:

```text
IDEMPOTENT RETRY
```

RANSYS mengembalikan transaksi existing.

Jika:

```text
client_reference sama
fingerprint berbeda
```

maka:

```text
DUPLICATE_REFERENCE_CONFLICT
```

Transaksi baru tidak diproses.

---

# 9. Wallet & Balance Model

RANSYS menggunakan:

```text
PREFUND ONLY
```

Tidak ada credit facility.

Wallet default:

```text
1 Merchant
    │
    └── Main Wallet
```

Optional future:

```text
Product-specific Wallet
```

Tidak diperlukan multi-currency merchant pada baseline awal.

---

# 10. Balance Structure

Wallet memiliki minimal:

```text
ledger_balance
available_balance
reserved_balance
```

Contoh:

```text
Ledger Balance    : 100,000,000
Available Balance : 80,000,000
Reserved Balance  : 20,000,000
```

Untuk transaksi:

```text
Amount = 100,000
Fee    = 2,500
```

RANSYS harus reserve:

```text
102,500
```

yaitu:

```text
Amount + seluruh guaranteed merchant fee
```

---

# 11. Atomic Balance Reservation

Balance check dan reservation harus atomic.

PostgreSQL menjadi financial consistency arbiter.

Concept:

```sql
BEGIN;

SELECT wallet
FOR UPDATE;

CHECK available_balance;

CREATE transaction;
CREATE reservation;
CREATE ledger entry;

COMMIT;
```

Provider tidak boleh dipanggil selama row lock wallet masih dipegang.

---

# 12. Provider Call Boundary

Flow:

```text
DB TRANSACTION
      │
      ├── Create Transaction
      ├── Lock Wallet
      ├── Reserve Balance
      ├── Ledger HOLD
      └── Outbox
      │
    COMMIT
      │
      ▼
Release DB Lock
      │
      ▼
Call Provider
```

Provider result kemudian diproses dalam DB transaction baru.

---

# 13. Ledger Principles

Setiap perubahan balance wajib mempunyai ledger transaction.

Dilarang melakukan:

```sql
UPDATE wallet SET balance = ...
```

tanpa ledger entry.

Invariant:

```text
Opening Balance
+
SUM(Ledger Entries)
=
Current Ledger Balance
```

Ledger bersifat immutable.

Correction dilakukan menggunakan:

```text
compensating entry
```

bukan mengubah entry lama.

---

# 14. Timeout & IN_DOUBT

Jika request sudah mungkin mencapai provider tetapi response tidak diterima:

```text
PROCESSING
    ↓
IN_DOUBT
```

Reserve tetap ditahan.

Tidak boleh:

```text
release balance otomatis
retry purchase ke provider lain
menganggap FAILED
```

Recovery:

```text
Status Check
Callback
Webhook
Advice
Reversal
Reconciliation
```

Maximum hold bukan auto-release.

Yang configurable adalah:

```text
hold_warning_after
hold_critical_after
```

---

# 15. Provider Failover

Jika request belum terkirim dan Provider A diketahui unavailable:

```text
Provider A
LINK_DOWN
    ↓
Provider B
```

boleh dilakukan.

Audit routing wajib menyimpan:

```text
routing_rule_version
initial_selected_provider
actual_provider
failover_count
failover_reason
```

Tidak boleh failover ke Provider B jika Provider A masih:

```text
IN_DOUBT
```

---

# 16. Provider Adapter Architecture

Rule:

```text
1 Logical Provider
=
1 Independently Deployable Adapter
```

Shared functionality menggunakan:

```text
Ransys Adapter SDK
```

Adapter hanya bertanggung jawab atas:

```text
communication
serialization
protocol handling
request mapping
response mapping
provider authentication
```

Adapter tidak boleh:

```text
update wallet
update ledger
memutuskan final financial state
```

---

# 17. Provider Capability

Provider Profile dapat mendefinisikan:

```text
supports_inquiry
supports_payment
supports_purchase
supports_status_check
supports_reversal
supports_refund
supports_advice
supports_callback
supports_balance_check
supports_reconciliation
supports_settlement_file
```

Transaction Core menyesuaikan recovery flow berdasarkan capability.

---

# 18. Provider Timeout Configuration

Configurable hingga level:

```text
Provider
+
Product
+
Transaction Type
```

Parameter:

```text
connect_timeout
read_timeout
max_retry
retry_delay
retry_backoff
status_check_after_timeout
reversal_after_timeout
```

---

# 19. Routing Engine

Baseline routing menggunakan:

```text
Primary Provider
Secondary Provider
Additional Fallback Provider
```

Belum menggunakan:

```text
weighted routing
hierarchical routing
complex least-cost routing
```

Future capability dapat mencakup:

```text
health-based routing
latency-based routing
cost-based routing
success-rate routing
```

---

# 20. Provider Health

Provider state:

```text
HEALTHY
DEGRADED
UNHEALTHY
CIRCUIT_OPEN
MANUAL_DISABLED
```

Operator dapat melakukan manual disable.

Circuit Breaker configurable:

```text
failure_rate
minimum_request_count
measurement_window
open_duration
half_open_probe_count
```

---

# 21. Canonical Response Code

RANSYS menggunakan 4-digit canonical response code.

Namespace:

```text
0xxx Provider / Downstream
1xxx RANSYS Internal
2xxx Validation
3xxx Authentication / Security
4xxx Financial / Ledger
5xxx Routing / Configuration
6xxx Recon / Settlement
7xxx Async / Callback
8xxx Infrastructure
9xxx Reserved / Custom
```

Contoh:

```text
0000 SUCCESS

0068 PROVIDER_TIMEOUT
0091 PROVIDER_LINK_DOWN

1001 RANSYS_INTERNAL_ERROR
1002 RANSYS_PROCESSING_TIMEOUT

2001 INVALID_REQUEST
2003 DUPLICATE_REFERENCE_CONFLICT

3001 INVALID_SIGNATURE
3002 IP_NOT_ALLOWED

4001 INSUFFICIENT_BALANCE

5001 NO_ROUTE_AVAILABLE

6001 RECON_MISMATCH
```

Raw provider code tetap disimpan:

```text
ransys_response_code
provider_response_code
provider_response_message
```

---

# 22. Canonical Transaction Model

Conceptual structure:

```text
CanonicalTransaction
├── Identity
├── TransactionType
├── Product
├── Merchant
├── Channel
├── Money
├── Fee
├── Customer
├── Source
├── Destination
├── References
├── Routing
├── Status
├── Timestamps
└── Metadata
```

`Metadata` hanya untuk provider/product-specific extension.

Field yang mulai digunakan secara umum harus dipromosikan menjadi canonical field.

---

# 23. Currency-aware Money Model

RANSYS tidak boleh menggunakan:

```text
float
double
```

untuk monetary value.

C#:

```text
decimal
```

PostgreSQL:

```text
NUMERIC
```

Money model:

```text
amount
currency_code
currency_scale
currency_definition_version
```

Currency definition harus versioned.

Hal ini memungkinkan:

```text
redenominasi
perubahan decimal scale
currency conversion rule
historical value preservation
```

Historical transaction tidak boleh dikonversi diam-diam.

---

# 24. Timestamp Model

Gunakan timezone-aware timestamp.

Contoh:

```text
2026-09-26T15:30:12.123+07:00
```

Database menggunakan PostgreSQL `timestamptz`.

Minimal timestamp:

```text
received_at
validated_at
provider_sent_at
provider_response_at
financial_posted_at
completed_at
```

---

# 25. External API Model

External endpoint dipisahkan berdasarkan transaction type.

Contoh:

```text
POST /api/v1/inquiries
POST /api/v1/payments
POST /api/v1/transfers
POST /api/v1/refunds
POST /api/v1/reversals
```

Breaking change:

```text
/api/v2/...
```

API v1 tidak boleh mengalami breaking behavior.

---

# 26. Response Contract

Baseline:

```json
{
  "ransysTransactionId": "019...",
  "clientReference": "INV-001",
  "responseCode": "0000",
  "responseMessage": "Success",
  "transactionStatus": "SUCCESS",
  "data": {}
}
```

Timeout example:

```json
{
  "ransysTransactionId": "019...",
  "clientReference": "INV-001",
  "responseCode": "1002",
  "responseMessage": "Transaction response timeout",
  "transactionStatus": "IN_DOUBT"
}
```

`responseCode` dan `transactionStatus` adalah dua konsep berbeda.

---

# 27. Contract-first Engineering

Contract formal:

```text
REST        → OpenAPI
Internal    → Protobuf where applicable
SOAP        → WSDL/XSD
ISO8583     → ISO Message Profile
Events      → Versioned Event Schema
```

Semua contract masuk source control.

Breaking change harus terdeteksi sebelum deployment.

---

# 28. Authentication Framework

Baseline MVP:

```text
SIGNED_API
+
mTLS
```

SIGNED_API menggunakan:

```text
Client ID
Timestamp
Nonce
Body Digest
HMAC / HTTP Signature
```

Anti-replay menggunakan timestamp dan single-use nonce.

Architecture harus memungkinkan tambahan:

```text
OAuth2
FAPI 2.0
private_key_jwt
DPoP
SOAP WS-Security
ISO MAC
```

tanpa mengubah Transaction Core.

---

# 29. Network Security

IP whitelist configurable per client/channel.

Dapat menggunakan:

```text
single public IP
multiple public IP
CIDR
```

IP whitelist adalah defense-in-depth, bukan authentication utama.

---

# 30. Backoffice Authorization

Menggunakan:

```text
RBAC
+
Granular Permission
+
Scope
+
Maker-Checker
```

Contoh permission:

```text
transaction.view
transaction.status_check

routing.view
routing.change

balance.view
balance.topup

refund.create
refund.approve

reversal.create
reversal.approve
```

Maker tidak boleh menjadi checker untuk action yang sama.

---

# 31. Mandatory Maker-Checker

Mandatory untuk:

```text
Manual Top-up
Refund
Manual Reversal
Transaction Limit Change
```

Future extension dapat diterapkan pada operation lain.

---

# 32. Audit Trail

Audit log immutable.

Minimal menyimpan:

```text
actor
action
timestamp
before
after
reason
session
source_ip
maker
checker
```

Financial mutation harus selalu dapat ditelusuri.

---

# 33. Secrets

Initial implementation:

```text
Encrypted Secret Storage in Database
```

Tetapi melalui abstraction:

```text
ISecretProvider
```

Agar kemudian dapat diganti:

```text
DatabaseSecretStore
Vault
Cloud Secret Manager
HSM
```

tanpa mengubah Transaction Core.

---

# 34. Configuration Service & Resync

RANSYS harus mendukung config update tanpa restart.

Lifecycle config:

```text
DRAFT
↓
PENDING_APPROVAL
↓
APPROVED
↓
SCHEDULED
↓
ACTIVE
↓
EXPIRED
```

Config memiliki:

```text
config_version
effective_from
effective_until
created_by
approved_by
activated_at
```

Transaction harus dapat menyimpan config/rule version yang digunakan.

---

# 35. Transaction Database

Default:

```text
PostgreSQL
```

Transaction DB digunakan untuk:

```text
Transaction
Wallet
Ledger
Idempotency
Reservation
Outbox
Transaction Attempt
Financial State
```

Online retention:

```text
±3 weeks
```

---

# 36. Backoffice Database

Backoffice DB terpisah sepenuhnya.

Retention:

```text
minimum 2 years
```

Digunakan untuk:

```text
transaction search
reporting
operations
reconciliation
settlement
historical analysis
```

Backoffice failure tidak boleh mengganggu online transaction.

---

# 37. Transaction Replication

Target data freshness Backoffice:

```text
≤ approximately 5 minutes
```

Bukan menggunakan simple "copy new rows".

Gunakan event/outbox/change model sehingga perubahan status ikut direplikasi.

Contoh:

```text
TIMEOUT
↓
SUCCESS
```

harus meng-update Backoffice record existing.

---

# 38. Transactional Outbox

Transactional Outbox adalah mandatory architecture component.

Financial transaction dan outbox event disimpan dalam DB transaction yang sama.

```text
BEGIN

UPDATE transaction
POST ledger
INSERT outbox_event

COMMIT
```

Ini mencegah kondisi:

```text
DB SUCCESS
but
event lost
```

---

# 39. Brokerless Async Mode

RabbitMQ tidak mandatory.

Default / Lite:

```text
PostgreSQL Outbox
+
.NET Background Workers
```

Workers dapat menggunakan:

```text
FOR UPDATE SKIP LOCKED
```

untuk safe concurrent processing.

Optional enterprise:

```text
PostgreSQL Outbox
      ↓
RabbitMQ
      ↓
Consumers
```

Transaction Core tidak boleh bergantung langsung pada RabbitMQ.

---

# 40. Async Consumer Pattern

Asynchronous consumer harus idempotent.

Gunakan:

```text
event_id
+
Inbox / Deduplication
```

Delivery assumption:

```text
AT LEAST ONCE
```

bukan:

```text
EXACTLY ONCE
```

---

# 41. Reconciliation Architecture

Semua provider reconciliation melalui:

```text
Recon Adapter
```

Input dapat berasal dari:

```text
API
SFTP
CSV
TXT
Excel
ISO
Custom File
```

Matching strategy configurable per provider.

Contoh:

```text
RRN + Amount + Date
```

atau:

```text
Provider Reference
```

---

# 42. Reconciliation Result

Status:

```text
MATCHED
RANSYS_ONLY
PROVIDER_ONLY
AMOUNT_MISMATCH
STATUS_MISMATCH
DUPLICATE_PROVIDER
REFERENCE_MISMATCH
PENDING_INVESTIGATION
RESOLVED
```

---

# 43. Reconciliation Auto Resolution

Auto resolution diperbolehkan hanya untuk deterministic case.

Contoh:

```text
RANSYS = IN_DOUBT
Provider = SUCCESS
Amount = MATCH
Reference = MATCH
```

RANSYS dapat menyelesaikan menjadi:

```text
SUCCESS
```

dengan:

```text
resolution_source = RECONCILIATION
```

Amount/status mismatch yang tidak deterministic masuk:

```text
RECON_EXCEPTION
```

dan tidak boleh auto-adjust financial ledger.

---

# 44. Settlement Engine

Settlement asynchronous.

Tidak masuk synchronous transaction path.

Configurable:

```text
Gross / Net
T+0 / T+1 / T+n
Fee
Commission
Tax
Reserve
Adjustment
Cutoff
Holiday Calendar
```

RANSYS menghitung payable/receivable.

Actual payout dilakukan oleh external payment/banking system.

---

# 45. Settlement Approval

Lifecycle:

```text
GENERATED
↓
REVIEWED
↓
PENDING_APPROVAL
↓
APPROVED
↓
READY_TO_PAY
```

Maker-checker diberlakukan.

---

# 46. Business Date Closing

Setelah business period selesai:

```text
BUSINESS DATE = CLOSED
```

Data historis tidak boleh diedit.

Correction dilakukan melalui:

```text
ADJUSTMENT ENTRY
```

---

# 47. Settlement Cutoff

Cutoff:

```text
configurable
timezone aware
default EOD
holiday-calendar aware
```

Cutoff tidak menghentikan online transaction.

Settlement assignment dilakukan asynchronous.

---

# 48. Raw Request / Response

Raw provider payload disimpan di flat-file/object-based storage terpisah dari application log.

Contoh:

```text
/var/lib/ransys/raw-messages/
```

Metadata DB:

```text
raw_request_reference
raw_response_reference
```

Retention configurable.

Sensitive credential/PIN/CVV tidak boleh ditulis ke raw dump.

---

# 49. Application Logging

Application Log:

```text
Structured JSON
Flat File / stdout
```

Tidak ditulis per-event ke Transaction DB.

Retention configurable.

Baseline:

```text
30–90 days
```

---

# 50. Security Logging

Security Log menggunakan structured flat file / centralized log storage.

Contoh event:

```text
LOGIN_FAILED
INVALID_SIGNATURE
IP_BLOCKED
REPLAY_DETECTED
API_KEY_REVOKED
PERMISSION_CHANGED
```

Retention configurable.

---

# 51. Audit Logging

Audit Log berbeda dari application log.

Disimpan dalam database/dedicated audit store.

Retention minimum baseline:

```text
≥2 years
```

---

# 52. Distributed Tracing

Semua service wajib propagate:

```text
ransys_transaction_id
correlation_id
trace_id
```

Tujuan:

```text
Merchant
↓
API
↓
Core
↓
Routing
↓
Adapter
↓
Provider
```

dapat ditelusuri end-to-end.

---

# 53. Metrics

Technical metrics:

```text
TPS
p50 latency
p95 latency
p99 latency

success rate
failed rate
timeout rate
IN_DOUBT count

provider health
provider latency

DB latency
DB connection pool

CPU
RAM
Disk

outbox backlog
recon backlog
callback backlog
```

Business metrics:

```text
transaction count
transaction value
fee revenue
merchant usage
product usage
provider usage
```

---

# 54. Alerting

Threshold configurable.

Notification pluggable:

```text
Email
Telegram
Slack
Teams
Webhook
SMS
```

Alert escalation belum menjadi baseline.

---

# 55. Availability Profiles

## RANSYS LITE

```text
1 Linux VM

Core
PostgreSQL
Adapters
Workers
Backoffice
```

Maintenance window diperbolehkan.

Tidak memberikan HA guarantee.

---

## RANSYS HA

```text
Load Balancer

Core x N
Adapters x N
Workers x N

PostgreSQL HA

Optional RabbitMQ

Optional DR Site
```

Rolling deployment belum mandatory untuk versi awal.

---

# 56. Disaster Recovery

Default target enterprise:

```text
RTO ≤ 1 hour
```

Transaction/Ledger:

```text
RPO = 0 atau sedekat mungkin dengan 0
```

Backoffice dapat direbuild selama source transaction data tersedia.

Untuk deployment yang membutuhkan near-zero RPO, PostgreSQL replication/WAL recovery harus tersedia.

---

# 57. Backup

Backup execution menjadi responsibility infrastructure customer.

RANSYS hanya expose informasi apabila tersedia:

```text
database_health
replication_status
replica_lag
last_backup_status
recovery_health
```

Periodic restore testing direkomendasikan.

---

# 58. Database Migration

Database migration harus:

```text
backward compatible sebisa mungkin
```

Pattern:

```text
EXPAND
↓
Deploy
↓
Migrate
↓
CONTRACT
```

Hindari destructive schema changes secara langsung.

---

# 59. Performance Target

Business load:

```text
300 TPS
```

Engineering target:

```text
Normal Operating Target : 300 TPS
Design Capacity         : 1,000 TPS
Stress Target           : ≥1,500 TPS
```

Angka final harus divalidasi melalui benchmark pada reference hardware.

---

# 60. Internal Processing SLA

Tidak termasuk provider latency.

Target awal:

```text
p50 < 50 ms
p95 < 150 ms
p99 < 300 ms
```

---

# 61. Backpressure

Provider dapat memiliki:

```text
max_concurrent_requests
max_queue_depth
provider_rate_limit
```

Jika capacity habis:

```text
controlled rejection
```

lebih baik daripada uncontrolled system collapse.

---

# 62. Load Isolation

Future/gradual implementation:

```text
provider isolation
product isolation
adapter isolation
```

Overload Provider A tidak boleh menjatuhkan Provider B.

---

# 63. Graceful Degradation

Noncritical component:

```text
Backoffice DOWN
Recon DOWN
Settlement DOWN
Notification DOWN
Monitoring DOWN
```

tidak boleh menghentikan transaksi.

Financial dependencies:

```text
Transaction DB DOWN
Ledger unavailable
```

harus menyebabkan:

```text
FAIL CLOSED
```

---

# 64. Performance Test

Tidak hanya happy path.

Contoh test mix:

```text
Payment
Inquiry
Duplicate
Insufficient Balance
Provider Timeout
Provider Failure
IN_DOUBT
Status Check
Reversal
Concurrent Debit Same Wallet
Duplicate Callback
```

Race-condition wallet wajib diuji.

---

# 65. Failure Injection

QA harus mampu mensimulasikan:

```text
Provider Timeout
Connection Reset
Adapter Crash
Transaction Core Restart
Database Restart
High Latency
Duplicate Callback
Out-of-order Response
Disk Pressure
Disk Full
```

Failure test adalah bagian penting quality validation RANSYS.

---

# 66. Soak Test

8–24 hour continuous load test:

```text
NICE TO HAVE
```

untuk major release.

Tujuan:

```text
memory leak
connection leak
pool exhaustion
file handle leak
GC issue
log growth
```

---

# 67. Technology Baseline

```text
Backend
C# / Modern .NET LTS

OS
Linux

REST
ASP.NET Core / Kestrel

TCP / ISO8583
.NET Socket
System.IO.Pipelines where appropriate

Database
PostgreSQL

Cache
Redis
NOT financial source of truth

Async
PostgreSQL Transactional Outbox
.NET Background Workers

Optional Broker
RabbitMQ

Deployment
Container-ready
Cloud Agnostic
On-premise compatible
```

---

# 68. MVP Scope

Baseline MVP harus memiliki:

```text
REST / JSON
ISO8583
TCP Adapter capability
SOAP capability

Transaction Core
Ledger
Wallet / Prefund
Reservation

Idempotency
Fingerprint

Primary / Secondary Routing
Circuit Breaker
Manual Provider Disable

Provider Adapter Framework

Config / Resync

Backoffice

Reconciliation

Settlement Calculation

Maker-Checker

Audit Trail

SIGNED_API
mTLS

IP Whitelist

PostgreSQL Outbox

Metrics
Logging
Tracing
Alerting

RANSYS Lite Deployment
```

---

# 69. Future Capability

Belum mandatory:

```text
Credit Facility

Multi-currency Merchant Wallet

Complex Hierarchical Routing
Weighted Routing
Least Cost Routing
Advanced Smart Routing

Fraud Engine
Risk Engine

Transaction Limit Engine
Realtime Auto Blocking

Full FAPI 2.0
OAuth2
DPoP

HSM
Vault Integration

Advanced PII Tokenization

RabbitMQ HA

Automatic Alert Escalation

Active-Active DR

Mandatory Zero-downtime Deployment

Product-level Isolation Advanced Controls
```

---

# 70. Core Architecture Summary

```text
                         MERCHANT / CHANNEL
                                │
              REST / SOAP / ISO8583 / TCP
                                │
                                ▼
                     ┌─────────────────────┐
                     │ Protocol/API Layer  │
                     └──────────┬──────────┘
                                │
                                ▼
                   ┌─────────────────────────┐
                   │    TRANSACTION CORE     │
                   │                         │
                   │ State Machine           │
                   │ Idempotency             │
                   │ Financial Orchestration │
                   └────────┬───────┬────────┘
                            │       │
                   ┌────────▼──┐ ┌──▼────────────┐
                   │  LEDGER   │ │ ROUTING       │
                   │ + WALLET  │ │ ENGINE        │
                   └────────┬──┘ └──────┬────────┘
                            │           │
                      PostgreSQL        │
                            │           ▼
                            │     Provider Adapter
                            │           │
                            │     ┌─────┼──────┐
                            │     ▼     ▼      ▼
                            │    Bank Biller Supplier
                            │
                            ▼
                     Outbox Events
                            │
                   ┌────────┼─────────────┐
                   ▼        ▼             ▼
              Backoffice   Recon      Settlement
```

---

# 71. Fundamental RANSYS Invariants

RANSYS development tidak boleh melanggar prinsip berikut:

**01.** Financial transaction tidak boleh diproses tanpa durable transaction record.

**02.** Balance tidak boleh berubah tanpa ledger entry.

**03.** Wallet balance tidak boleh negatif.

**04.** Check balance + reserve harus atomic.

**05.** Provider timeout tidak otomatis berarti failed.

**06.** `IN_DOUBT` tidak boleh langsung difailover ke provider lain.

**07.** Historical financial record tidak boleh diedit.

**08.** Manual financial correction menggunakan compensating entry.

**09.** Maker tidak boleh menjadi checker transaksi yang sama.

**10.** Adapter tidak boleh mempunyai akses langsung untuk mengubah ledger.

**11.** Redis bukan source of truth financial.

**12.** RabbitMQ bukan financial source of truth.

**13.** Transaction Core tidak bergantung pada Backoffice, Recon, atau Settlement availability.

**14.** Financial dependency failure harus fail closed.

**15.** Breaking external API change membutuhkan API version baru.

**16.** Application log bukan transaction history.

**17.** Raw provider message bukan application log.

**18.** Semua financial mutation harus auditable.

---

# 72. Architecture Baseline Status

Dengan specification ini, foundation architecture RANSYS v1.0 dianggap:

```text
BUSINESS MODEL        LOCKED
TRANSACTION MODEL     LOCKED
LEDGER MODEL          LOCKED
ROUTING BASELINE      LOCKED
PROVIDER MODEL        LOCKED
RECON MODEL           LOCKED
SETTLEMENT MODEL      LOCKED
SECURITY BASELINE     LOCKED
API PRINCIPLES        LOCKED
DATABASE PRINCIPLES   LOCKED
ASYNC MODEL           LOCKED
DEPLOYMENT BASELINE   LOCKED
PERFORMANCE TARGET    LOCKED
```

Tahap desain berikutnya adalah detailed engineering design.