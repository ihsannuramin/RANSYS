# RANSYS Architecture Review — f1fbefe

Tanggal: 27 September 2026 (Asia/Jakarta)  
Repository: https://github.com/ihsannuramin/RANSYS  
Branch: main  
Reviewed HEAD: `f1fbefe401d36a13acaceb4affa9e2ddf4606a52`  
Implementation comparison: `996c85c..f1fbefe` (M12a–M12g)  
Decision: **BLOCKED untuk sign-off Milestone 12 / gate fase berikutnya.**

## Scope dan batas bukti

HEAD telah dicocokkan dengan origin/main. Commit terakhir hanya menambah dokumentasi pada CLAUDE.md dan README.md; review karena itu menelusuri implementasi M12 yang dirangkum commit tersebut, terutama orchestration, callback/finalization, child transactions, idempotency, API mapping/validation/authentication, serta test terkait. Rentang M12 menyentuh 119 file; ini focused architecture/code review, bukan klaim audit menyeluruh atas setiap baris repository.

Sembilan dokumen lampiran identik dengan versi docs/ pada HEAD setelah normalisasi CRLF/LF. Dokumen repo tambahan (OpenAPI, adapter contract, handoff dan ADR terkait) digunakan untuk menilai implementasi. CLAUDE.md dan README.md dibaca untuk konsistensi instruksi dan status.

`dotnet --info` gagal dengan `dotnet: command not found`. Build, unit tests, integrasi PostgreSQL, dan reproduksi race **belum dijalankan oleh reviewer**. Angka 1.439 test/0 warning adalah klaim README, bukan hasil verifikasi independen. Temuan berikut berasal dari alur kode; acceptance tests adalah pekerjaan yang diminta, bukan test yang sudah lulus. Tidak ada perubahan source code atau push ke GitHub.

Prioritas: P1 = correctness utama yang wajib diperbaiki sebelum sign-off; P2 = masalah fungsional/konsistensi yang harus ditutup atau secara eksplisit diterima dengan pemilik dan target. Tidak ditemukan bukti untuk mengklaim insiden produksi telah terjadi.

## Ringkasan temuan

| ID | Prioritas | Temuan |
|---|---|---|
| R1 | P1 | Validasi provider callback tidak atomik dengan finalization; race dengan failover |
| R2 | P1 | Referensi dan bukti hasil callback tidak dipersist untuk operasi lanjutan |
| R3 | P1 | Replay bergantung pada reference data/currency version terbaru |
| R4 | P2 | Replay inquiry/payment menghilangkan response data |
| R5 | P2 | Recovery mengubah status tanpa event outbox transaksi |
| R6 | P2 | CLAUDE.md/README.md memuat instruksi/status yang saling bertentangan |

## R1 — Validasi provider callback harus berada di dalam lock finalization

**Lokasi:** `src/Ransys.TransactionCore/Providers/ProviderCallbackSink.cs`, baris 65–90; `src/Ransys.TransactionCore/Finalization/TransactionFinalizationService.cs`, baris 108–118 dan 334–337. Session menggunakan ReadCommitted (`src/Ransys.Persistence.PostgreSql/PostgresSession.cs`).

Sink membaca transaksi dengan `forUpdate: false`, mengecek `CurrentProvider`, lalu mengirim ProviderResultCommand yang tidak membawa expected provider. Finalization baru mengambil row lock dan memuat ulang transaksi. Provider yang sudah divalidasi bisa berbeda dari provider saat posting dilakukan.

**Skenario interleaving:**
1. Transaksi masih routed ke A; callback A membaca dan lolos validasi provider.
2. Jalur sync memperoleh lock, mencatat NOT_SENT dari A, melakukan failover ke B, membuat attempt B, lalu commit.
3. Callback A memperoleh lock finalization setelah itu. Tidak ada pemeriksaan ulang identitas provider; SUCCESS A dapat diterapkan ke transaksi yang sekarang routed ke B.
4. PostPaymentAsync memakai CurrentProvider B. Hasil A berpotensi menyebabkan posting yang dikaitkan ke B sementara attempt B berjalan.

Skenario ini membutuhkan laporan A yang terlambat/bertentangan dengan NOT_SENT. Sistem memang harus menangani laporan abnormal; jangan menganggap semua sumber hasil selalu konsisten. Ini adalah race yang diturunkan dari kode, belum direproduksi secara runtime.

