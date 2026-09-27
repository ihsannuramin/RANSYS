# RANSYS Review dan Progress — 2ff8477

Tanggal: 27 September 2026, Asia/Jakarta

Base: `7dbaf4b5078166cfb2bde678ae6b026ebe04e45d`

Head: `2ff84776caf7c3b966ab2ea989d6dd3cec5bac84` (origin/main saat fetch)

**Gate: BLOCKED. U2 addressed pada GET/replay; U1 masih partial.**

## Scope dan batas verifikasi

Review diff 10 file termasuk perbaikan e2c5cf9, dokumentasi f13ed22, source domain/finalization/readers, regression tests, ADR-027, CLAUDE.md dan README.md. Checkout bersih; diff check source/tests/docs bersih. Tidak ada source code diubah atau dipush reviewer.

Dotnet belum tersedia pada lingkungan reviewer. README mengklaim build 0 warning/error dan 1.464 passed, 0 failed/skipped pada e2c5cf9, serta verifikasi tests gagal sebelum fix. Reviewer memeriksa kode tests, bukan menjalankan ulang atau mengesahkan klaim runtime tersebut.

## Status sebelumnya

| Item | Hasil |
|---|---|
| U1: duplicate menghapus evidence / mengganti identity | Partial: duplicate tanpa identity tidak lagi menghapus projection; same-reference dengan field null mempertahankan field lama. Namun identity comparison dan merge belum benar (V1), serta konflik yang terdeteksi hilang di caller (V2) |
| U2: final empty data jatuh ke PENDING lama | Addressed secara static pada Build/replay dan GET. Test final empty + legacy no-projection ditambahkan. Child request masih punya kebijakan fallback berbeda (V3) |
| RR1 lock order, T1 status conflict, T4 assertion concurrency | Perbaikan sebelumnya dipertahankan; tidak dibuka ulang |

## V1 — P1: Identity harus dibandingkan per field; merge masih bisa mengganti identity yang telah diketahui

Lokasi: `src/Ransys.Domain/Transactions/Transaction.cs:470–491`, MergeLatestProviderResult.

Kode membentuk identity sebagai `ProviderReference ?? ProviderStan ?? ProviderRrn`, lalu membandingkan satu string. Ketiga field punya makna berbeda dan tidak boleh dianggap identifier yang saling menggantikan. Setelah string utama sama, incoming nonnull menimpa masing-masing field, walau field lama sudah terisi.

Contoh deterministik dari alur kode (belum dieksekusi pada runtime .NET reviewer):

| Existing evidence | Incoming duplicate | Perilaku kode saat ini | Masalah |
|---|---|---|---|
| ref=P, STAN=S1, RRN=R1 | ref=P, STAN=S2, RRN=R2 | identity P==P; Enriched; S1/R1 diganti S2/R2 | Konflik field sekunder tidak terdeteksi |
| ref=null, RRN=R1 | ref=P, RRN=R1 | R1 dibandingkan dengan P; ConflictingIdentity | Pelengkapan reference valid dengan RRN yang sama ditolak |
| ref=P, RRN=R1 | ref=null, RRN=R1 | P dibandingkan dengan R1; ConflictingIdentity | Partial duplicate yang konsisten disalahklasifikasikan |

Dampak: reference yang dipakai GET/replay/refund/reversal masih dapat berubah tanpa kebijakan konflik, atau evidence valid gagal dilengkapi. Perbaikan saat ini hanya melindungi bentuk input yang memilih field prioritas pertama yang sama.

Perbaikan:
- Bandingkan nilai pada field bernama yang sama, menggunakan provider/transaction/attempt correlation yang telah diverifikasi.
- Perbedaan pada field yang sama-sama terisi harus ditangani sesuai policy, bukan disembunyikan oleh kesamaan ProviderReference.
- Field incoming kosong tidak menghapus existing; field existing kosong boleh diperkaya setelah korelasi memadai. Jika tidak ada overlap yang cukup, definisikan penanganan ambiguous secara eksplisit.
- Jangan menjadikan Data nonempty otomatis lebih kaya: baris 497 masih mengganti seluruh dictionary lama dengan incoming nonempty, sehingga partial nonempty dapat menghapus key lama. Pilih semantik payload snapshot atau patch secara eksplisit; jangan merge key bisnis secara sembarang.

Acceptance tests: seluruh tiga baris tabel, duplicate penuh→parsial dengan field prioritas berubah, shared reference tetapi RRN berbeda, dan partial nonempty business data. Pastikan accepted reference tetap stabil, enrichment yang valid diterima, dan actual child adapter request menerima identity yang benar.

## V2 — P2: ConflictingIdentity ditelan dan di-ack sebagai duplicate normal

Lokasi: `TransactionFinalizationService.cs:202–215`; `ProviderCallbackSink.cs` mapping TransitionKind.NoChange → Duplicate.

Merge mengembalikan ConflictingIdentity, tetapi finalization hanya menangani Recorded/Enriched. Sisanya diabaikan dan Snapshot NoChange dikembalikan. Tidak ada log, durable exception/audit atau informasi konflik pada hasil finalization. Sink lalu meng-ack Accepted=true/Duplicate.

Test `Success_report_with_a_different_reference_never_silently_replaces_the_accepted_one` bahkan mengassert ack Duplicate. Ia membuktikan A tidak diganti B, tetapi tidak membuktikan konflik dapat ditelusuri. Return enum dari fungsi internal bukan observability bila caller membuangnya.

Perbaikan: pertahankan accepted evidence, tetapi catat konflik secara terkontrol dan idempotent melalui mekanisme audit/reconciliation yang disepakati. Ack transport boleh tetap accepted agar delivery tidak berulang tanpa akhir; jangan menghilangkan fakta konflik. Jangan log credential/raw sensitive payload.

