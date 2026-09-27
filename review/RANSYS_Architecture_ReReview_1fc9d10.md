# RANSYS Architecture Re-review — 1fc9d10

Tanggal: 27 September 2026 (Asia/Jakarta)  
Repo: https://github.com/ihsannuramin/RANSYS  
Base: `f1fbefe401d36a13acaceb4affa9e2ddf4606a52`  
Head: `1fc9d10caf78d283d2ef1d4fb8f89bc1a3602702` (origin/main saat fetch)  
**Keputusan: BLOCKED — dua blocker callback belum selesai, salah satunya regresi dari perbaikan R1.**

## Ruang lingkup dan verifikasi

Review diff 24 file / lima commit remediation dan jalur kode terkait, regression tests, ADR-025/026, CLAUDE.md dan README.md. Baseline temuan adalah review f1fbefe dalam percakapan ini. Tidak ada source code yang diubah atau dipush oleh reviewer.

Commit yang diperiksa:
- `4e8edf5`: callback R1/R2.
- `93f6396`: replay snapshot R3.
- `bd80418`: response data persistence R4.
- `a2b9039`: recovery outbox R5.
- `1fc9d10`: documentation R6.

`git diff --check f1fbefe..HEAD` bersih; working tree bersih. Runtime dotnet tetap tidak tersedia di lingkungan reviewer, sehingga build/test PostgreSQL dan reproduksi deadlock belum dijalankan. README mengklaim 1.446 passed / 0 failed / 0 skipped pada a2b9039, .NET 10.0.401, PostgreSQL 18. Ini bukti yang dilaporkan implementer, bukan hasil runtime reviewer. Tidak ditemukan file .trx atau log build/test yang dilacak dalam checkout untuk mengaudit klaim itu. a2b9039 adalah commit kode terakhir sebelum perubahan dokumentasi.

## Status R1–R6

| ID | Status review ulang | Kesimpulan |
|---|---|---|
| R1 | Belum ditutup: race original diperbaiki, regresi lock order pada child | Lock kini mendahului provider check, tetapi child di-lock sebelum parent; lihat RR1 |
| R2 | Belum ditutup | Evidence hanya disimpan bila outcome belum tercatat; timeout/PENDING yang sudah tercatat tetap membuang evidence final; lihat RR2 |
| R3 | Addressed secara static untuk skenario yang dilaporkan | Existing claim dicari sebelum active reference data; fingerprint memakai currency original; test produk inactive/currency rollover/different product ditambahkan |
| R4 | Partial | Respons sync dapat dipersist dan di-replay lewat migration 0009; data final callback masih terkena RR2 |
| R5 | Addressed secara static | Recovery meng-enqueue event dalam session yang sama memakai helper bersama; test event count, source_version, dan duplicate recovery ditambahkan |
| R6 | Partial | Banyak informasi usang sudah dibenahi; klaim seluruh temuan selesai masih salah dan instruksi callback bertentangan dengan lock order; lihat RR3 |

“Addressed secara static” bukan sertifikasi bahwa seluruh test telah lulus atau seluruh variasi input sudah diaudit. Untuk R3/R5 tidak ditemukan blocker baru dalam diff yang diperiksa.

## RR1 — P1: Callback child membalik lock order dan dapat deadlock

**Lokasi utama:** `src/Ransys.TransactionCore/Providers/ProviderCallbackSink.cs:71` dan pemanggilan finalization pada baris 109–119.  
**Jalur pembanding:** `Finalization/TransactionFinalizationService.cs:105–118`; `Processing/TransactionProcessingService.cs:922–933`.

Fix R1 mengganti pembacaan awal menjadi `forUpdate: true` terhadap transaction id callback. Untuk refund/reversal/void, id ini adalah CHILD. Sink kemudian memanggil finalization, yang mengunci PARENT sebelum mengunci child. Karena child sudah terkunci oleh sink, urutan aktual callback menjadi child → parent. Jalur sync tetap parent → child.

Interleaving konkret:
1. Callback refund memperoleh lock child C di sink.
2. Sync result untuk refund yang sama memperoleh lock parent P di LockAsync.
3. Sync mencoba lock C dan menunggu callback.
4. Callback masuk finalization dan mencoba lock P, menunggu sync.
5. PostgreSQL harus membatalkan salah satu transaksi karena deadlock (40P01).