**Perbaikan:** validasi expected provider dan korelasi attempt di dalam critical section yang sama dengan finalization; pertahankan lock parent → child. Membawa ProviderId ke command tanpa validasi di dalam lock belum cukup. Callback untuk attempt lama/contradictory perlu penanganan exception/investigation yang eksplisit, bukan posting atas provider baru.

**Acceptance test:** test PostgreSQL dengan barrier: pause callback setelah read awal, commit failover A→B, lanjutkan callback. Pastikan callback A tidak mem-post hasil ke B, tidak melepas hold secara keliru, dan journal tidak terduplikasi. Tambahkan callback child agar perbaikan tidak membalik urutan lock.

Test mismatch yang ada (`CallbackAndChildFinalizationTests.Unknown_transaction_other_provider_and_not_sent_callbacks_are_not_accepted`) bersifat sequential; tidak menguji perubahan provider di sela pemeriksaan dan finalization.

## R2 — Callback membuang referensi provider dan bukti hasil

**Lokasi:** `ProviderCallbackSink.cs`, baris 56–90; `Providers/ProviderRequestFactory.cs`, baris 30–46; `Processing/TransactionProcessingService.cs`, baris 529–542; `Persistence.PostgreSql/Queries/PostgresTransactionQuery.cs`, query attempt terbaru.

Callback diinterpretasikan menjadi outcome lengkap, tetapi yang diteruskan hanya resolution, response code dan callback ID pendek pada reason description. Tidak ada penyimpanan `interpreted.Outcome`, provider reference/STAN/RRN, raw references, atau response data pada sink. Finalization command juga tidak membawa field tersebut.

**Skenario:** payment timeout tanpa provider reference → SUCCESS callback membawa reference `PRV-CB`/RRN baru → transaksi menjadi SUCCESS/POSTED → refund atau reversal dibuat. OriginalProviderReferences.From membaca attempt outcomes saja; hasil timeout lama tidak memiliki reference dari callback. Child request kehilangan identitas provider yang diperlukan provider tertentu untuk refund/reversal. GET juga membaca STAN/RRN dari attempt outcomes sehingga tidak dapat menampilkan referensi callback tersebut.

**Perbaikan:** simpan hasil provider asinkron secara durable dan idempotent dalam transaksi yang sama dengan perubahan status/ledger. Tetapkan model evidence/result tersendiri atau cara memperkaya referensi yang tidak menimpa outcome attempt yang immutable. Jangan mengganti hasil timeout lama secara diam-diam. Child request, replay dan GET harus memakai referensi authoritative yang telah dikorelasikan, termasuk hasil callback setelah crash.

**Acceptance test:** timeout → callback SUCCESS dengan provider reference dan RRN → restart proses → GET dan refund/reversal tetap mempunyai referensi tersebut. Uji duplicate callback dan contradictory callback, termasuk tepat satu posting. Test yang sekarang mengirim `PRV-CB` belum mengassert persistensi reference atau round-trip ke child request.

## R3 — Replay berubah akibat reference data terbaru

**Lokasi:** `Processing/TransactionProcessingService.cs`, baris 188–242; `Domain/Transactions/TransactionFingerprint.cs`, baris 63–64; `Persistence.PostgreSql/ReferenceData/PostgresReferenceDataReader.cs`.

Sebelum mencari idempotency claim, PrepareOriginalAsync mengharuskan produk ACTIVE, memuat currency definition yang aktif sekarang, mencari wallet versi tersebut, dan membentuk fingerprint dari definition terbaru. Fingerprint mencakup currencyVersion dan currencyScale.

**Skenario A:** payment sukses, kemudian product menjadi INACTIVE. Retry payload yang sama dalam 24 jam ditolak PRODUCT_NOT_AVAILABLE sebelum transaksi lama ditemukan.

**Skenario B:** request dibuat menggunakan currency definition v1; definisi v2 aktif sebelum retry. Payload publik yang sama membentuk fingerprint berbeda → 409 duplicate conflict. Bila wallet v2 belum ada, ia bahkan gagal lebih awal dengan WALLET_NOT_FOUND. Ini bukan payload merchant yang berubah.

**Dampak:** merchant yang kehilangan respons tidak bisa mendapatkan hasil transaksi lama dengan retry yang dijanjikan kontrak. Tidak ada bukti jalur ini langsung menggandakan debit; masalahnya adalah correctness replay dan ketidakpastian hasil.

