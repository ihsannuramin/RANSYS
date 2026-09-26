# RANSYS Product Requirements Document (PRD) v1.0

**Product Name:** RANSYS  
**Product Type:** Universal Transaction Switching & Processing Platform  
**Document Type:** Product Requirements Document (PRD) + Technical Product Requirements  
**Primary Backend:** C# / Modern .NET LTS  
**Primary Database:** PostgreSQL  
**Target Operating System:** Linux  
**Deployment Model:** Per-company isolated deployment; on-premise or cloud  
**Status:** Foundation Baseline  
**Source:** Consolidation of product and architecture brainstorming Round 1–12

---

## 1. Executive Summary

RANSYS adalah universal switching dan transaction processing platform yang dirancang untuk menerima transaksi dari berbagai channel/merchant dan meneruskannya ke host/provider yang sesuai, seperti bank, acquirer, issuer, biller, supplier, principle, core system, atau payment service lainnya.

RANSYS bukan hanya router. RANSYS harus menjadi **full transaction processing platform** yang memiliki transaction state machine, prefund wallet, ledger, balance reservation, routing, provider adapter, reconciliation, settlement calculation, audit trail, maker-checker, operational monitoring, dan recovery mechanism untuk transaksi yang hasil akhirnya tidak langsung diketahui.

Target penggunaan RANSYS adalah perusahaan yang membutuhkan switching/payment processing engine yang dapat diinstal secara isolated per perusahaan dan dikonfigurasi sesuai business model masing-masing. RANSYS tidak dirancang sebagai SaaS multi-tenant pada baseline awal. White-label berarti satu codebase dan architecture yang sama dapat di-deploy terpisah untuk perusahaan berbeda, dengan konfigurasi, provider integration, branding, routing, fee, product, settlement, dan operational policy yang berbeda.

RANSYS harus dapat berjalan mulai dari deployment ringan pada satu Linux VM sampai deployment HA dengan beberapa application node, PostgreSQL replication, optional message broker, load balancer, dan optional disaster recovery site.

Target beban awal adalah **300 TPS normal**, dengan engineering design capacity **1.000 TPS** dan stress target **1.500 TPS atau lebih** pada reference environment yang akan ditentukan kemudian.

Prinsip terpenting RANSYS adalah:

1. **Financial consistency first.**
2. **Fail closed untuk financial processing.**
3. **Timeout bukan berarti transaksi gagal.**
4. **Tidak boleh ada perubahan saldo tanpa ledger entry.**
5. **Tidak boleh ada negative balance pada model prefund.**
6. **Provider adapter tidak boleh mengubah ledger atau financial truth secara langsung.**
7. **Transaksi historis dan ledger tidak boleh diedit; correction harus melalui compensating entry.**
8. **Backoffice, reconciliation, settlement, dan notification tidak boleh menjadi dependency synchronous transaksi online.**
9. **Configuration harus dapat di-resync tanpa restart core system.**
10. **External API harus contract-first dan versioned.**

---

# 2. Product Vision

## 2.1 Vision Statement

RANSYS menjadi switching dan transaction processing engine generik yang dapat digunakan lintas domain pembayaran dan layanan transaksi, tanpa mengikat core system ke satu jenis provider, satu jenis protocol, satu product category, atau satu deployment model.

RANSYS harus memungkinkan flow:

```text
Merchant / Client / POS / App / B2B Channel
                    |
                    v
                  RANSYS
                    |
        +-----------+------------+
        |           |            |
        v           v            v
      Bank        Biller      Supplier
        |           |            |
        +---- Principle / Core --+
```

RANSYS harus tetap mampu menangani variasi integrasi seperti REST/JSON, SOAP/XML, ISO 8583, dan custom TCP socket tanpa menjadikan Transaction Core bergantung pada detail protocol tertentu.

---

# 3. Problem Statement

Perusahaan payment, aggregator, merchant network, biller platform, dan perusahaan dengan banyak provider sering menghadapi masalah berikut:

- Integrasi provider dibuat langsung di business application sehingga logic provider bercampur dengan core transaction logic.
- Routing, fee, timeout, retry, dan recovery sering di-hardcode.
- Timeout provider sering disalahartikan sebagai transaksi gagal.
- Duplicate request dapat menghasilkan duplicate debit atau duplicate purchase.
- Saldo merchant dan pencatatan transaksi tidak selalu berada dalam consistency boundary yang sama.
- Provider failure dapat berdampak luas ke seluruh sistem.
- Reconciliation dilakukan manual atau tidak memiliki normalized engine.
- Settlement logic bercampur dengan online transaction processing.
- Perubahan fee/routing membutuhkan restart dan berpotensi menimbulkan downtime.
- Audit financial correction tidak cukup kuat.
- Sistem sulit dijalankan baik di perusahaan kecil maupun enterprise karena infrastructure dependency terlalu berat.
- API berubah tanpa contract governance sehingga berisiko memutus integrasi merchant lama.
- Observability tidak memiliki correlation yang cukup untuk investigasi transaksi end-to-end.

RANSYS dibangun untuk menyelesaikan problem tersebut melalui transaction core generik, ledger kuat, adapter isolation, configuration-driven behavior, reconciliation dan settlement terpisah, serta operational safety yang memadai.

---

# 4. Goals

## 4.1 Business Goals

- Mendukung berbagai domain transaksi yang sifatnya payment gateway/switching.
- Dapat digunakan oleh perusahaan berbeda melalui per-company deployment.
- Memungkinkan penambahan provider baru tanpa perubahan besar pada Transaction Core.
- Mengurangi risiko financial loss akibat duplicate debit, timeout ambiguity, negative balance, atau manual adjustment yang tidak terkontrol.
- Memudahkan operational investigation.
- Memudahkan perubahan business model seperti fee, routing, settlement, dan provider policy tanpa downtime core.
- Menyediakan foundation yang cukup fleksibel untuk integrasi dengan bank, biller, supplier, principle, atau external core system.

## 4.2 Technical Goals

- Normal load 300 TPS.
- Design capacity 1.000 TPS.
- Stress target minimal 1.500 TPS pada reference hardware.
- Internal processing SLA awal, tidak termasuk provider latency:
  - p50 < 50 ms
  - p95 < 150 ms
  - p99 < 300 ms
- Strong consistency untuk transaction, wallet reservation, dan ledger.
- Mendukung single-VM deployment.
- Cloud agnostic dan on-premise compatible.
- Semua service berjalan pada Linux.
- Independent provider adapter deployment.
- Transaction Core tidak bergantung pada Backoffice, Reconciliation, Settlement, Notification, atau optional broker untuk terus memproses transaksi.
- Database utama PostgreSQL.
- C#/.NET sebagai primary backend technology.

---

# 5. Non-Goals / Out of Scope Baseline

Berikut belum menjadi mandatory baseline dan masuk future capability:

- Credit facility / postpaid merchant balance.
- Multi-currency wallet untuk merchant.
- Advanced hierarchical routing.
- Weighted routing.
- Least-cost routing.
- Dynamic smart routing sebagai default.
- Fraud Engine internal.
- Risk Engine internal.
- Advanced Transaction Limit Engine.
- Realtime automatic blocking karena anomaly.
- Mandatory OAuth2/FAPI 2.0 pada semua client.
- Mandatory HSM.
- Mandatory Vault/external secrets manager.
- Full PII tokenization framework.
- Mandatory RabbitMQ.
- Mandatory Kafka/Redpanda.
- Active-active disaster recovery.
- Mandatory rolling/zero-downtime application deployment.
- Advanced alert escalation chain.
- Transfer balance antar merchant.
- Merchant self-service withdrawal.
- Credit line / overdraft.
- Automated payout engine sebagai bagian baseline Settlement.
- Weighted load distribution ke provider.

---

# 6. Target Users and Actors

## 6.1 External Actors

### Merchant / Client
Mengirim inquiry, payment, purchase, transfer, refund request, reversal request, atau jenis transaksi lain melalui API/protocol yang disepakati.

### POS / Application / Channel
Sumber transaksi yang dapat mewakili merchant atau customer-facing channel.

### Bank / Acquirer / Issuer
Downstream financial institution yang menerima atau memproses transaksi.

### Biller / Supplier / Principle
Downstream non-bank atau payment provider yang melayani product tertentu.

---

## 6.2 Internal Users

### Operations
Mencari transaksi, memantau provider, menjalankan status check, melakukan tindakan recovery sesuai permission, serta mengelola routing operasional.

