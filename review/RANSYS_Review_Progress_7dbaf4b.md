# RANSYS Review dan Progress — 7dbaf4b

Tanggal: 27 September 2026, Asia/Jakarta

Base: `b9616fbbc2bd219f1d54526f8ba9479fbf2f755c`

Head: `7dbaf4b5078166cfb2bde678ae6b026ebe04e45d` (origin/main saat fetch).

**Review gate: BLOCKED. Satu P1 evidence-enrichment masih terbuka; ada satu P2 fallback data.**

## Scope / bukti

Review perubahan d88c645, 2e0ce55 dan HEAD, total tujuh file: source finalization/processing, regression tests, ADR-027, CLAUDE.md, README.md dan salinan review historis. Progress ditaksir dari scope PRD, main.md, handoff M12, README dan komponen src yang tersedia; bukan audit menyeluruh ulang atas seluruh repository.

Runtime dotnet tetap tidak tersedia pada lingkungan reviewer. Klaim implementer: 1.454 passed, 0 failed/skipped pada d88c645, .NET 10.0.401, PostgreSQL 18; empat tests diklaim gagal sebelum fix dan lulus setelahnya; concurrency REFUND/VOID/REVERSAL diklaim diulang delapan kali. Reviewer membaca source test, tetapi belum menjalankan ulang atau memverifikasi artefak hasil run tersebut. Diff check source/tests/docs yang diperiksa bersih. Tidak ada perubahan source atau push dari reviewer.

## Status T1–T4

| Temuan | Status saat ini |
|---|---|
| T1: ConflictRecorded mengganti accepted evidence | Addressed secara static untuk skenario SUCCESS → conflicting FAILED. Gate sekarang melarang overwrite pada ConflictRecorded; test konflik berbeda/kosong/berulang ditambahkan |
| T2: NoChange membuang evidence / sync tidak memasok evidence | Partial. Evidence sync sudah diteruskan dan NoChange kini menyimpan; tetapi penyimpanan masih blind replace, belum enrichment yang aman (U1) |
| T3: Replay STAN/RRN lama | Addressed secara static untuk kasus late callback dengan reference lengkap. Build kini memilih projection; test timeout nyata → callback → GET/replay ditambahkan. Data kosong masih punya gap terpisah U2 |
| T4: concurrency test mengabaikan Accepted=false | Addressed pada kelemahan assertion: hasil kedua pihak diperiksa, bounded timeout, varian reversal ditambahkan. Scheduling Task.Run masih probabilistik, bukan deterministic lock-barrier test; bukan blocker baru karena lock order kode telah dibenahi |

R3 (snapshot replay), R5 (recovery outbox) serta RR1 (parent → child lock order) tetap dianggap addressed secara static dalam scope temuan sebelumnya.

## U1 — P1: NoChange masih dapat menghapus atau mengganti accepted evidence

Lokasi: `src/Ransys.TransactionCore/Finalization/TransactionFinalizationService.cs:196–207`; `Domain/Transactions/Transaction.cs`, RecordLatestProviderResult; readers OriginalProviderReferences.From dan TransactionProcessingService.Build.

Komentar menyebut evidence “richer”, tetapi tidak ada pemeriksaan richness, freshness, identitas reference atau merge rule. Semua Evidence nonnull pada NoChange mengganti seluruh projection. Status yang sama bukan bukti bahwa payload lebih lengkap atau lebih baru.

Reproduksi dari alur kode:
1. Attempt mencatat timeout tanpa reference.
2. Callback SUCCESS membawa provider reference A, STAN/RRN dan data lengkap; projection tersimpan.
3. Callback SUCCESS lain untuk transaksi yang sama hanya membawa status, dengan references kosong dan data {}.
4. CompleteSuccess → NoChange; projection diganti menjadi kosong. Immutable timeout attempt tidak punya reference untuk fallback. GET/replay/child refund atau reversal kehilangan reference yang sebelumnya tersedia.

Variasi: callback pertama memberi reference A, duplicate SUCCESS memberi reference B. Keduanya dianggap NoChange walau identitas transaksi provider berubah. Reference B langsung menjadi acuan child berikutnya tanpa penanganan konflik evidence. Juga dapat terjadi saat callback lengkap tiba sebelum sync SUCCESS yang payload-nya lebih minim.

