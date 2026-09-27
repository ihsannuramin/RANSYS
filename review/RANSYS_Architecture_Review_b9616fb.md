# RANSYS Architecture Review — b9616fb

Tanggal: 27 September 2026, Asia/Jakarta

Base: `1fc9d10caf78d283d2ef1d4fb8f89bc1a3602702`

Head: `b9616fbbc2bd219f1d54526f8ba9479fbf2f755c` — origin/main saat fetch.

**Gate: BLOCKED. RR1 lock order addressed secara static; RR2 evidence correctness belum selesai.**

## Scope dan bukti

Review perbaikan `a2ce9f6`, dokumentasi `b9616fb`, diff 21 file dari baseline, ADR-027, finalization/domain transitions, persistence/readers, callback dan regression tests. Review tidak mengubah source code atau melakukan push.

Runtime dotnet tetap tidak tersedia. Build/test dan reproduksi concurrency belum dijalankan reviewer. README melaporkan 1.449 passed, 0 failed/skipped pada a2ce9f6 dan concurrency test diulang 8 kali; angka ini belum diverifikasi independen. Tidak ditemukan tracked .trx/build/test log dalam pencarian checkout. `git diff --check` melaporkan trailing whitespace pada file review lama yang baru dimasukkan repo; itu Markdown hard breaks dari laporan, bukan blocker kode.

## Status review sebelumnya

| Item | Status sekarang |
|---|---|
| RR1: child → parent deadlock | Addressed secara static: sink tidak lagi mengunci; finalization mengunci parent → child dan memvalidasi provider setelah lock |
| RR2: late callback evidence | Partial: TIMEOUT/IN_DOUBT → callback SUCCESS kini memiliki projection durable; lifecycle projection belum benar untuk konflik, hasil sync lanjutan dan NoChange |
| RR3: dokumentasi | Partial: file review dan nama proto dibenahi; klaim closure/evidence dan status push masih perlu diselaraskan |
| R3: replay snapshot | Perbaikan sebelumnya tetap dipertahankan; tidak dibuka ulang oleh review ini |
| R5: recovery outbox | Perbaikan sebelumnya tetap dipertahankan; tidak dibuka ulang oleh review ini |

## T1 — P1: Hasil yang bertentangan menimpa evidence bisnis authoritative

Lokasi: `src/Ransys.TransactionCore/Finalization/TransactionFinalizationService.cs:195–214`; `src/Ransys.Domain/Transactions/Transaction.cs:587–608,407–419`; reader `Providers/ProviderRequestFactory.cs:56–60`.

Finalization hanya menghentikan pemrosesan untuk TransitionKind.NoChange. ConflictRecorded tetap masuk ke RecordLatestProviderResult dan menimpa seluruh projection. CompleteFailure pada transaksi SUCCESS menghasilkan ConflictRecorded tanpa mengganti status SUCCESS/POSTED.

Skenario:
1. Callback SUCCESS menyimpan reference A, RRN A dan data hasil sukses di projection.
2. Callback FAILED yang bertentangan membawa reference B/data berbeda, atau reference/data kosong.
3. Status transaksi tetap SUCCESS/POSTED dan reconciliation EXCEPTION, tetapi projection berubah menjadi evidence FAILED.
4. GET/replay/child request memprioritaskan projection itu. Reference operasi refund/reversal dapat berubah menjadi B, atau hilang jika fallback attempt hanya berisi timeout. Replay dapat menampilkan data failure bersama status SUCCESS.

Ini bukan bukti jurnal langsung rusak; masalahnya evidence konflik dipromosikan menjadi sumber data untuk operasi finansial lanjutan. Penyimpanan konflik sebagai bukti investigasi memang diperlukan, tetapi berbeda dari penggantian hasil authoritative.

Perbaikan: pisahkan evidence yang diterima dari hasil yang dipilih sebagai authoritative. ConflictRecorded harus mempertahankan accepted result dan menyimpan laporan konflik untuk investigasi. Definisikan precedence/finality dan korelasi provider/attempt; jangan gunakan “last arrival wins” untuk semua hasil. Revisi ADR-027 yang mengatur always-overwritable projection karena reader menggunakannya sebagai authoritative, bukan sekadar log laporan terakhir.

Acceptance test: callback SUCCESS(A,dataA) → FAILED(B,dataB), lalu reload, GET, replay dan refund/reversal. Accepted references/data harus tetap A/dataA, reconciliation EXCEPTION, evidence konflik dapat ditelusuri, dan tidak ada posting tambahan. Uji konflik tanpa reference/data dan konflik berulang.

## T2 — P1: Evidence final masih hilang pada NoChange dan jalur sync tidak memperbarui projection

Lokasi: `Finalization/TransactionFinalizationService.cs:196–199` (return sebelum projection); `Processing/TransactionProcessingService.cs` RecordInSessionAsync (command sync tidak membawa Evidence), replay baris 714; `Providers/ProviderRequestFactory.cs:56–60`.

Ada dua jalur saling terkait:

A. Sync SUCCESS sudah tercatat dan transaksi SUCCESS. Callback SUCCESS berikutnya membawa reference/RRN/data lengkap. CompleteSuccess menghasilkan NoChange, sehingga finalization kembali sebelum menyimpan projection. Jika attempt sudah tercatat, tidak ada tempat lain menyimpan evidence callback ini.

B. Callback PENDING datang lebih dulu dan menyimpan projection data/reference sementara serta outcome attempt. Kemudian sync SUCCESS menyelesaikan transaksi; command sync tidak membawa Evidence sehingga projection tetap PENDING. Attempt yang sudah tercatat juga tidak ditimpa. Respons sync pertama bisa memakai data final dari memori, tetapi replay/child kembali membaca data sementara. Bahkan callback SUCCESS lanjutan tidak memperbaikinya karena status sudah SUCCESS dan menghasilkan NoChange.