Acceptance: SUCCESS(A) → SUCCESS(B) tetap mempertahankan A, menghasilkan satu catatan konflik yang dapat ditelusuri, dan pengulangan report B tidak menghasilkan financial posting atau banjir exception duplikat.

## V3 — P1: Child request masih bisa memakai reference PENDING yang sudah superseded

Lokasi: `src/Ransys.TransactionCore/Providers/ProviderRequestFactory.cs:52–63`, OriginalProviderReferences.From(Transaction,...).

Reader child tidak diubah: projection hanya digunakan jika sekurangnya satu reference nonnull; jika accepted projection ada tetapi semua reference kosong, reader kembali memindai immutable attempts. GET/replay sekarang justru secara benar tidak melakukan fallback tersebut.

Skenario:
1. Callback PENDING menyimpan provisional reference TEMP pada immutable attempt.
2. Sync SUCCESS diterima dengan projection final tanpa reference (Applied replace adalah kebijakan yang memang dipertahankan di fix ini).
3. GET/replay menggunakan projection final tanpa TEMP.
4. Refund/reversal mengambil TEMP lewat fallback attempt dan meneruskannya ke provider.

Tidak ada bukti TEMP sah sebagai reference final hanya karena ia pernah ada. Bila suatu provider memang memakai reference yang stabil lintas status, retention harus menjadi aturan provider/result-selection yang tervalidasi, bukan fallback global yang menghidupkan evidence superseded.

Perbaikan: satukan pemilihan accepted reference untuk child dengan aturan GET/replay. Jika projection ada, jangan fallback ke provisional history. Jika provider membutuhkan reference final tetapi belum tersedia, fail closed/defer operasi tersebut dengan hasil yang eksplisit; provider yang dapat mengalamatkan original via id lain harus memiliki policy yang jelas. Jangan membuat asumsi bahwa semua provider membutuhkan reference yang sama.

Acceptance: processing PENDING(TEMP) → SUCCESS(no references) → actual refund/reversal request; TEMP tidak terkirim. Legacy transaction tanpa projection tetap boleh memakai fallback yang benar. Test harus melewati child processing/provider request, bukan hanya GET.

## Dokumentasi yang perlu dikoreksi

CLAUDE.md mengatakan semua readers termasuk child hanya fallback bila projection tidak ada; implementasi child tidak demikian. ADR-027 justru menyebut fallback child sengaja dipertahankan karena child perlu “some reference to proceed”. Itu bertentangan dengan accepted-result precedence yang menjadi tujuan perbaikan.

ADR-027 bagian merge juga menyatakan MergeLatestProviderResult dipanggil pada NoChange dan Applied, padahal Applied masih menggunakan RecordLatestProviderResult. README terbaru menjelaskan perbedaan ini dengan lebih benar. Selaraskan dokumen menjadi satu aturan yang sesuai implementasi yang disetujui; jangan mengubah Applied menjadi merge hanya untuk menyamakan dokumen karena PENDING → final memang memerlukan aturan supersession tersendiri.

## Progress — rubric sama dengan review 7dbaf4b

| Ukuran | Sebelumnya | Saat ini | Interpretasi |
|---|---:|---:|---|
| Seluruh PRD | ≈40% | **≈40%** | Belum ada workstream produk baru; perbaikan correctness tidak otomatis menambah scope selesai |
| Core transaksi + merchant API | ≈85% | **≈85%** | Kualitas membaik, tetapi evidence handling masih blocker; perubahan kecil tidak layak dianggap kenaikan persentase presisi |
| Gate review | BLOCKED | **BLOCKED** | U2 tertutup untuk GET/replay; identity merge dan child reference belum selesai |
| Production readiness | Belum lolos | **Belum lolos** | Production auth, provider nyata, operational recovery/resilience dan qualification tetap outstanding |

Estimasi berbobot scope sebelumnya adalah 40,75 poin yang dibulatkan kasar menjadi 40%, dengan kisaran 30–50% karena belum ada WBS berbobot effort yang disepakati. Bobot dipertahankan: core 25%, API 10%, adapter/integrasi 10%, routing/recovery/async 10%, security 10%, Backoffice 10%, recon 8%, settlement 7%, observability 5%, deployment/qualification 5%. Tidak ada bukti pada update ini yang mengubah skor area-area tersebut secara material.

Angka bukan test coverage, waktu pengerjaan, biaya, atau certification. Jumlah tests dilaporkan naik dari 1.454 ke 1.464 (+10); ini kemajuan verifikasi implementer, bukan berarti fitur PRD bertambah 10 unit. U2 memiliki solusi yang lebih baik dan tests tambahan; progress correctness nyata meskipun pembulatan persentase produk tetap.

## Handoff agar perbaikan berikutnya menyeluruh

Jangan menambah special-case satu per satu. Tuliskan decision table untuk: Applied vs NoChange vs conflict; field identity yang overlap/tidak overlap/bertentangan; data absent/empty/partial/full; projection absent/present; dan reader GET/replay/child. Jadikan tabel itu policy tunggal dengan tests.

Prioritas: V1 dan V3 wajib sebelum sign-off; V2 serta ketidaksesuaian ADR/CLAUDE perlu ditutup dalam remediation yang sama. Setelah itu jalankan build/test PostgreSQL terisolasi dan kirim bukti terikat SHA. Fitur berikutnya yang direkomendasikan setelah gate core lulus tetap production auth profile + satu provider nyata + recovery operasional, lalu Backoffice/recon/settlement sesuai scope rilis yang disepakati.

**Kesimpulan: BLOCKED pada 2ff8477; progress PRD ≈40%, core/API ≈85%. Review ini tidak mengubah kode atau mengeksekusi fase implementasi berikutnya.**