Dampak yang bisa dinyatakan dari kode: hasil valid dapat gagal difinalisasi, callback ditolak sementara untuk redelivery atau jalur sync kembali IN_DOUBT. Ini bukan bukti double posting atau korupsi saldo; transaksi DB tetap menjadi proteksi rollback. Namun melanggar invariant lock order dan mengganggu correctness/availability di kondisi callback bersamaan yang normal.

**Perbaikan yang diminta:** identifikasi parent melalui peek nonlocking yang hanya dipakai untuk menentukan urutan lock; lock parent dahulu bila child, lalu lock/reload child; lakukan provider/attempt validation menggunakan versi yang sudah terkunci. Alternatif: pindahkan validasi provider/evidence ke satu layanan finalization yang memiliki keseluruhan urutan lock. Jangan kembali ke validasi provider berdasarkan peek sebelum lock. Pertahankan atomicity evidence/status/ledger/outbox.

**Test wajib:** PostgreSQL concurrency test untuk REFUND, REVERSAL dan VOID, memanggil sink nyata bersamaan dengan sync finalization. Gunakan barrier/instrumentasi terkontrol pada perolehan lock dan bounded timeout. Pastikan tidak ada deadlock yang ditelan lalu dilaporkan seolah sukses; final state benar, journal sesuai semantik dan tidak duplikat. Test original callback vs failover juga perlu interleaving aktual.

**Kelemahan test sekarang:** `Stale_callback_from_a_provider_superseded_by_failover_is_rejected` (CallbackAndChildFinalizationTests.cs:136–161) menyelesaikan dan commit failover sebelum callback dimulai. Kasus ini sudah akan ditolak oleh provider-match check pada implementasi lama. Ia berguna sebagai stale-provider test, tetapi bukan regression test race yang dilaporkan sebelumnya dan tidak menguji child lock order.

## RR2 — P1: Final callback setelah outcome tercatat tetap kehilangan evidence dan data

**Lokasi:** `Providers/ProviderCallbackSink.cs:100–107`; jalur pencatatan sync di `Processing/TransactionProcessingService.cs:794–819`; consumer references di `Providers/ProviderRequestFactory.cs:30–46` dan replay di `Processing/TransactionProcessingService.cs`.

Sink hanya menyimpan `interpreted.Outcome` bila `!attempt.IsOutcomeRecorded`. Sesudah timeout/PENDING dicatat oleh processing, kondisi tersebut false. Sink tetap memfinalisasi SUCCESS dan meng-ack callback, tetapi reference/STAN/RRN/data dari callback tidak disimpan. Menambah response_data di attempt tidak menyelesaikan hal ini karena UPDATE outcome juga dijaga `outcome_recorded_at IS NULL`.

Skenario wajib yang masih gagal menurut jalur kode:
1. Payment dikirim; provider timeout. Core mencatat outcome TIMEOUT dan status IN_DOUBT.
2. Callback SUCCESS membawa provider reference/RRN serta data final baru.
3. Sink melewati RecordOutcomeAsync, tetapi finalization mem-post transaksi dan commit SUCCESS.
4. GET/child refund/reversal membaca outcome timeout lama; reference baru tidak tersedia. Replay membaca data kosong/lama.

Variasi yang sama terjadi untuk sync PENDING → callback SUCCESS, synthetic recovery outcome → callback SUCCESS, atau callback PENDING → callback SUCCESS. Bahkan callback PENDING pertama dapat menjadi outcome immutable yang menghalangi data final berikutnya.

**Perbaikan yang diminta:** pertahankan immutable attempt outcome, tetapi tambahkan durable result/evidence model atau authoritative result projection yang mendukung beberapa hasil asinkron per transaksi/attempt. Dedup callback berdasarkan identitas yang sesuai, simpan evidence final dalam transaksi yang sama dengan status/ledger/outbox, dan pastikan GET/replay/child request membaca hasil yang tepat. Bedakan transport attempt outcome dari perkembangan business result. Jangan menghapus guard immutability atau menimpa histori timeout secara diam-diam. Perubahan schema/semantik perlu dijelaskan dalam ADR dan additive migration baru.