### Finance
Memantau wallet, top-up, reconciliation, settlement, dan financial exception.

### Finance Supervisor / Checker
Memberikan approval atas action finansial tertentu.

### Business
Mengelola fee, product configuration, settlement rule, dan perubahan bisnis yang memiliki effective date.

### Customer Support
Melihat transaksi dan status tertentu sesuai scope permission.

### Auditor
Melihat audit trail, transaksi, settlement, reconciliation, dan historical change tanpa memiliki hak modifikasi.

### System Administrator / Security Administrator
Mengelola user, role, security configuration, certificate, IP whitelist, dan system-level configuration sesuai least privilege.

---

# 7. Deployment Model

RANSYS tidak menggunakan multi-tenant SaaS baseline.

Model:

```text
Company A
└── RANSYS Deployment A
    ├── Core
    ├── DB
    ├── Provider Adapters
    ├── Backoffice
    └── Configuration

Company B
└── RANSYS Deployment B
    ├── Core
    ├── DB
    ├── Provider Adapters
    ├── Backoffice
    └── Configuration
```

Setiap perusahaan mendapatkan isolated deployment.

## 7.1 RANSYS Lite

Target perusahaan kecil:

```text
1 Linux VM

├── Transaction Core
├── PostgreSQL
├── Provider Adapters
├── Background Workers
├── Configuration
└── Backoffice
```

Karakteristik:
- Tidak menjanjikan HA.
- Maintenance window diperbolehkan.
- Tidak membutuhkan RabbitMQ.
- Async processing menggunakan PostgreSQL Outbox + .NET workers.

## 7.2 RANSYS HA

Target perusahaan yang membutuhkan redundancy:

```text
Load Balancer
├── Transaction Core x N
├── Provider Adapter x N / sesuai kebutuhan
├── Worker x N
├── PostgreSQL HA
├── Optional RabbitMQ
└── Optional DR
```

Rolling deployment belum mandatory pada baseline awal, tetapi desain database migration harus sebisa mungkin backward compatible.

---

# 8. Supported Protocols

Baseline protocol:

- REST / JSON
- SOAP / XML
- ISO 8583
- TCP Socket / Proprietary TCP protocol

Core tidak boleh memahami detail protocol secara langsung.

Semua protocol harus diterjemahkan ke Canonical Transaction Model.

```text
REST Adapter -------+
SOAP Adapter -------+
ISO8583 Adapter ----+--> Canonical Transaction --> Transaction Core
TCP Adapter --------+
```

---

# 9. Supported Transaction Families

RANSYS dirancang untuk mendukung:

- Inquiry
- Payment
- Purchase
- Transfer
- Refund
- Reversal
- Void
- Advice
- Balance Inquiry
- Status Check
- Settlement-related processing
- Future generic transaction types

Walaupun platform target mendukung seluruh keluarga transaksi, implementasi product/provider tertentu dapat dilakukan bertahap.

---

# 10. Transaction Identity Model

Setiap business transaction memiliki satu primary global identifier:

```text
ransys_transaction_id
```

Disarankan menggunakan identifier yang globally unique dan time-sortable.

Field identity/correlation lainnya:

```text
client_reference
client_idempotency_key
stan
rrn
provider_reference
provider_stan
provider_rrn
transaction_fingerprint
original_transaction_id
```

`client_reference` mandatory untuk seluruh financial transaction.

STAN dan RRN tidak menjadi global primary key RANSYS.

---

# 11. Idempotency and Duplicate Protection

## 11.1 Idempotency Window

Baseline:

```text
24 hours
```

Scope:

```text
client/channel + client_reference
```

## 11.2 Duplicate Request: Same Payload

Jika client reference sama dan fingerprint sama:

```text
IDEMPOTENT RETRY
```

RANSYS tidak membuat business transaction baru dan dapat mengembalikan current/existing result.

## 11.3 Duplicate Request: Different Payload

Jika client reference sama tetapi fingerprint berbeda:

```text
DUPLICATE_REFERENCE_CONFLICT
```

Request tidak diproses sebagai transaksi baru.

## 11.4 Transaction Fingerprint

Fingerprint digunakan sebagai secondary duplicate detector.

Fingerprint dapat dibentuk dari canonicalized field seperti:

```text
merchant/client
transaction_type
product
destination
amount
currency
client_reference
```

Field volatile seperti timestamp transport atau signature tidak boleh membuat fingerprint berubah secara salah.

---

# 12. Generic Transaction State Machine

RANSYS menggunakan generic primary state dengan reason code terpisah.

| State | Description |
|---|---|
| `RECEIVED` | Request sudah diterima dan transaction ID dibuat |
| `VALIDATED` | Authentication, format, merchant, product, duplicate check, dan validation lain telah lolos |
| `PROCESSING` | Transaksi sedang diproses atau sedang berkomunikasi dengan provider |
| `PENDING` | Provider secara eksplisit menyatakan transaksi masih diproses |
| `IN_DOUBT` | Request mungkin sudah diterima provider, tetapi hasil final belum diketahui |
| `SUCCESS` | Hasil provider sukses dan valid |
| `FAILED` | Hasil transaksi final dan gagal |
| `REVERSAL_PENDING` | Reversal sedang diproses |
| `REVERSED` | Financial effect berhasil dibalik |
| `REFUND_PENDING` | Refund sedang diproses |
| `REFUNDED` | Refund berhasil |
| `RECON_PENDING` | Menunggu resolution melalui reconciliation |
| `RECON_EXCEPTION` | Ada mismatch yang memerlukan investigation/resolution |

State tidak dipakai untuk menyimpan seluruh alasan.

Gunakan:

```text
status
reason_code
reason_description
```

Contoh:

```text
status = IN_DOUBT
reason_code = PROVIDER_READ_TIMEOUT
```

---

# 13. Transport Result vs Transaction Result

RANSYS wajib membedakan hasil komunikasi dengan hasil finansial.

Contoh:

```json
{
  "responseCode": "1002",
  "responseMessage": "Transaction response timeout",
  "transactionStatus": "IN_DOUBT"
}
```

Artinya:
- communication/transport mengalami timeout;
- financial result belum diketahui;
- merchant tidak boleh menganggap transaksi sudah gagal.

Prinsip:

```text
TIMEOUT != FAILED
```

---

# 14. Wallet Model

Baseline menggunakan:

```text
PREFUND ONLY
```

Tidak ada credit facility.

Default:

```text
1 Merchant
└── 1 Main Wallet
```

Nice-to-have:

```text
Merchant
├── Main Wallet
└── Product-specific Wallet
```

Multi-currency merchant wallet belum dibutuhkan.

---

# 15. Balance Model

Minimal field:

```text
ledger_balance
available_balance
reserved_balance
```

Contoh:

```text
ledger_balance    = 100,000,000
available_balance = 80,000,000
reserved_balance  = 20,000,000
```

## 15.1 Reservation Rule

Jika:

```text
amount = 100,000
merchant fee = 2,500
```

RANSYS harus reserve:

```text
102,500
```

yaitu transaction amount + seluruh guaranteed fee yang dibebankan ke merchant.

---

# 16. Atomic Balance Check and Reserve

Balance check + reserve harus dilakukan dalam atomic database transaction.

Concept:

```sql
BEGIN;

SELECT wallet
FROM merchant_wallet
WHERE wallet_id = ?
FOR UPDATE;

CHECK available_balance;

CREATE transaction;
CREATE reservation;
CREATE ledger entry;

COMMIT;
```

Tujuan:
- mencegah double spending;
- mencegah race condition;
- memastikan dua transaksi bersamaan tidak membaca available balance yang sama.

Jika saldo hanya cukup untuk satu transaksi, transaksi lain harus langsung menerima insufficient balance.

---

# 17. Provider Call Boundary

Provider call tidak boleh dilakukan sambil menahan wallet row lock.

Yang benar:

```text
BEGIN DB TRANSACTION
  create transaction
  lock wallet
  reserve balance
  create ledger hold
  create outbox
COMMIT

release lock

call provider
```

Setelah response provider diterima, gunakan database transaction baru untuk finalization.

---

# 18. Ledger Model

Semua balance mutation wajib memiliki ledger transaction.

Tidak boleh ada direct balance edit tanpa ledger.

Invariant:

```text
Opening Balance + SUM(Ledger Entries) = Current Ledger Balance
```

