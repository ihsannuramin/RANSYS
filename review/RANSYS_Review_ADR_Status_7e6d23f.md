# RANSYS — Re-review dan status ADR-022/023/024

Tanggal: 27 September 2026 UTC
Repository: https://github.com/ihsannuramin/RANSYS
HEAD: `7e6d23f3af9addfec6205fe58b8a900125f82d8d`
Baseline review sebelumnya: `2ff84776caf7c3b966ab2ea989d6dd3cec5bac84`
Commit implementasi terbaru: `72e52bd`; HEAD merekonsiliasi dokumentasi.

## Keputusan review

**PASS WITH FOLLOW-UPS untuk perbaikan V1–V3 berdasarkan pemeriksaan statis.** Ketiga temuan sebelumnya sudah ditangani di kode dan dilengkapi regression tests yang sesuai. Tidak ditemukan blocker baru dalam perubahan terarah yang diperiksa. Ini bukan sertifikasi production readiness atau konfirmasi bahwa test telah dijalankan ulang oleh reviewer.

Scope: diff baseline → HEAD, CLAUDE.md, README.md, ADR-022/023/024 dan revisi ADR-027, alur finalization/evidence, child provider request, refund domain/core/ledger, serta test terkait. Source tidak diubah oleh reviewer.

## Penutupan temuan sebelumnya

| ID | Status | Bukti implementasi dan coverage |
| --- | --- | --- |
| V1 — identitas provider dibandingkan secara collapsed | Addressed secara statis | `Transaction.MergeLatestProviderResult` membandingkan reference, STAN, RRN masing-masing melalui `MergeIdentityField`. Null mempertahankan nilai lama; field kosong dapat diperkaya; dua nilai berbeda pada field yang sama menjadi konflik. Shared reference tidak lagi menyembunyikan perubahan RRN. Domain regression tests ditambahkan. |
| V2 — konflik evidence hilang sebagai duplicate biasa | Addressed secara statis | `TransactionFinalizationService` menangani `ConflictingIdentity` dengan `RecordProviderEvidenceConflict`, persist, event rekonsiliasi bila ada perubahan, dan hasil konflik. Test `Conflicting_identity_report_is_recorded_as_a_durable_recon_exception_and_repeats_idempotently` memeriksa persist/reload, accepted evidence, event, dan pengulangan tanpa ledger/event ganda. |
| V3 — refund child memakai reference pending lama | Addressed secara statis | `ProviderRequestFactory.OriginalProviderReferences.From` menggunakan projection final yang ada sekalipun seluruh reference kosong. Fallback attempt hanya untuk original tanpa projection. Integration tests memeriksa request aktual ke scripted adapter untuk referenceless success dan legacy fallback. |

Lokasi utama: `src/Ransys.Domain/Transactions/Transaction.cs`, `src/Ransys.TransactionCore/Finalization/TransactionFinalizationService.cs`, `src/Ransys.TransactionCore/Providers/ProviderRequestFactory.cs`, `tests/Ransys.IntegrationTests/Providers/CallbackAndChildFinalizationTests.cs`.

## ADR-022 — API request authentication

**Kebijakan interim sudah diimplementasikan; autentikasi produksi belum selesai. Status keputusan masih Proposed.**

Kode menyediakan abstraction autentikasi/signature/replay, middleware, pemeriksaan digest/timestamp/nonce/channel di Development/Test, guard konfigurasi yang menghentikan host bila development authentication diaktifkan di environment lain, serta fail-closed default. `FailClosedRequestAuthenticationService` menolak request merchant API; `FailClosedSignatureVerifier` mengembalikan false.

Bukti: `src/Ransys.Api/Security/Abstractions.cs`, `src/Ransys.Api/Composition/ServiceRegistration.cs`, `tests/Ransys.Api.Tests/SecurityTests.cs`. Test mencakup production rejection, invalid environment, replay, timestamp, digest, dan identity.

Belum selesai: signature profile dan verifier nyata, registry/rotation key dan certificate, binding mTLS ke client, replay store bersama yang tahan restart, serta aturan authorization/403 yang masih menunggu keputusan. Penolakan seluruh traffic merchant di production adalah perilaku sengaja, bukan regression terbaru.

**Penilaian:** implementasi sesuai ADR fail-closed dapat dinyatakan selesai dalam scope interim. Jangan menandai production security selesai atau membuka merchant traffic sebelum pekerjaan lanjutan di atas diverifikasi.

## ADR-023 — refund child completes original

**Sudah diimplementasikan pada domain, use case, dan ledger integration. Status keputusan masih Proposed.**

Original tidak masuk REFUND_PENDING saat child berjalan. Setelah refund child sukses, finalization menghitung refund kumulatif dan fee dari komponen original, memanggil `PostRefundAsync`, lalu `ApplyRefundCompleted` pada original dalam transaksi database yang sama dengan urutan lock parent → child. Original menjadi PARTIALLY_REFUNDED atau REFUNDED; child gagal tidak menyelesaikan original. Guard pembuatan child memeriksa kelayakan original, currency, dan cap refund child non-failed.