**Test wajib:**
- Jalankan payment melalui processing service hingga outcome TIMEOUT benar-benar tercatat; kirim SUCCESS callback dengan reference/STAN/RRN/data final.
- Ulangi dengan outcome PENDING dan synthetic recovery yang sudah tercatat.
- Pakai sesi/service instance baru, pastikan GET dan replay dapat memperoleh hasil yang dibutuhkan serta refund/reversal adapter menerima reference original yang benar.
- Kirim callback duplikat dan conflicting; assert evidence/audit tetap konsisten dan posting hanya sekali.

**Kelemahan test sekarang:** `Callback_success_persists_provider_evidence_on_the_attempt_for_reuse_by_child_requests` (CallbackAndChildFinalizationTests.cs:98–126) secara eksplisit mengassert `IsOutcomeRecorded == false`. Ini menguji attempt yang belum selesai direkam, misalnya crash sebelum persist. Komentar “after a timeout” tidak merepresentasikan timeout normal yang telah diproses Core. Test juga hanya memanggil helper OriginalProviderReferences.From, belum benar-benar mengirim refund/reversal lewat processing service.

## RR3 — P2: Dokumentasi menutup temuan terlalu dini dan mengajarkan lock order yang salah

README sekarang menyatakan “All six are fixed” dan milestone 13 “Done”. Dengan RR1/RR2, klaim ini belum akurat. CLAUDE.md bagian provider callback mengajarkan mengambil lock transaction sekali tanpa membedakan child dan parent, sementara bagian finalization mengharuskan parent → child. Agen implementer berikutnya bisa mempertahankan regresi karena mengikuti instruksi tersebut.

Perbaikan dokumentasi yang diminta:
- Ubah status remediation menjadi implemented / awaiting architecture re-review, dengan RR1/RR2 open; pisahkan completion implementer dari reviewer sign-off.
- Tuliskan lock order callback parent → child dan jelaskan evidence outcome awal vs hasil callback lanjutan.
- Revisi ADR-026 yang mengklaim callback otomatis memperoleh data persistence: klaim hanya benar bila attempt belum mempunyai outcome.
- Hapus “Not pushed to GitHub pending review”: commit sudah tersedia di origin/main yang di-fetch reviewer.
- Perbaiki rujukan `review/RANSYS_Architecture_Review_f1fbefe.md` atau masukkan file tersebut: path tidak ditemukan dalam tracked checkout saat review ini.
- Gunakan nama proto aktual `docs/RANSYS_Provider_Adapter_v1.proto`; penyingkatan CLAUDE.md menjadi `RANSYS_Provider_Adapter_Contracts_v1.cs`/`.proto` dapat menyiratkan nama file proto yang tidak ada.
- Lampirkan output build/test atau CI artifact yang terikat SHA; jumlah passed sendiri tidak membuktikan skenario concurrency/evidence yang hilang.

Yang sudah membaik: solution layout, milestone M12a–g, posting-key examples, pemisahan pre-send/post-send DB failure, indeks ADR hingga 026, dan pemisahan ADR Proposed vs accepted. Tidak perlu mengulang perbaikan itu.

## Handoff perbaikan berikutnya

1. Prioritaskan RR1 (lock order child) dan RR2 (durable async evidence). Keduanya wajib sebelum sign-off.
2. Tambahkan tests yang membuktikan skenario sesungguhnya, bukan hanya perubahan method/helper. Test race lama tetap boleh disimpan sebagai stale-provider test.
3. Selaraskan CLAUDE.md, README.md dan ADR dengan status faktual. Jangan menandai finding closed hanya karena commit fix sudah ada.
4. Jalankan build/test .NET 10 dengan PostgreSQL test yang terisolasi, termasuk test baru, lalu kirim base/head SHA dan bukti hasilnya.
5. R3/R5 tidak perlu ditulis ulang tanpa temuan tambahan. R4 sync persistence juga dapat dipertahankan; integrasikan dengan model hasil asinkron yang diperbaiki.

**Gate tetap BLOCKED pada 1fc9d10. Kemajuan perbaikannya nyata, tetapi callback child dan late callback masih menghalangi sign-off.**