Ledger historical entry bersifat immutable.

Correction dilakukan melalui compensating entry.

Contoh flow:

### Reserve

```text
Available -102,500
Reserved +102,500
```

### Success

```text
Reserved -102,500
Ledger balance finalized
```

### Failed

```text
Reserved -102,500
Available +102,500
```

### IN_DOUBT

```text
Reserved tetap ditahan
```

---

# 19. Balance Mutation Visibility

Setiap mutasi saldo harus dapat dijelaskan di Backoffice.

Contoh:

```text
TX123 PAYMENT RESERVE
Amount     -100,000
Fee          -2,500

Available   897,500
Reserved    102,500
```

Jika transaksi timeout:

```text
TX123 PROVIDER TIMEOUT
Reserved remains 102,500
Reason: TRANSACTION_IN_DOUBT
```

Operational/Finance user harus dapat mengetahui alasan setiap hold, debit, release, refund, reversal, top-up, atau adjustment.

---

# 20. Timeout and Recovery

Jika request sudah mungkin sampai ke provider tetapi response hilang:

```text
PROCESSING -> IN_DOUBT
```

RANSYS tidak boleh:
- langsung menganggap FAILED;
- release reserve secara otomatis;
- langsung mengirim ulang purchase ke provider lain;
- melakukan duplicate financial action.

Recovery method dapat meliputi:

- Status Check / Transaction Inquiry
- Callback
- Webhook
- Advice
- Reversal
- Reconciliation

Hold boleh bertahan selama transaksi belum memiliki final truth.

Gunakan alert threshold, bukan auto-release:

```text
hold_warning_after
hold_critical_after
```

---

# 21. Provider Failover

Jika Provider A diketahui unavailable **sebelum request financial terkirim**, RANSYS boleh failover ke Provider B.

Audit harus menyimpan:

```text
routing_rule_version
initial_selected_provider
actual_provider
failover_count
failover_reason
```

Jika Provider A sudah menerima atau mungkin menerima request dan hasil menjadi `IN_DOUBT`, RANSYS tidak boleh failover purchase yang sama ke Provider B.

---

# 22. Routing Baseline

Baseline routing menggunakan deterministic priority:

```text
Product
├── Primary Provider
├── Secondary Provider
└── Optional Fallback Provider
```

Belum dibutuhkan:
- hierarchical merchant/channel routing;
- weighted routing;
- least-cost routing.

Future optional:
- health-based routing;
- latency-based routing;
- success-rate-based routing;
- cost-based routing;
- balance-aware routing.

---

# 23. Provider Health and Circuit Breaker

Provider states:

```text
HEALTHY
DEGRADED
UNHEALTHY
CIRCUIT_OPEN
MANUAL_DISABLED
```

Operator dapat manual disable provider, misalnya untuk maintenance.

Circuit breaker parameter configurable:

```text
failure_rate
minimum_request_count
measurement_window
open_duration
half_open_probe_count
```

Merchant dapat menerima normalized response seperti `LINK_DOWN` tanpa harus mengetahui detail nama provider internal.

---

# 24. Provider Adapter Architecture

Rule utama:

```text
1 logical provider = 1 independently deployable adapter
```

Adapter menggunakan shared RANSYS Adapter SDK/Common Library untuk menghindari code duplication.

Adapter responsibilities:
- protocol communication;
- serialization/deserialization;
- provider authentication;
- provider-specific request mapping;
- provider-specific response mapping;
- provider-specific cryptographic/message formatting bila diperlukan;
- health communication.

Adapter **tidak boleh**:
- update wallet;
- update ledger;
- menentukan financial truth secara langsung;
- mengubah transaction state di DB tanpa melalui Transaction Core.

Adapter menghasilkan normalized `ProviderResult`.

---

# 25. Provider Capability Matrix

Per provider dapat didefinisikan:

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

Transaction Core dan Recovery Engine menyesuaikan flow berdasarkan capability.

---

# 26. Provider Timeout and Retry Configuration

Configurable pada level:

```text
Provider + Product + Transaction Type
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

Financial retry hanya dilakukan jika aman secara idempotency dan transaction state.

---

# 27. Canonical Response Code

RANSYS menggunakan 4-digit canonical response code.

Namespace baseline:

```text
0xxx = Provider / Downstream Result
1xxx = RANSYS Internal
2xxx = Request / Validation
3xxx = Authentication / Security
4xxx = Financial / Balance / Ledger
5xxx = Routing / Configuration
6xxx = Reconciliation / Settlement
7xxx = Async / Callback
8xxx = Infrastructure
9xxx = Reserved / Custom
```

Contoh:

```text
0000 SUCCESS
0068 PROVIDER_TIMEOUT
0091 PROVIDER_LINK_DOWN

1001 RANSYS_INTERNAL_ERROR
1002 RANSYS_PROCESSING_TIMEOUT

2001 INVALID_REQUEST
2002 INVALID_PRODUCT
2003 DUPLICATE_REFERENCE_CONFLICT

3001 INVALID_SIGNATURE
3002 IP_NOT_ALLOWED
3003 REPLAY_DETECTED

4001 INSUFFICIENT_MERCHANT_BALANCE

5001 NO_ROUTE_AVAILABLE
5002 PROVIDER_MANUALLY_DISABLED

6001 RECON_MISMATCH
```

Raw provider response tetap disimpan:

```text
ransys_response_code
provider_response_code
provider_response_message
```

Provider code tidak boleh diasumsikan memiliki arti universal; mapping dilakukan oleh adapter masing-masing.

---

# 28. Canonical Transaction Model

Internal model bersifat structured, bukan flat object besar.

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

Metadata hanya untuk provider/product-specific extension.

Jika sebuah field mulai dipakai lintas provider/product secara umum, field tersebut harus dipromosikan menjadi canonical field.

---

# 29. Currency-aware Money Model

RANSYS tidak boleh menggunakan floating point untuk monetary value.

C#:

```text
decimal
```

PostgreSQL:

```text
NUMERIC
```

Money object:

```text
amount
currency_code
currency_scale
currency_definition_version
```

Currency definition harus versioned.

Tujuan:
- tidak hardcode bahwa IDR selamanya scale 0;
- siap terhadap kemungkinan redenominasi;
- historical transaction mempertahankan nominal dan currency definition yang berlaku saat transaksi terjadi;
- conversion dilakukan eksplisit, bukan overwrite historical amount.

---

# 30. Timestamp Model

Gunakan timezone-aware timestamp.

Database menggunakan `timestamptz`.

Minimal timestamp:

```text
received_at
validated_at
provider_sent_at
provider_response_at
financial_posted_at
completed_at
```

Tidak menggunakan satu field `transaction_date` untuk semua event.

---

# 31. External API Design

External API menggunakan endpoint per transaction type.

Contoh:

```text
POST /api/v1/inquiries
POST /api/v1/payments
POST /api/v1/transfers
POST /api/v1/refunds
POST /api/v1/reversals
```

Tujuan:
- contract lebih jelas;
- validation lebih spesifik;
- documentation lebih mudah;
- permission/rate limit lebih mudah dipisahkan.

Semua external request tetap diterjemahkan ke Canonical Transaction internal.

---

# 32. API Versioning

Breaking change tidak boleh merusak v1.

Rule:

```text
Breaking Change => /api/v2/...
```

API contract yang sudah published immutable terhadap breaking behavior.

Optional additive field diperbolehkan hanya jika backward compatible.

---

# 33. Response Contract

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

Timeout:

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

# 34. Contract-first Engineering

Semua interface lintas service boundary harus memiliki machine-readable atau formal contract.

```text
REST        -> OpenAPI
gRPC        -> Protobuf
SOAP        -> WSDL/XSD
ISO8583     -> ISO Message Profile
Async Event -> Versioned Event Schema
```

Contract masuk source control dan melalui review.

CI/CD diharapkan dapat mendeteksi breaking change yang tidak disengaja.

---

# 35. Authentication Framework

Baseline awal:

```text
SIGNED_API (HMAC / HTTP Signature)
+
mTLS
```

SIGNED_API minimal membawa:

```text
Client ID
Timestamp
Nonce
Body Digest
Signature
```

Anti-replay menggunakan:
- timestamp validity window;
- single-use nonce;
- signature validation.

Auth Policy Engine harus didesain extensible agar di masa depan dapat menambahkan:

- OAuth2 Client Credentials
- FAPI 2.0 security profile
- private_key_jwt
- DPoP
- SOAP WS-Security
- ISO MAC
- certificate-bound token

tanpa mengubah Transaction Core.

---

# 36. IP Whitelist

IP whitelist configurable per merchant/channel.

Support:
- single public IP;
- multiple public IP;
- CIDR bila deployment membutuhkan.

IP whitelist adalah network defense-in-depth, bukan authentication utama.

---

# 37. Backoffice Access Control

Backoffice menggunakan:

```text
RBAC
+
Granular Permissions
+
Scope
+
Maker-Checker
```

Contoh permission:

```text
transaction.view
transaction.search
transaction.status_check
transaction.manual_reversal