Dengan demikian klaim “later/final evidence tidak pernah hilang lagi” belum berlaku. Masalah ini tidak membutuhkan database failure; urutan callback/sync yang normal sudah cukup.

Perbaikan: pisahkan keputusan perubahan state/ledger dari pembaruan evidence yang sah. NoChange pada status tidak selalu berarti tidak ada evidence baru. Izinkan enrichment final yang terverifikasi tanpa mem-post ulang atau merusak histori attempt. Jalur sync juga harus memberi evidence ke layanan yang sama dan mengikuti precedence yang sama. Tentukan dedup/correlation serta kebijakan konflik; jangan sekadar memindahkan unconditional overwrite sebelum return NoChange.

Acceptance tests:
- Sync SUCCESS tanpa reference lengkap → callback SUCCESS dengan reference/data lengkap; evidence bertahan setelah reload dan digunakan child request.
- Callback PENDING(temp) → sync SUCCESS(final) → replay; replay/GET/child harus menggunakan final.
- Ulangi callback identik, stale PENDING dan conflicting FAILED; status, ledger dan accepted evidence tetap konsisten.
- Gunakan processing service dan scripted adapter untuk interleaving nyata, bukan hanya helper finalization.

## T3 — P2: POST replay belum membaca STAN/RRN dari projection baru

Lokasi: `Processing/TransactionProcessingService.cs:708–715,946–960`.

Replay memilih Data dari LatestProviderResult, tetapi masih mengirim prepared.LatestOutcome ke Build. Build mengambil ProviderStan/ProviderRrn dari outcome lama dan transaction.References; tidak membaca LatestProviderResult. GET sudah diubah menggunakan COALESCE terhadap projection.

Skenario: outcome TIMEOUT tanpa RRN → callback SUCCESS dengan RRN baru → GET menampilkan RRN baru, tetapi POST replay request yang sama tidak menampilkan RRN tersebut. Test baru memeriksa GET dan helper child, belum menguji public POST replay.

Perbaikan: gunakan satu pemilihan accepted result untuk data dan references di semua readers (GET, POST replay, child). Lakukan fallback per field hanya sesuai kebijakan yang tidak mencampur hasil yang berbeda.

Acceptance test: timeout melalui processing → SUCCESS callback dengan STAN/RRN/data → ulangi POST yang sama. Assert id, status, references dan business data konsisten dengan GET; provider call tetap satu.

## T4 — P2: Concurrency test dapat melewatkan deadlock yang ditangkap sink

Lokasi: `tests/Ransys.IntegrationTests/Providers/CallbackAndChildFinalizationTests.cs:265–292`; `Providers/ProviderCallbackSink.cs` catch DbException.

Test baru mengabaikan ProviderCallbackAck dan Result<FinalizationResult>, lalu hanya mengecek exception bag kosong serta final state/posting. Sink menangkap DbException (termasuk deadlock) dan mengembalikan Accepted=false/TemporarilyUnavailable. Jika callback menjadi korban deadlock dan sync sukses, assertion sekarang tetap dapat lulus. Tidak ada barrier untuk memaksa interleaving atau bounded wait; pengulangan 8× tidak memperbaiki oracle yang mengabaikan kegagalan.

Ini gap verifikasi, bukan klaim bahwa lock order baru masih memiliki deadlock lama. Dari kode, perbaikan RR1 sudah sesuai parent → child.

Perbaikan: assert callback Accepted dan hasil sync IsSuccess, tangkap hasil fail-closed sebagai kegagalan test, gunakan instrumentasi/barrier dan timeout. Cakup reversal lewat fixture ReversalService yang benar atau jelaskan coverage lock bersama dengan test reversal terpisah. Uji versi sebelum fix bila praktis untuk memastikan regression test benar-benar mendeteksi bug.

## Dokumentasi / ADR follow-ups

- README mengakui implementer completion bukan reviewer sign-off; itu sudah tepat. Namun “Round 1 fixed all six” tidak sesuai hasil review sebelumnya. Ganti menjadi attempted remediation dan tampilkan status temuan faktual.
- README masih menyatakan a2ce9f6 local-only/not pushed; reviewer memperoleh commit itu dari origin/main. Hapus status push yang cepat usang atau cantumkan sebagai keadaan pada waktu tertentu.
- ADR-027 consequences menutup RR2 terlalu luas. Persisted projection bukan otomatis model evidence yang benar: T1/T2 menunjukkan hasil yang bertentangan dapat mengganti accepted evidence dan hasil valid dapat diabaikan.
- Perbarui CLAUDE.md agar tidak menginstruksikan unconditional last-arrival overwrite; tuliskan accepted-result precedence, enrichment NoChange dan penyimpanan konflik setelah desain diputuskan.
- File review lama sekarang tersedia di repo; jangan mengubah isi historisnya untuk memberi kesan bahwa commit lama telah lolos.

## Arah perbaikan dan gate berikutnya

Pertahankan refactor lock order dan immutable attempt outcomes. Fokus pada satu aturan pemilihan accepted provider result yang dipakai sync/callback/readers, terpisah dari audit evidence masuk. Tambahkan regression tests T1–T4, kemudian jalankan build/test dengan PostgreSQL terisolasi dan berikan hasil yang terikat SHA.

T1/T2 menghalangi sign-off. T3 perlu ditutup dalam scope replay yang sedang diperbaiki, dan T4 harus diperbaiki agar klaim concurrency dapat dinilai. Ini kelanjutan scope callback/evidence sebelumnya, bukan permintaan memperluas fitur.

**Status akhir: BLOCKED pada b9616fb. RR1 lock order sudah addressed secara static; RR2 belum dapat ditutup.**