Dampak: evidence bisnis yang sudah diketahui tidak monotonic; duplicate delivery yang sah dapat mengurangi informasi atau mengganti identitas provider. Tidak ada bukti debit ganda langsung, tetapi operasi lanjutan dapat kehilangan/salah menggunakan reference original.

Perbaikan yang diminta:
- Definisikan enrichment accepted result, terpisah dari perubahan status. Untuk duplicate hasil yang sama, pertahankan known reference; isi field yang belum ada dari report terverifikasi.
- Bedakan reference baru yang melengkapi dari reference berbeda yang bertentangan. Jangan auto-replace A→B hanya karena keduanya SUCCESS.
- Untuk data produk, tetapkan semantik patch/snapshot/version: jangan generik merge semua field jika bisa mencampur snapshot yang tidak kompatibel.
- Gunakan korelasi provider/attempt dan kebijakan freshness yang eksplisit. Simpan konflik evidence untuk investigasi sesuai desain, tanpa mengganti accepted identity secara diam-diam.
- Revisi ADR-027/CLAUDE.md: NoChange boleh memperkaya evidence, bukan izin unconditional overwrite.

Acceptance tests:
1. recorded TIMEOUT → SUCCESS penuh → SUCCESS kosong: accepted reference/data tetap ada, posting tetap satu.
2. SUCCESS penuh → duplicate SUCCESS parsial: field lama tidak hilang.
3. SUCCESS reference A → SUCCESS reference B: A tidak diganti diam-diam; hasil kebijakan konflik dapat ditelusuri.
4. Callback SUCCESS lengkap mendahului sync SUCCESS minim, lalu reload/replay dan actual refund/reversal request: reference tetap benar.

Test T2-A baru hanya menguji arah kosong → lengkap. Arah lengkap → kosong/parsial/berbeda belum dilindungi.

## U2 — P2: Empty final data memunculkan kembali data PENDING lama pada replay

Lokasi: `Processing/TransactionProcessingService.cs:708–715,949–955`; `Persistence.PostgreSql/DbValues.cs:53–57`; `Providers/ProviderResultInterpreter.cs` pemetaan Data kosong ke null.

Replay memakai `LatestProviderResult?.Evidence.Data ?? LatestOutcome?.Data`. Jika projection final ada tetapi Data-nya null, fallback mengambil data attempt lama. Empty dictionary dipetakan ke null oleh interpreter/persistence. Keberadaan accepted final result tidak dibedakan dari belum adanya accepted result.

Skenario konkret (memperluas test Sync_success_after_a_stale_pending_callback_projects_its_own_final_evidence):
1. Callback PENDING menyimpan data `{field: temp}` di outcome immutable.
2. Sync SUCCESS membawa final data `{}`; projection final disimpan dengan Data null.
3. Respons pertama dapat menampilkan `{}`.
4. Replay memuat projection final tetapi fallback ke Data dari PENDING attempt; respons SUCCESS sekarang berisi `{field: temp}`.

Perbaikan: jika projection accepted ada, baca data projection sesuai semantiknya; jangan fallback ke outcome lama hanya karena accepted data kosong. Fallback legacy berlaku ketika projection tidak ada, bukan ketika projection menyatakan data kosong. Jika null dimaksudkan “belum diketahui”, simpan pembeda eksplisit dari empty-final dan gunakan policy yang sama di semua readers.

Acceptance test: callback PENDING dengan data temp → sync SUCCESS dengan data kosong → fresh-session replay. First response dan replay konsisten, tidak menghidupkan kembali data PENDING. Uji legacy row tanpa projection agar backward compatibility tetap benar.

## Progress project — estimasi reviewer, bukan persentase resmi backlog

**Keseluruhan scope PRD: sekitar 40% (kisaran kasar 30–50%). Core transaksi + merchant API: sekitar 85% implementasi. Production sign-off: belum lulus.**

Basis: PRD mencakup lebih dari engine transaksi: integrasi provider, keamanan produksi, Backoffice, IAM/maker-checker, recon, settlement, operasi dan performance/HA. Milestone remediation 13–15 tidak dihitung sebagai tambahan fitur produk; menghitung “milestone selesai / milestone yang sudah dibuat” akan menggembungkan progress.