**Perbaikan:** setelah autentikasi/validasi bentuk dasar, cari claim aktif berdasarkan channel + clientReference terlebih dahulu. Untuk existing transaction, bandingkan payload dengan identitas dan snapshot currency/product yang digunakan transaksi itu. Tetap deteksi payload berbeda dan jaga ownership. Pemeriksaan eligibility konfigurasi terbaru hanya untuk transaksi baru. Jangan menghapus currency version dari fingerprint tanpa keputusan kompatibilitas.

**Acceptance tests:** retry setelah product dinonaktifkan, wallet ditutup, dan currency definition baru aktif. Semua payload sama tetap mengembalikan transaksi lama tanpa provider call/reserve baru; perubahan amount/destination tetap konflik. Uji juga claim expiry dan race dua request baru.

## R4 — Response data hilang pada replay

**Lokasi:** `Processing/TransactionProcessingService.cs`, ReplayAsync baris 572–597 dan CompleteAsync baris 635–649; `tests/Ransys.IntegrationTests/Processing/TransactionProcessingTests.cs`, test inquiry baris 216–227.

Respons pertama memakai `interpreted.Result.Data`. Replay mengembalikan Prepared tanpa data lalu selalu memanggil Build dengan `NoData`. ProviderResultInterpreter tidak menyimpan data itu ke outcome. Test inquiry saat ini hanya memeriksa respons pertama dengan billAmount.

**Skenario:** inquiry menghasilkan data tagihan, respons HTTP hilang, merchant retry clientReference yang sama. Status tetap SUCCESS tetapi `data` menjadi `{}`. Request tidak boleh dipanggil ulang ke provider, tetapi hasil yang dibutuhkan merchant juga tidak tersedia.

**Perbaikan:** persist proyeksi response bisnis yang diperbolehkan untuk publik, lalu gunakan pada replay. Pisahkan data produk dari metadata internal/raw payload. Kontrak menjanjikan current transaction, jadi timestamp/status tidak harus byte-identik; data bisnis yang masih relevan jangan hilang semata karena replay.

**Acceptance test:** inquiry dengan billAmount/nonempty data → restart → retry. ID dan data bisnis bertahan; adapter dipanggil sekali. Uji payment product data dan recovery/callback path yang memberikan final data.

## R5 — Recovery status tidak menghasilkan outbox event transaksi

**Lokasi:** `src/Ransys.TransactionCore/Attempts/AttemptRecoveryService.cs`, baris 112–133.

RecoverAsync langsung memanggil MarkInDoubt, mengganti hold reason, lalu UpdateAsync. Service tidak memiliki IOutboxWriter dan tidak memakai finalization. TransactionStore.UpdateAsync tidak meng-enqueue status event. Dengan demikian recovery dapat commit IN_DOUBT tanpa TRANSACTION_IN_DOUBT; Sequence Pack SD-02 mengharuskan event tersebut dan Backoffice mengandalkan outbox/source_version.

**Dampak:** ketika recovery ini diaktifkan, source-of-truth dan projection downstream dapat berbeda sampai ada perubahan berikutnya. Scheduler recovery memang belum di-host; itu tidak menghilangkan gap di service yang akan dipakai. Temuan ini adalah gap fondasi yang terlihat saat review integrasi M12, bukan klaim baru diperkenalkan oleh commit dokumentasi.

**Perbaikan:** gunakan jalur finalization yang konsisten atau emit event transaksi yang sama secara atomik dengan recovery. Untuk child, pertahankan parent → child. Recovery duplicate harus no-op; rollback tidak boleh meninggalkan event.

**Acceptance test:** outcome-less attempt → recovery → commit; assert status IN_DOUBT, hold tetap, satu event dengan source_version sesuai row. Retry recovery tidak menambah event. Uji rollback dan race callback/recovery. Test race yang ada memeriksa status/posting, belum membuktikan event khusus recovery.

## R6 — CLAUDE.md dan README.md perlu satu baseline instruksi yang konsisten