routing.view
routing.change
routing.approve

balance.view
balance.topup
balance.adjustment.create
balance.adjustment.approve

refund.create
refund.approve

settlement.view
settlement.generate
settlement.approve

reconciliation.view
reconciliation.resolve

audit.view
```

Role merupakan kumpulan permission.

Scope dapat membatasi permission ke merchant, product, atau kelompok tertentu.

---

# 38. Mandatory Maker-Checker

Baseline mandatory untuk:

- Manual top-up
- Refund
- Manual reversal
- Transaction limit changes

Rule:

```text
maker_user_id != checker_user_id
```

Walaupun satu user memiliki create dan approve permission, user tidak boleh approve action yang dibuat sendiri.

---

# 39. Manual Intervention

Tidak boleh tersedia tombol yang sekadar mengubah status:

```text
Force SUCCESS
Force FAILED
```

Manual action harus merupakan real business/financial action:

- Status Check
- Requery
- Manual Reversal
- Refund
- Release Hold
- Recon Resolution
- Ledger Adjustment

Jika financial, action harus menghasilkan compensating ledger entry dan audit trail.

---

# 40. Audit Trail

Audit trail bersifat immutable.

Minimal record:

```text
actor
action
timestamp
before
after
reason
session_id
source_ip
maker
checker
reference
```

Audit digunakan untuk:
- financial investigation;
- operational investigation;
- compliance;
- dispute;
- internal control.

---

# 41. Secrets Management

Baseline awal:
- credentials provider dapat disimpan di database;
- tidak boleh plaintext;
- encrypted at rest;
- encryption key tidak ideal jika disimpan bersama ciphertext dalam boundary yang sama.

Gunakan abstraction:

```text
ISecretProvider
```

Initial implementation:

```text
DatabaseSecretStore
```

Future:
- HashiCorp Vault;
- cloud secret manager;
- HSM;
- enterprise secret infrastructure.

---

# 42. Configuration Service and Resync

Perubahan konfigurasi financial/routing tidak boleh membutuhkan restart core.

Config lifecycle:

```text
DRAFT
-> PENDING_APPROVAL
-> APPROVED
-> SCHEDULED
-> ACTIVE
-> EXPIRED
```

Field:

```text
config_version
effective_from
effective_until
created_by
approved_by
activated_at
```

Contoh:
- fee baru berlaku tanggal tertentu;
- routing baru disiapkan sebelum cutover;
- product configuration diaktifkan tanpa restart.

Transaction dapat menyimpan `config_version` atau `rule_version` yang digunakan untuk audit.

---

# 43. Configuration Cache

Transaction Core tidak boleh melakukan remote configuration lookup untuk setiap transaksi.

Config harus di-resync dan disimpan di local cache/runtime memory yang sesuai.

Tujuan:
- menghindari network dependency;
- mengurangi latency;
- menjaga transaction path tetap ringan.

---

# 44. Transaction Database

Default:

```text
PostgreSQL
```

Digunakan untuk:

- Transaction
- Transaction Attempt
- Wallet
- Ledger
- Reservation
- Idempotency
- Transaction History
- Outbox
- Financial state
- Provider routing references

Online retention:

```text
maksimal sekitar 3 minggu
```

Purging hanya boleh dilakukan setelah downstream/backoffice data dipastikan sudah tersalin dan terverifikasi.

---

# 45. Backoffice Database

Backoffice DB terpisah dari Transaction DB.

Retention baseline:

```text
minimal 2 tahun
```

Digunakan untuk:
- transaction search;
- reporting;
- operations;
- reconciliation;
- settlement;
- historical analytics;
- business metrics;
- financial investigation.

Backoffice failure tidak boleh menghentikan transaksi online.

---

# 46. Transaction Replication to Backoffice

Target freshness:

```text
sekitar <= 5 menit
```

Tidak menggunakan simple copy hanya berdasarkan row baru.

Perubahan status harus ikut tereplikasi.

Contoh:

```text
TIMEOUT -> SUCCESS
```

harus meng-update Backoffice record existing.

Approach:
- Transactional Outbox;
- versioned event/update;
- upsert di Backoffice;
- optional CDC/broker untuk deployment lebih besar.

---

# 47. Data Protection in Backoffice

Keputusan product baseline:
- Backoffice storage dapat menyimpan field sesuai kebutuhan sistem;
- advanced masking/tokenization bukan core design requirement baseline;
- presentation masking dapat diatur pada development stage;
- sensitive field tetap sebaiknya diklasifikasikan sejak awal.

Untuk future hardening, RANSYS harus memungkinkan:
- server-side masking;
- tokenization;
- controlled reveal;
- limited retention;
- stronger PII governance.

Raw credential, PIN, CVV, atau secret tidak boleh ditulis sembarangan ke logs/raw files.

---

# 48. Transactional Outbox

Transactional Outbox mandatory.

Financial state dan outbox event dibuat dalam database transaction yang sama.

```text
BEGIN

UPDATE transaction
POST ledger
INSERT outbox_event