Bukti: `src/Ransys.TransactionCore/Children/ChildTransactionService.cs`, `TransactionFinalizationService.ApplyRefundToOriginalAsync`, domain `Transaction.AuthorizeRefund` / `ApplyRefundCompleted`; domain tests `RefundAndVoidTests.cs` serta integration tests `CallbackAndChildFinalizationTests.cs` untuk refund kumulatif, failed child, fee, dan duplicate finalization.

Follow-up dokumentasi: baris status ADR masih mengatakan use case akan menyusul pada M12d. Pernyataan itu tertinggal dari kode yang sudah terintegrasi. Perbarui deskripsi implementasi tanpa otomatis mengubah keputusan menjadi Accepted.

## ADR-024 — refund authorization

**Kontrak otorisasi dan jalur merchant refund sudah diimplementasikan. Status keputusan masih Proposed.**

`RefundAuthorization` memisahkan `ApprovedRequest(ApprovalRequestId)` dari `MerchantApiRequest(RefundTransactionId, ChannelId, ClientReference)`. Ledger menolak authorization kosong/invalid; merchant authorization harus menunjuk refund child yang sama, berbeda dari original, dan memiliki client reference. Finalization membentuk merchant authorization setelah suksesnya refund child dalam transaksi yang sama; tidak memalsukan approval id manual.

Bukti: `src/Ransys.Ledger/LedgerContracts.cs`, `LedgerPostingService.AuthorizeRefund`, `TransactionFinalizationService.ApplyRefundToOriginalAsync`, serta test `Merchant_api_refund_is_authorized_only_by_its_own_refund_child` dalam `tests/Ransys.Ledger.Tests/LedgerPostingServiceTests.cs`.

Batas kepercayaan penting: pada ledger, bentuk `ApprovedRequest` hanya memeriksa GUID nonempty; belum membuktikan approval tersimpan, berstatus approved, atau maker berbeda dari checker. Ledger juga tidak secara mandiri memvalidasi kepemilikan channel merchant melalui database: jalur ini mempercayai caller Transaction Core. Karena itu, implementasi ADR ini bukan bukti workflow Backoffice maker-checker lengkap. Jalur merchant produksi juga bergantung pada penyelesaian ADR-022.

**Penilaian:** sesuai scope kontrak ADR-024 saat ini. Sebelum endpoint manual Backoffice dibuka, wajib hubungkan approval yang benar-benar tervalidasi dan uji penolakan fake/unapproved approval serta maker = checker.

## Verifikasi dan batas kesimpulan

- Working tree bersih pada pemeriksaan; diff whitespace check untuk src/tests/docs/CLAUDE.md/README.md tidak melaporkan masalah.
- README melaporkan `dotnet build Ransys.sln`: 0 warnings/errors, dan `dotnet test Ransys.sln` menggunakan PostgreSQL: **1.471 passed, 0 failed, 0 skipped**, delapan test projects pada `72e52bd`.
- README juga melaporkan tujuh regression tests baru gagal sebelum fix dan lulus setelah fix. Reviewer memeriksa source test terkait; klaim eksekusi tersebut belum diverifikasi ulang secara independen.
- Environment reviewer tidak memiliki `dotnet`; build/test runtime tidak dijalankan dalam review ini. Untuk gate runtime gunakan hasil CI pada SHA yang sama atau jalankan ulang build dan suite dengan PostgreSQL.
- Ketiga ADR tetap **Proposed**. Implementasi dan penerimaan keputusan merupakan dua status terpisah; README sudah menyatakan pending product-owner acceptance.

## Urutan pekerjaan berikutnya untuk Claude Code

1. Reconcile deskripsi implementasi ADR-023 dengan kode; catat status implementasi interim vs production pada ADR-022, dan contract vs workflow Backoffice pada ADR-024.
2. Sertakan bukti build/test pada SHA final. Jangan mengganti status ADR menjadi Accepted tanpa keputusan pemilik arsitektur/produk.
3. Untuk target merchant production, selesaikan keputusan signature profile dahulu, lalu implementasikan verifier, identity/key/certificate binding dan shared replay protection beserta integration tests.
4. Untuk target manual Backoffice, implementasikan validasi approval tersimpan, actor authorization, maker/checker separation, dan audit sebelum jalur tersebut dapat memanggil posting.

Estimasi perencanaan sebelumnya tetap sekitar **40% scope PRD keseluruhan** dan **85% core + API**. Tidak ada dasar menaikkan angka itu hanya karena perbaikan correctness terbaru: tidak ada scope produk besar baru dalam diff ini. Angka tersebut bukan ukuran production readiness, persentase test coverage, maupun prediksi waktu selesai.