| Lokasi | Masalah | Koreksi yang diminta |
|---|---|---|
| CLAUDE.md:7 | Handoff utama hanya main.md; fase M12 memakai dokumen tambahan | Cantumkan 20260927-handoff-part2.md dan scope/status M12 |
| CLAUDE.md:121–135 | Document map belum memasukkan kontrak baru dan ADR; OpenAPI/Adapter masih disebut next planned | Tambah kontrak authoritative dan precedence ADR accepted; hapus status planned yang usang |
| CLAUDE.md:27 vs :163 | Format posting key ADR-001 `TX:<id>:...` berkonflik dengan contoh `TX123:...` | Gunakan contoh format ADR-001 secara konsisten |
| README.md:59,61 vs :104–125 | Adapter masih placeholder/API masih pending, tetapi M12 dinyatakan implemented | Sinkronkan solution layout dengan implementasi |
| README.md:67–80 | Status table hanya menambahkan M12c/d, belum M12a/b/e/f/g | Tampilkan status milestone lengkap dengan bukti per tahap |
| README.md:110 | Klaim semua DB-unavailable menghasilkan 503 tanpa provider call terlalu luas | Bedakan kegagalan pre-send dan post-send; post-send dapat menghasilkan IN_DOUBT 1002 |
| README.md:124 | Klaim 1.439 tests tanpa commit/log/tanggal verifikasi | Tautkan bukti test run ke SHA, environment dan hasil skipped/failed; reviewer belum mengonfirmasi angka ini |
| README.md:131 | Daftar ADR menghilangkan ADR-021/022 | Lengkapi indeks |
| README.md:159 | OpenAPI dan Adapter Contract masih deferred | Sisakan hanya artefak yang benar-benar belum tersedia |
| ADR-022/023/024 status | Masih Proposed meski implemented; README mengelompokkan beberapa di Decided | Pisahkan implemented dari accepted; jangan ubah menjadi Accepted tanpa keputusan pemilik |

Instruksi mesin yang bertentangan berpotensi membuat Claude Code mengulang implementasi, memilih format salah, atau menyatakan gate lulus sebelum keputusan arsitektur selesai.

Memory Claude yang diperiksa adalah **CLAUDE.md yang dilacak Git pada SHA ini**. Auto-memory lokal Claude Code, user-level CLAUDE.md, dan konfigurasi di komputer pengembang tidak tersedia melalui snapshot repo ini; belum diperiksa.

## Hal yang sudah mengikuti arah arsitektur

- Jalur merchant membuat transaksi/reserve/attempt dan commit sebelum panggilan provider.
- Hasil provider diproses dalam sesi DB terpisah; finalization memusatkan ledger dan event status.
- Child finalization memakai parent → child, serta refund memanggil perhitungan fee dari transaksi original.
- Timeout provider diperlakukan sebagai kemungkinan terkirim; hasil ambigu tidak digunakan untuk failover biasa.
- Production authentication fail closed dan development auth dibatasi environment merupakan keputusan ADR-022 yang eksplisit, bukan bug yang perlu diakali agar test/traffic lewat.
- Void belum memindahkan uang, sesuai ADR-019 interim.
- Test PostgreSQL, contract dan concurrency tersedia, tetapi keberadaannya tidak membuktikan skenario R1–R5 sudah tercakup atau lulus.

## Gate dan handoff Claude Code

1. Tangani R1–R3 sebelum meminta sign-off ulang. Sertakan regression tests yang gagal sebelum fix dan lulus sesudahnya.
2. Tutup R4–R6 untuk penyelesaian milestone; bila ada follow-up yang ingin ditunda, tulis alasan, owner, milestone target, dan keputusan penerimaan risiko. Jangan menandainya selesai secara sepihak.
3. Pertahankan immutable/released migrations. Jika evidence/result persistence membutuhkan schema baru, tambahkan migration baru dan jelaskan model/retention/idempotency-nya; jangan ubah outcome history lama tanpa ADR.
4. Jalankan build dan suite dengan .NET 10 serta PostgreSQL aktual pada database test terisolasi. Fixture test menjatuhkan schema; jangan gunakan database operasional.
5. Kirim base SHA, head SHA, ringkasan per finding, hasil build/test (passed/failed/skipped), dan ADR yang masih butuh keputusan. Sertakan bukti run, bukan hanya jumlah test di README.
6. Reviewer menilai commit perbaikan berikutnya; tidak ada review otomatis atau sign-off implisit setelah push.

**Status akhir review ini: BLOCKED, berdasarkan temuan static code R1–R3. Verifikasi runtime tetap outstanding.**