COMMIT
```

Tujuan:
- tidak ada kondisi DB sukses tetapi event hilang;
- transaction truth tetap berada di PostgreSQL;
- async consumer dapat retry.

---

# 49. Brokerless Async Processing

RabbitMQ bukan dependency mandatory.

Default / Lite:

```text
PostgreSQL Outbox
+
.NET Background Workers
```

Worker dapat menggunakan safe concurrency pattern seperti `FOR UPDATE SKIP LOCKED`.

Optional Enterprise:

```text
PostgreSQL Outbox
-> RabbitMQ
-> Consumers
```

Transaction Core tidak boleh bergantung langsung pada RabbitMQ.

Jika RabbitMQ down, financial transaction tetap dapat berjalan selama PostgreSQL/Ledger sehat dan outbox dapat menerima event.

---

# 50. Async Delivery Semantics

Asumsi:

```text
AT LEAST ONCE
```

Consumer harus idempotent.

Gunakan:
- event_id;
- inbox/deduplication;
- consumer transaction;
- retry;
- dead-letter/dead status.

Jangan mengandalkan klaim exactly-once untuk external side effect.

---

# 51. Callback / Webhook Delivery

Callback delivery asynchronous.

Harus mendukung:
- retry;
- backoff;
- deduplication;
- delivery status;
- audit;
- dead state/DLQ equivalent;
- configurable timeout.

Merchant callback failure tidak boleh mengubah financial truth transaksi.

---

# 52. Reconciliation Architecture

Reconciliation terpisah dan asynchronous.

Semua provider menggunakan Recon Adapter.

Input dapat berasal dari:
- API;
- SFTP;
- CSV;
- TXT/fixed width;
- Excel;
- ISO message;
- file dump;
- custom source.

Online transaction tidak bergantung pada Recon Engine availability.

---

# 53. Reconciliation Matching Strategy

Configurable per provider.

Possible key:
- provider_reference;
- RRN;
- STAN;
- merchant_reference;
- customer_identifier;
- amount;
- transaction_date;
- product.

Contoh:

```text
Provider A = RRN + Amount + Date
Provider B = Provider Reference
```

---

# 54. Reconciliation Result

Supported normalized result:

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

Tujuan utama adalah memudahkan operational investigation dan resolution.

---

# 55. Reconciliation Auto Resolution

Auto resolution hanya untuk deterministic rule.

Contoh:

```text
RANSYS = IN_DOUBT
Provider = SUCCESS
Reference = MATCH
Amount = MATCH
```

RANSYS boleh melakukan resolution:

```text
IN_DOUBT -> SUCCESS
```

dan mencatat:

```text
resolution_source = RECONCILIATION
```

Jika amount mismatch atau condition tidak deterministic:
- masuk `RECON_EXCEPTION`;
- tidak auto-adjust ledger;
- Ops/Finance investigation;
- maker-checker untuk financial adjustment.

---

# 56. Reconciliation Frequency

Configurable:

- realtime;
- every N minutes;
- hourly;
- daily;
- T+1;
- manual;
- berdasarkan file availability provider.

---

# 57. Settlement Engine

Settlement asynchronous dan terpisah dari realtime transaction.

Settlement dapat mendukung:
- gross;
- net;
- T+0;
- T+1;
- T+n;
- fee;
- commission;
- tax;
- reserve;
- adjustment;
- holiday calendar;
- configurable cut-off;
- product/provider-specific rule.

RANSYS menghitung payable/receivable.

Actual payout dilakukan oleh sistem eksternal/bank dan bukan scope baseline Settlement Engine.

---

# 58. Settlement Cut-off

Cut-off configurable:
- default EOD;
- timezone-aware;
- per provider/product bila diperlukan;
- holiday calendar aware.

Cut-off tidak menghentikan realtime transaction.

Contoh:

```text
22:59 transaction -> settlement period D
23:01 transaction -> settlement period D+1
```

Assignment dilakukan asynchronous berdasarkan settlement rule.

---

# 59. Settlement Approval

Lifecycle:

```text
GENERATED
-> REVIEWED
-> PENDING_APPROVAL
-> APPROVED
-> READY_TO_PAY
```

Maker-checker diterapkan.

---

# 60. Business Date Closing

Setelah periode ditutup:

```text
BUSINESS DATE = CLOSED
```

Historical transaction/ledger tidak diedit.

Correction setelah closing menggunakan:

```text
ADJUSTMENT ENTRY
```

Tujuan:
- menjaga auditability;
- menjaga settlement integrity;
- mencegah retroactive silent edits.

---

# 61. Raw Request / Response Storage

Raw request/response dibutuhkan terutama untuk troubleshooting protocol/provider.

Storage:
- flat file atau object-based storage;
- terpisah dari application log;
- retention configurable;
- access lebih terbatas.

Metadata DB menyimpan reference:

```text
raw_request_reference
raw_response_reference
```

Correlation:

```text
ransys_transaction_id
transaction_attempt_id
correlation_id
direction
timestamp
```

Raw dump tidak boleh menyimpan secret, CVV, PIN, PIN block, atau credential dalam plaintext.

---

# 62. Application Logging

Application log:
- structured JSON;
- flat file atau stdout;
- tidak ditulis per-event ke PostgreSQL transaction database.

Contoh field:

```text
timestamp
level
service
event
ransys_transaction_id
correlation_id
trace_id
provider
latency_ms
```

Retention configurable.

Baseline recommendation:
- 30–90 hari;
- DEBUG/TRACE lebih pendek.

---

# 63. Security Logging

Security log:
- flat file / structured centralized log;
- terpisah dari business transaction history.

Contoh event:

```text
LOGIN_SUCCESS
LOGIN_FAILED
INVALID_SIGNATURE
IP_BLOCKED
REPLAY_DETECTED
API_KEY_REVOKED
CERTIFICATE_CHANGED
PERMISSION_CHANGED
USER_DISABLED
```

Retention configurable per deployment.

---

# 64. Audit Logging

Audit log berbeda dari application log dan security log.

Disimpan di database/dedicated audit store.

Retention baseline:

```text
>= 2 tahun
```

Immutable dari perspektif user/admin normal.

---

# 65. Distributed Tracing

Semua service wajib propagate:

```text
ransys_transaction_id
correlation_id
trace_id
```

Tujuan:
- end-to-end investigation;
- cross-service latency analysis;
- debugging provider integration;
- tracing transaction recovery.

---

# 66. Monitoring Metrics

## 66.1 Technical Metrics

- TPS
- p50 latency
- p95 latency
- p99 latency
- success rate
- failed rate
- timeout rate
- IN_DOUBT count
- provider health
- provider latency
- provider error rate
- DB latency
- DB connection pool usage
- CPU
- RAM
- disk
- outbox backlog
- recon backlog
- callback backlog

## 66.2 Business Metrics

- transaction count
- transaction value
- fee/revenue
- product usage
- merchant usage
- provider usage

Business metrics dibutuhkan realtime, bukan hanya report historical.

---

# 67. Alerting

Threshold configurable.

Notification channel pluggable:
- Email
- Telegram
- Slack
- Microsoft Teams
- Webhook
- SMS
- channel lain via integration

Alert escalation chain belum mandatory baseline.

---

# 68. Provider Balance

Jika provider menyediakan endpoint balance atau balance pada message response, RANSYS dapat menyimpan actual provider balance.

Jika tidak tersedia, RANSYS boleh menghitung estimated provider balance secara internal.

Harus dibedakan jelas:

```text
balance_type = ACTUAL
source = PROVIDER_API
```

versus:

```text
balance_type = ESTIMATED
source = RANSYS_INTERNAL
```

Estimated balance tidak boleh disajikan seolah saldo resmi provider.

---

# 69. Top-up

Merchant hanya diperbolehkan top-up balance.

Tidak ada:
- merchant self-service withdrawal;
- transfer balance antar merchant.

Manual top-up harus memiliki:
- reference;
- audit trail;
- maker-checker;
- ledger entry.

---

# 70. Refund / Reversal / Adjustment

Refund, manual reversal, dan adjustment harus:
- menghasilkan transaction/action record;
- memiliki audit trail;
- jika financial, menghasilkan ledger entry;
- melalui maker-checker sesuai baseline policy;
- tidak mengubah historical entry diam-diam.

---

# 71. Availability Profiles

Availability ditentukan oleh deployment profile.

RANSYS tidak memaksa customer kecil memiliki HA.

### Lite
- 1 VM;
- maintenance window allowed;
- no HA guarantee.

### HA
- multiple application node;
- PostgreSQL HA/replication;
- load balancer;
- optional broker;
- optional DR.

---

# 72. RTO and RPO

Default enterprise target:

```text
RTO <= 1 hour
```

Transaction/Ledger:

```text
RPO = 0 atau sedekat mungkin 0
```

Backoffice dapat dibangun ulang dari source yang masih tersedia.

Near-zero RPO membutuhkan PostgreSQL replication/WAL strategy yang sesuai deployment profile.

---

# 73. Backup Responsibility

Backup execution adalah tanggung jawab infrastructure customer.

RANSYS expose status bila tersedia:

```text
database_health
replication_status
replica_lag
last_backup_status
recovery_health
```

Periodic restore test direkomendasikan.

---

# 74. Database Migration

Database migration harus zero-downtime sebisa mungkin.

Gunakan pattern:

```text
EXPAND
-> DEPLOY
-> MIGRATE
-> CONTRACT
```

Hindari immediate destructive change seperti drop/rename column yang masih digunakan release lama.

---

# 75. Performance Targets

Baseline:

```text
Normal Operating Target = 300 TPS
Design Capacity         = 1,000 TPS
Stress Target           = >= 1,500 TPS
```

Angka final wajib divalidasi melalui benchmark nyata.

Internal processing SLA tidak termasuk provider latency:

```text
p50 < 50 ms
p95 < 150 ms
p99 < 300 ms
```

---

# 76. Burst and Backpressure

RANSYS harus mampu menghadapi burst dengan bounded concurrency.

Configurable:
- max concurrent request;
- provider rate limit;
- max queue depth;
- rejection/busy threshold.

Jika capacity provider penuh, controlled rejection lebih baik daripada membuat seluruh sistem collapse.

---

# 77. Load Isolation

Dapat diterapkan bertahap pada:
- provider;
- product;
- adapter.

Tujuan:
- overload PLN tidak boleh menjatuhkan transfer;
- Provider A failure tidak boleh membuat Provider B ikut melambat;
- adapter bermasalah harus memiliki blast radius terbatas.

---

# 78. Graceful Degradation

Jika subsystem noncritical down:

```text
Backoffice
Monitoring
Notification
Recon
Settlement
```

online transaction harus tetap jalan.

Jika critical financial dependency down:

```text
Transaction DB
Ledger
```

financial transaction harus:

```text
FAIL CLOSED
```

Tidak ada mode "teruskan transaksi dulu, catat nanti".

---

# 79. Performance Certification

Performance test tidak boleh hanya happy path.

Test mix harus realistis dan mencakup:
- payment;
- inquiry;
- duplicate;
- insufficient balance;
- provider timeout;
- provider failure;
- IN_DOUBT;
- status check;
- reversal;
- concurrent debit wallet sama;
- duplicate callback;
- recovery.

---

# 80. Failure Injection

QA harus dapat mensimulasikan:

- provider timeout;
- connection reset;
- adapter crash;
- Transaction Core restart;
- database restart;
- high latency;
- duplicate callback;
- out-of-order response;
- disk pressure;
- disk full.

Resilience/failure testing adalah bagian penting quality validation.

---

# 81. Soak Test

8–24 hour soak test adalah nice-to-have, terutama untuk major release.

Tujuan:
- memory leak;
- connection leak;
- DB pool exhaustion;
- file handle leak;
- GC issue;
- log growth;
- resource degradation.

---

# 82. Technology Baseline

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

Async Default
PostgreSQL Transactional Outbox
.NET Background Workers

Optional Broker
RabbitMQ

Deployment
Container-ready
Cloud agnostic
On-premise compatible
```