Belum ada WBS seluruh produk dengan bobot effort yang disepakati. Karena itu angka berikut adalah estimasi kematangan implementasi berbobot scope, bukan earned value, persentase waktu, persentase anggaran, atau prediksi tanggal selesai. Nilai per area adalah judgement reviewer dengan ketidakpastian, bukan hasil pengukuran test coverage.

| Area PRD | Bobot estimasi | Implementasi estimasi | Kontribusi ke total | Dasar |
|---|---:|---:|---:|---|
| Core transaksi, domain, wallet, ledger, DB | 25% | 85% | 21,25 poin | Implementasi luas beserta migrations/tests; callback evidence masih blocker |
| Merchant REST API dan contracts | 10% | 80% | 8,00 poin | Endpoints/DTO/validation tersedia; katalog kode dan replay edge case belum tuntas; production auth dinilai di area security |
| Adapter dan integrasi provider/protokol | 10% | 35% | 3,50 poin | SDK/contract/gRPC tersedia, registry host masih tanpa provider nyata; cakupan SOAP/ISO/TCP belum terbukti |
| Routing, recovery, resilience dan async | 10% | 40% | 4,00 poin | Routing/outbox services tersedia; recovery schedule/circuit breaker/health serta backpressure produksi masih gap |
| Keamanan produksi, secrets dan kontrol akses | 10% | 20% | 2,00 poin | Abstraction/dev auth tersedia; production auth sengaja fail closed; signature profile masih belum final |
| Backoffice, IAM, maker-checker workflows, reporting | 10% | 5% | 0,50 poin | Fondasi event/schema/approval reference ada; consumer/projection dan aplikasi workflow belum terbukti tersedia |
| Reconciliation engine | 8% | 0% | 0,00 poin | State/model dukungan ada, tetapi engine matching/import/operasi recon belum terbukti diimplementasikan |
| Settlement dan business closing | 7% | 0% | 0,00 poin | Desain/status tidak dihitung sebagai engine operasional |
| Observability, audit operasional, alerting | 5% | 20% | 1,00 poin | Logging/correlation fondasi; monitoring metrics/dashboards/alerting/audit terpadu belum terbukti lengkap |
| Deployment, HA/DR dan release certification | 5% | 10% | 0,50 poin | Host/build/test foundation; bukti benchmark realistis, deployment qualification, HA/restore belum tersedia dalam review |
| **Total** | **100%** | — | **40,75 poin ≈ 40%** | Pembulatan sengaja kasar |

Core + API saja: (21,25 + 8,00) / (25 + 10) ≈ 84%, dibulatkan menjadi sekitar 85%. Kedua angka memakai denominator berbeda; 85% tidak berarti seluruh RANSYS hampir siap produksi. Skor rendah area lain berarti implementasi belum terlihat dalam repo yang dinilai, bukan berarti tidak ada desain atau pekerjaan di luar repo.

Kesiapan produksi tidak dinyatakan sebagai angka palsu: saat ini **belum lolos gate**, karena ada P1, production authentication belum aktif, provider nyata belum terintegrasi, dan bukti qualification belum tersedia. Tidak ada angka progress sebelumnya yang dihitung dengan rubric ini; jadi belum bisa mengklaim kenaikan sekian persen antar-review. Rubric ini dapat menjadi baseline tracking selanjutnya, lalu disesuaikan bila scope rilis pertama dipersempit/disepakati.

## Prioritas berikutnya

1. Tutup U1 dengan policy enrichment yang eksplisit dan regression tests lengkap; perbaiki U2 dalam read-path yang sama.
2. Jalankan suite PostgreSQL dan kirim bukti run terikat SHA. Pertahankan perbaikan T1/T3/T4; tidak perlu menulis ulang komponen yang sudah sesuai.
3. Setelah core review lolos, fokus pada jalur end-to-end yang dapat diuji: production auth profile, satu adapter provider nyata, recovery scheduling/operational controls.
4. Selanjutnya bangun Backoffice projection/inbox dan workflow operasional, lalu recon/settlement sesuai target rilis. Ini rekomendasi urutan, bukan keputusan scope baru yang sudah disepakati.

**Keputusan review HEAD 7dbaf4b: BLOCKED pada U1. Progress estimasi PRD sekitar 40%, core + API sekitar 85%; angka bukan production certification.**