Redis hanya untuk cache/non-authoritative state seperti:
- config cache;
- routing cache;
- nonce;
- rate limiting;
- provider health cache.

Redis tidak boleh menjadi source of truth saldo.

---

# 83. C# Suitability Requirements

C#/.NET dipilih karena:
- mampu memenuhi target 300 TPS+;
- async I/O matang;
- concurrency tinggi;
- cocok untuk REST, SOAP, TCP, ISO8583;
- berjalan native di Linux;
- cocok untuk microservice-capable architecture;
- skill utama tim saat ini ada di C#;
- operational complexity relatif manageable.

Untuk ISO8583/TCP, RANSYS harus mendukung persistent connection, partial packet, length-prefix framing, reconnect, timeout, dan provider-specific ISO profile.

---

# 84. ISO8583 Adapter Requirements

ISO8583 tidak boleh diasumsikan seragam.

Provider profile dapat berbeda pada:
- ISO 8583 version;
- MTI;
- field definition;
- bitmap;
- ASCII/BCD/binary encoding;
- TPDU;
- custom header;
- length header;
- DE48/DE62/DE63;
- DE64/DE128 MAC;
- field transformation.

Gunakan RANSYS ISO engine / profile concept.

---

# 85. Resync Requirements

Perubahan berikut harus dapat aktif tanpa restart core:
- fee;
- provider routing;
- timeout;
- retry;
- provider capability;
- settlement rule;
- product mapping;
- response mapping;
- configurable business parameter.

Resync harus version-aware.

Jika config invalid, active config lama tetap digunakan.

---

# 86. Product Configuration

Product sedapat mungkin configuration-driven.

Contoh:
- PLN
- Pulsa
- Transfer
- VA
- Payment
- Voucher
- Game
- Insurance

Product-specific hardcoded logic diminimalkan di Transaction Core.

---

# 87. Simple Mapping vs Complex Adapter Logic

Simple field mapping boleh configuration-driven.

Contoh:

```text
Ransys.customer_id -> provider.customerNumber
```

Complex mapping harus menggunakan code:

- MAC generation;
- custom encryption;
- checksum;
- XML digital signature;
- proprietary nested message;
- complex transformation.

Tidak membangun unrestricted scripting engine pada production baseline.

---

# 88. Response Mapping Governance

Adapter harus mapping provider result menjadi canonical response.

Contoh:

```text
Provider RC23 -> RANSYS 0051
```

Raw code tetap disimpan.

Mapping configuration harus versioned dan auditable.

---

# 89. Provider Manual Disable

Operations dapat disable provider secara manual.

Contoh:

```text
Provider A
Status = MANUAL_DISABLED
Reason = Maintenance
Effective = 23:00-01:00
```

Manual disable harus:
- auditable;
- visible di monitoring;
- memengaruhi routing;
- dapat memiliki effective period.

---

# 90. Settlement Finality vs Transaction Finality

RANSYS harus memisahkan concept:

```text
processing_status
financial_status
settlement_status
reconciliation_status
```

Contoh:

```text
processing_status     = SUCCESS
financial_status      = POSTED
settlement_status     = PENDING
reconciliation_status = UNMATCHED
```

Kemudian:

```text
settlement_status     = SETTLED
reconciliation_status = MATCHED
```

Online `SUCCESS` tidak berarti settlement sudah selesai.

---

# 91. Manual Financial Safety Principles

RANSYS tidak boleh memiliki direct "edit balance".

Semua manual financial change harus:
- memiliki reason;
- memiliki reference;
- melalui maker-checker bila required;
- menghasilkan ledger entry;
- menghasilkan audit trail;
- dapat direkonstruksi.

---

# 92. Data Lifecycle

Transaction DB:
- hot/operational;
- sekitar 3 minggu.

Backoffice:
- minimal 2 tahun.

Suggested lifecycle:

```text
HOT
-> ARCHIVED/COPIED
-> VERIFIED
-> PURGE_ELIGIBLE
-> PURGED
```

Source transaction tidak boleh dipurge sebelum downstream copy/consistency check selesai.

---

# 93. API Security and Replay Protection

SIGNED_API harus memisahkan:
- authentication;
- integrity;
- anti-replay;
- business idempotency.

Nonce:
- security anti-replay.

Idempotency-Key/client reference:
- business duplicate protection.

Keduanya tidak boleh dianggap sama.

---

# 94. Internal Service Boundaries

Logical components:

```text
Protocol/API Gateway
Transaction Core
Ledger/Wallet
Routing Engine
Provider Adapters
Configuration Service
Async Worker
Reconciliation Engine
Settlement Engine
Backoffice
Audit
Observability
```

Transaction Core + Ledger berada dalam strong consistency boundary.

Provider Adapter, Recon, Settlement, Backoffice dapat deploy independen.

---

# 95. Service Scaling

Setiap component harus dapat dibuat independently scalable sesuai kebutuhan, tetapi Lite profile tetap boleh menjalankan beberapa logical component pada satu VM.

Architecture bersifat:
- microservice-capable;
- tidak memaksa microservice deployment pada semua customer.

---

# 96. Acceptance Criteria — Financial Integrity

RANSYS dianggap memenuhi financial integrity baseline jika:

1. Dua concurrent debit pada wallet yang sama tidak dapat menghasilkan negative balance.
2. Duplicate request dengan same reference + same fingerprint tidak menghasilkan financial action kedua.
3. Same reference + different fingerprint menghasilkan `DUPLICATE_REFERENCE_CONFLICT`.
4. Provider timeout menghasilkan `IN_DOUBT`, bukan automatic FAILED.
5. `IN_DOUBT` tidak otomatis failover ke provider lain.
6. Setiap balance mutation memiliki ledger record.
7. Manual adjustment memiliki maker/checker dan audit.
8. Historical ledger tidak diedit.
9. Wallet reserve dipertahankan hingga final truth ditemukan atau authorized compensating action dibuat.
10. Transaction DB/Ledger failure menyebabkan financial request fail closed.

---

# 97. Acceptance Criteria — Provider Integration

1. Provider adapter dapat di-deploy independen.
2. Update Adapter A tidak membutuhkan restart Adapter B.
3. Transaction Core tidak memiliki provider-specific protocol logic.
4. Provider raw response code tersimpan.
5. Canonical response code selalu tersedia untuk client.
6. Provider timeout/retry configurable per Provider + Product + Transaction Type.
7. Manual provider disable memengaruhi routing tanpa restart Core.
8. Circuit breaker bekerja sesuai threshold configuration.
9. Provider capabilities dapat dikonfigurasi.
10. Simple mapping dapat dikonfigurasi; complex transformation berada di code adapter.

---

# 98. Acceptance Criteria — Operations

1. User dapat mencari transaksi minimal berdasarkan:
   - ransys_transaction_id;
   - STAN;
   - RRN;
   - merchant reference;
   - provider reference;
   - customer identifier;
   - amount;
   - date/time;
   - status;
   - product.
2. Mutasi wallet menjelaskan business reason.
3. Transaction routing history terlihat.
4. Provider health terlihat.
5. IN_DOUBT dapat diinvestigasi.
6. Recon mismatch memiliki classification.
7. Settlement batch memiliki approval lifecycle.
8. Audit trail tidak dapat diedit oleh user normal.
9. Config financial dapat memiliki effective date.
10. Resync tidak membutuhkan restart core.

---

# 99. Acceptance Criteria — Availability and Resilience

1. Lite dapat berjalan pada satu Linux VM.
2. Backoffice down tidak menghentikan online transaction.
3. Recon down tidak menghentikan online transaction.
4. Settlement down tidak menghentikan online transaction.
5. Notification down tidak menghentikan online transaction.
6. PostgreSQL/Ledger down membuat financial request gagal secara controlled.
7. Outbox event tetap tersimpan jika async transport unavailable.
8. Optional RabbitMQ tidak menjadi financial dependency.
9. Failure injection scenario dapat direcover sesuai state machine.
10. HA deployment dapat menggunakan PostgreSQL replication.

---

# 100. Acceptance Criteria — API and Contract

1. External endpoint dipisah per transaction type.
2. `client_reference` mandatory untuk financial transaction.
3. API versioning tersedia.
4. Breaking change tidak dilakukan pada version existing.
5. OpenAPI menjadi source-of-truth external REST contract.
6. Response selalu memiliki:
   - ransysTransactionId;
   - clientReference;
   - responseCode;
   - responseMessage;
   - transactionStatus.
7. Amount tidak menggunakan float/double.
8. Timestamp timezone-aware.
9. Metadata hanya untuk extension.
10. Raw request/response retention configurable.

---

# 101. Key Risks

## 101.1 Over-engineering
Risiko: terlalu banyak capability dibangun sebelum kebutuhan nyata.

Mitigasi:
- baseline deterministic routing;
- tanpa weighted routing;
- tanpa credit facility;
- broker optional;
- limit/risk engine future;
- multi-currency future.

## 101.2 Provider Ambiguity
Risiko: timeout menghasilkan duplicate debit bila langsung retry/failover.

Mitigasi:
- IN_DOUBT;
- status check;
- reversal;
- reconciliation;
- no failover while ambiguous.

## 101.3 Wallet Contention
Risiko: high concurrency pada wallet yang sama menyebabkan lock contention.

Mitigasi:
- DB transaction singkat;
- provider call di luar lock;
- performance test same-wallet concurrency.

## 101.4 Configuration Error
Risiko: fee/routing config salah berdampak massal.

Mitigasi:
- versioning;
- maker-checker untuk sensitive config bila diperlukan;
- effective date;
- validation;
- rollback/resync.

## 101.5 Backoffice Drift
Risiko: source dan Backoffice tidak sinkron.

Mitigasi:
- outbox;
- versioned update;
- upsert;
- reconciliation of copy;
- rebuild capability.

## 101.6 Raw Payload Data Exposure
Risiko: raw file mengandung data sensitif.

Mitigasi:
- access control;
- retention pendek;
- secret field exclusion;
- future masking/tokenization.

---

# 102. Future Roadmap Candidates

Potential future modules:

- OAuth2/FAPI 2.0 full profile
- private_key_jwt
- DPoP
- HSM integration
- Vault integration
- Fraud/Risk Engine
- Transaction Limit Engine
- Multi-currency Wallet
- Credit Facility
- Advanced merchant/product scope
- Smart Routing
- Weighted Routing
- Least Cost Routing
- Health-based auto-route
- Active-active multi-site
- Automated payout initiation
- Advanced PII vault/tokenization
- Mandatory rolling deployment
- Alert escalation
- Kafka/NATS/RabbitMQ enterprise event infrastructure
- Advanced real-time analytics

---

# 103. Round-by-Round Decision Log

## Round 1 — Business and System Boundary

### Business Domain
RANSYS ditujukan untuk seluruh domain yang bersifat payment gateway/switching dan dapat dipasang sebagai white-label isolated deployment per perusahaan.

### Flow
```text
Merchant B2C / Channel B2B
-> RANSYS
-> Host Bank / Principle / Biller / Supplier
```

### Connected Parties
```text
Client / Merchant / POS / App
-> RANSYS
-> Acquirer / Issuer / Provider / Core System
```

### Transaction Coverage
RANSYS ditujukan untuk mendukung seluruh keluarga transaksi:
payment, inquiry, purchase, transfer, refund, reversal, void, advice, balance inquiry, status check, settlement, dan lainnya.

### Processing Scope
Diputuskan bahwa RANSYS adalah **full transaction processing platform**, bukan pure router.

### Routing
Fitur routing harus tersedia secara fleksibel, tetapi implementasi awal kemudian disederhanakan menjadi priority routing untuk menghindari over-engineering.

### Performance
Normal target 300 TPS.

### Availability
Sistem diharapkan practically always available, tetapi deployment profile kemudian menentukan actual HA capability.

### Core Description
RANSYS adalah switching system yang menerima transaksi dari merchant/channel dan meneruskannya ke service provider yang dipilih.

---

## Round 2 — Transaction Model, Protocol, Failure Handling

### Protocol
Support:
- REST/JSON
- SOAP/XML
- ISO8583
- TCP socket

### Global ID
Setuju menggunakan `ransys_transaction_id`.

### Timeout
Timeout tidak dianggap otomatis gagal. RANSYS harus memiliki `IN_DOUBT` dan recovery mechanism.

### Failure Strategy
Retry, reversal, status check, dan recovery configurable sesuai provider/product/transaction.

### Duplicate
STAN/unique reference digunakan tetapi RANSYS juga memiliki fingerprint sendiri.

### Balance
RANSYS memiliki pencatatan saldo internal.

### Settlement
RANSYS harus mampu menangani berbagai model settlement industri.

### Multi-tenancy
Direvisi: tidak perlu multi-tenant SaaS. Satu deployment per perusahaan.

### Routing
Advanced routing capability boleh tersedia sebagai future, tetapi baseline disederhanakan.

### Final Truth
Final truth dapat ditentukan melalui sync response, advice, status check, callback/webhook, reversal, dan reconciliation.

---

## Round 3 — Wallet, Ledger, Financial Model

### Funding Model
Prefund only.

### Wallet
Default satu wallet per merchant; optional product wallet.

### Credit
Tidak ada credit facility.

### Fee
Fee dihitung sebelum transaksi dikirim dan ikut di-reserve.

### Provider Balance
Jika provider memiliki endpoint balance, gunakan actual. Jika tidak, simpan estimated internal.

### Reserve
Hold/reserve wajib untuk mencegah negative balance.

### Concurrent Transaction
Jika saldo hanya cukup untuk satu transaksi, transaksi kedua harus ditolak.

### Merchant Balance Action
Merchant hanya top-up.
Tidak ada withdrawal self-service.
Tidak ada merchant-to-merchant balance transfer.

### Maker-Checker
Wajib untuk sensitive financial action.

### Deployment
Harus support on-premise dan cloud.

---

## Round 4 — Availability, Database, Deployment

### Small Deployment
Harus dapat berjalan pada satu VM.

### Cloud
Cloud agnostic selama Linux.

### DR
Beberapa menit downtime masih acceptable untuk customer kecil tanpa DR.

### Circuit Breaker
Wajib tersedia.

### Provider Health
Metric health realtime optional/configurable.

### Resync
Fee/routing/config dapat berubah tanpa restart Core.

### Effective Configuration
Financial configuration dapat disiapkan terlebih dahulu dengan effective date.

### Search
Minimal berdasarkan transaction ID, STAN, RRN, merchant/provider reference, customer identifier, amount, date/time, status, product.

### Database Split
Transaction DB retention sekitar 3 minggu.
Backoffice DB minimal 2 tahun.
Data dipindahkan sekitar setiap 5 menit melalui event/change-based approach, bukan hanya copy row baru.

### Service Deployment
Component dapat scale/deploy independen.

---

## Round 5 — Security and Operational Safety

### Authentication
Baseline:
- SIGNED_API (HMAC/HTTP Signature)
- mTLS

Auth Policy Engine extensible ke:
- OAuth2
- FAPI 2.0
- private_key_jwt
- DPoP
- SOAP WS-Security
- ISO MAC

### IP Whitelist
Bisa satu atau beberapa public IP/CIDR.

### Backoffice Permission
RBAC + Granular Permissions + Scope + Maker-Checker.

### Maker-Checker Mandatory
- Manual top-up
- Refund
- Manual reversal
- Transaction limit change

### Secrets
Initial encrypted database secret storage, tetapi abstraction harus future-ready untuk Vault/HSM.

### PII
Advanced masking bukan baseline product requirement; presentation handling dapat ditentukan saat development. Sensitive classification tetap disarankan.

### Limit Engine
Future.

### Auto Blocking
Future.

### Audit
Immutable audit trail.

### Fraud/Risk
Future external hook/module.

---

## Round 6 — Service Boundaries and Technology Direction

### Transaction Core + Ledger
Financial consistency lebih penting; berada dalam strong consistency boundary.

### Database
PostgreSQL default.

### Messaging
Awalnya dibandingkan RabbitMQ/Kafka/Redpanda/NATS. Keputusan akhir: RabbitMQ optional, bukan dependency wajib.

### Redis
Boleh digunakan sebagai cache, bukan source of truth financial.

### Provider Adapter
Satu logical provider = satu independently deployable adapter.

### Product
Configuration-driven.

### Backoffice
Terpisah penuh dari realtime Core.

### Reconciliation
Asynchronous dan independen.

### API
Versioned.

### Language
C#/.NET sebagai primary backend karena sesuai expertise tim dan kebutuhan performance/Linux/network/microservices.

---

## Brokerless Refinement

RANSYS harus dapat berjalan tanpa RabbitMQ.

Default:

```text
PostgreSQL Transactional Outbox
+
.NET Background Worker
```

Optional:

```text
Outbox -> RabbitMQ -> Consumer
```

Outbox tetap mandatory.

---

## Round 7 — Transaction Core Internal Design

### State
Generic state untuk seluruh transaction type dengan description dan reason code terpisah.

### Reservation
Create transaction + balance reserve + ledger hold harus atomic.

### Fee Reserve
Reserve amount + guaranteed merchant fee.

### Idempotency
Window 1 hari.

### Conflict
Same reference + different payload -> `DUPLICATE_REFERENCE_CONFLICT`.

### Result
Transport result dipisahkan dari transaction result.

### Provider Retry
Configurable per Provider + Product + Transaction Type.

### Failover Audit
Initial selected provider dan actual provider wajib disimpan.

### IN_DOUBT Hold
Reserve tetap ditahan hingga final truth.

### Manual Intervention
Tidak ada force status; gunakan compensating action.

---

## Round 8 — Provider Adapter & Routing Engine

### Adapter Deployment
1 logical provider = 1 adapter deployment, common SDK/shared library digunakan.

### Canonical Model
Generic + extensible metadata.

### Response Code
Canonical 4-digit namespace.
Raw provider response tetap disimpan.

### Capability Matrix
Configurable per provider.

### Timeout
Configurable per provider/product/transaction type.

### Routing
Hierarchical routing belum perlu.

### Weighted Routing
Belum perlu.

### Provider Manual Disable
Wajib tersedia.

### Mapping
Simple mapping configuration-driven.
Complex mapping di code.

### Adapter Contract
Adapter hanya komunikasi + mapping.
Tidak boleh mengubah ledger/balance/business status.

---

## Round 9 — Reconciliation & Settlement

### Recon Adapter
Support semua model source melalui Recon Adapter.

### Matching
Configurable per provider.

### Result
Normalized result untuk investigation.

### Auto Resolution
Hanya deterministic rule.

### Financial Mismatch
Tidak auto-adjust ledger.

### Frequency
Configurable.

### Cutoff
Configurable, default EOD, timezone dan holiday calendar aware.

### Payout
Terpisah dari RANSYS.

### Settlement Approval
Wajib approval.

### Closing
Business date closing + adjustment entry.

---

## Round 10 — HA, Observability, Operations, DR

### Availability
Deployment profile menentukan availability.

### Deployment Upgrade
Rolling deployment belum mandatory; maintenance window masih boleh.

### Migration
Zero-downtime sebisa mungkin.

### RTO
1 jam.

### RPO
Transaction/Ledger 0 atau sedekat mungkin 0.
Backoffice dapat direbuild.

### Monitoring
Technical + business metrics realtime.

### Alerting
Configurable threshold + pluggable channel.
No escalation baseline.

### Tracing
Semua service propagate:
- ransys_transaction_id
- correlation_id
- trace_id

### Log
Application & security log flat file.
Retention configurable.

### Backup
RANSYS expose status jika tersedia; backup tetap tanggung jawab infra customer.

---

## Round 11 — API Contract & Canonical Data

### External API
Endpoint dipisah per transaction type.

### Client Reference
Mandatory.

### Canonical Structure
Structured canonical object + metadata.

### Money
Currency-aware, no float/double, versioned currency definition, redenomination-ready.

### Time
Multiple event timestamps.

### Metadata Governance
Hanya extension.

### Raw Payload
Flat file, separate retention.

### Response
Standard contract.

### Backward Compatibility
Breaking change membutuhkan API version baru.

### Contract-first
OpenAPI dan formal interface contract menjadi source-of-truth.

---

## Round 12 — Performance, Capacity, Resilience

### Capacity
- 300 TPS normal
- 1.000 TPS design
- 1.500 TPS+ stress

### Internal SLA
- p50 < 50 ms
- p95 < 150 ms
- p99 < 300 ms

### Burst
Bounded concurrency/backpressure.

### Provider Backpressure
Configurable.

### Load Isolation
Bertahap.

### Circuit Breaker
Configurable threshold.

### Fail Closed
Mandatory untuk financial dependency failure.

### Performance Certification
Realistic mixed scenario.

### Soak Test
Nice-to-have major release.

### Failure Injection
Mandatory important resilience testing area.

---

# 104. Final Product Principles

RANSYS harus selalu mempertahankan prinsip berikut:

1. **Financial correctness lebih penting dari convenience.**
2. **Provider timeout tidak boleh disederhanakan menjadi failure.**
3. **Transaction identity harus konsisten end-to-end.**
4. **Duplicate protection harus terjadi sebelum financial effect kedua.**
5. **Wallet reserve harus atomic.**
6. **Ledger adalah catatan financial yang immutable.**
7. **Manual correction harus meninggalkan jejak.**
8. **Provider-specific logic berhenti di adapter boundary.**
9. **Backoffice tidak menentukan financial truth.**
10. **Reconciliation adalah mekanisme untuk menemukan mismatch dan final resolution, bukan alasan menghentikan online processing.**
11. **Settlement adalah asynchronous accounting process.**
12. **Configuration harus versioned dan auditable.**
13. **RANSYS harus bisa hidup sederhana pada satu VM tetapi tidak menutup jalan menuju HA.**
14. **Message broker tidak boleh menjadi requirement untuk kebenaran finansial.**
15. **API contract adalah bagian dari produk, bukan dokumentasi tambahan.**
16. **Observability wajib menghubungkan seluruh journey transaksi.**
17. **Financial component failure harus fail closed.**
18. **Future flexibility tidak boleh menjadi alasan over-engineering MVP.**

---

# 105. Recommended Next Engineering Artifacts

Setelah PRD ini, engineering design sebaiknya diturunkan menjadi:

1. C4 System Context Diagram
2. C4 Container Diagram
3. C4 Component Diagram
4. Database ERD
5. Ledger ERD
6. Payment Success Sequence Diagram
7. Timeout -> IN_DOUBT Sequence Diagram
8. Status Check Recovery Sequence Diagram
9. Reversal Sequence Diagram
10. Duplicate Request Sequence Diagram
11. Reconciliation Sequence Diagram
12. Settlement Batch Sequence Diagram
13. API OpenAPI v1 draft
14. Provider Adapter Contract
15. Canonical Transaction Schema
16. Canonical Response Code Catalog
17. Transaction State Transition Matrix
18. Ledger Posting Rule Matrix
19. Configuration Schema
20. Deployment Topology Lite
21. Deployment Topology HA
22. Performance Test Plan
23. Failure Injection Test Plan
24. Security Threat Model
25. Operational Runbook

---

# 106. Document Status

Dengan PRD ini, foundation RANSYS dianggap cukup matang untuk masuk ke detailed engineering design.

Status keputusan:

```text
Business Scope              LOCKED
Transaction Model           LOCKED
Wallet Model                LOCKED
Ledger Principles           LOCKED
Provider Adapter Model      LOCKED
Routing Baseline            LOCKED
Recon Model                 LOCKED
Settlement Model            LOCKED
Security Baseline           LOCKED
API Principles              LOCKED
Database Principles         LOCKED
Async Model                 LOCKED
Deployment Baseline         LOCKED
Performance Target          LOCKED
Observability Baseline      LOCKED
```

Perubahan selanjutnya harus dikelola sebagai revision PRD/Architecture Decision Record, bukan perubahan informal yang mengubah financial invariant tanpa review.
