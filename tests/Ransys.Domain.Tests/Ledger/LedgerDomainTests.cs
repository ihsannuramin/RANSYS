using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Ledger;

public sealed class PostingKeyTests
{
    private static readonly TransactionId Tx = new(Guid.Parse("0192a000-0000-7000-8000-0000000000f1"));

    [Fact]
    public void Keys_follow_adr_001()
    {
        Assert.Equal("TX:0192a000-0000-7000-8000-0000000000f1:RESERVE", PostingKey.Reserve(Tx).Value);
        Assert.Equal("TX:0192a000-0000-7000-8000-0000000000f1:POST", PostingKey.Post(Tx).Value);
        Assert.Equal("TX:0192a000-0000-7000-8000-0000000000f1:RELEASE", PostingKey.Release(Tx).Value);
        Assert.Equal("TX:0192a000-0000-7000-8000-0000000000f1:REVERSAL_RELEASE", PostingKey.ReversalRelease(Tx).Value);
        Assert.Equal("TX:0192a000-0000-7000-8000-0000000000f1:REVERSAL:RV1", PostingKey.Reversal(Tx, "RV1").Value.Value);
        Assert.Equal("TX:0192a000-0000-7000-8000-0000000000f1:REFUND:RF1", PostingKey.Refund(Tx, "RF1").Value.Value);
        Assert.Equal("TOPUP:TP001", PostingKey.TopUp("TP001").Value.Value);
        Assert.Equal("ADJUSTMENT:ADJ001", PostingKey.Adjustment("ADJ001").Value.Value);
    }

    [Fact]
    public void Reference_is_required_and_key_fits_varchar_200()
    {
        Assert.Equal(ErrorCodes.Required, PostingKey.TopUp(" ").Error.Code);
        Assert.Equal(ErrorCodes.TooLong, PostingKey.TopUp(new string('x', 195)).Error.Code);
        Assert.Equal(PostingKey.MaxLength, PostingKey.TopUp(new string('x', 194)).Value.Value.Length);
    }
}

public sealed class JournalTests
{
    private static readonly Wallet W = TestWallet(available: 0m);

    [Fact]
    public void Balanced_journal_is_created_with_total()
    {
        var journal = Journal.Create(Draft(
            new JournalLine(LedgerAccounts.MerchantReserved(W), EntrySide.Debit, Rp(102_500m)),
            new JournalLine(LedgerAccounts.ProviderPayable(ProviderA, Idr), EntrySide.Credit, Rp(100_000m)),
            new JournalLine(LedgerAccounts.FeeRevenue(Idr), EntrySide.Credit, Rp(2_500m)))).Value;

        Assert.Equal(Rp(102_500m), journal.Total);
    }

    [Fact]
    public void Unbalanced_journal_is_rejected()
    {
        var result = Journal.Create(Draft(
            new JournalLine(LedgerAccounts.MerchantReserved(W), EntrySide.Debit, Rp(102_500m)),
            new JournalLine(LedgerAccounts.ProviderPayable(ProviderA, Idr), EntrySide.Credit, Rp(100_000m))));

        Assert.Equal(ErrorCodes.JournalUnbalanced, result.Error.Code);
    }

    [Fact]
    public void Single_line_zero_amount_and_mixed_currency_are_rejected()
    {
        var usdAccount = LedgerAccounts.CashClearing(Usd);

        Assert.Equal(ErrorCodes.JournalInvalid, Journal.Create(Draft(
            new JournalLine(LedgerAccounts.CashClearing(Idr), EntrySide.Debit, Rp(1m)))).Error.Code);
        Assert.Equal(ErrorCodes.JournalInvalid, Journal.Create(Draft(
            new JournalLine(LedgerAccounts.CashClearing(Idr), EntrySide.Debit, Rp(0m)),
            new JournalLine(LedgerAccounts.MerchantAvailable(W), EntrySide.Credit, Rp(0m)))).Error.Code);
        Assert.Equal(ErrorCodes.JournalInvalid, Journal.Create(Draft(
            new JournalLine(usdAccount, EntrySide.Debit, Rp(1m)),
            new JournalLine(LedgerAccounts.MerchantAvailable(W), EntrySide.Credit, Rp(1m)))).Error.Code);
    }

    [Fact]
    public void Account_codes_follow_adr_007()
    {
        Assert.Equal($"MERCHANT:{W.Id}:AVAILABLE", LedgerAccounts.MerchantAvailable(W).Code);
        Assert.Equal($"MERCHANT:{W.Id}:RESERVED", LedgerAccounts.MerchantReserved(W).Code);
        Assert.Equal($"SYSTEM:PROVIDER_PAYABLE:{ProviderA}:IDR-V1", LedgerAccounts.ProviderPayable(ProviderA, Idr).Code);
        Assert.Equal("SYSTEM:CASH_CLEARING:IDR-V1", LedgerAccounts.CashClearing(Idr).Code);
        Assert.Equal("SYSTEM:CASH_CLEARING:IDR-V2", LedgerAccounts.CashClearing(IdrV2).Code);
        Assert.Equal(AccountClass.Control, LedgerAccounts.AdjustmentClearing(Idr).AccountClass);
    }

    private static JournalDraft Draft(params JournalLine[] lines) => new(
        Guid.CreateVersion7(), PostingKey.Post(NewTransactionId()), LedgerOperationType.Post, null, null, null,
        "REF", "test", T0, T0, LedgerActor.System, null, lines);

    internal static Wallet TestWallet(decimal available, decimal reserved = 0m, WalletStatus status = WalletStatus.Active) =>
        Wallet.Rehydrate(
            new WalletId(Guid.CreateVersion7()), Merchant, null, Rp(available + reserved), Rp(available), Rp(reserved), status, 1).Value;
}

public sealed class WalletTests
{
    [Fact]
    public void Reserve_commit_and_release_keep_ledger_equal_available_plus_reserved()
    {
        var wallet = JournalTests.TestWallet(available: 1_000_000m);

        Assert.True(wallet.Reserve(Rp(102_500m)).IsSuccess);
        Assert.Equal((Rp(897_500m), Rp(102_500m), Rp(1_000_000m)), (wallet.AvailableBalance, wallet.ReservedBalance, wallet.LedgerBalance));

        Assert.True(wallet.CommitReserved(Rp(102_500m)).IsSuccess);
        Assert.Equal((Rp(897_500m), Rp(0m), Rp(897_500m)), (wallet.AvailableBalance, wallet.ReservedBalance, wallet.LedgerBalance));

        Assert.True(wallet.Reserve(Rp(97_500m)).IsSuccess);
        Assert.True(wallet.ReleaseReserved(Rp(97_500m)).IsSuccess);
        Assert.Equal((Rp(897_500m), Rp(0m), Rp(897_500m)), (wallet.AvailableBalance, wallet.ReservedBalance, wallet.LedgerBalance));
    }

    [Fact]
    public void Reserve_beyond_available_is_insufficient_balance_and_changes_nothing()
    {
        var wallet = JournalTests.TestWallet(available: 100_000m);

        var result = wallet.Reserve(Rp(100_000.01m));

        Assert.Equal(ErrorCodes.InsufficientBalance, result.Error.Code);
        Assert.Equal(ErrorCategory.Financial, result.Error.Category);
        Assert.Equal(Rp(100_000m), wallet.AvailableBalance);
    }

    [Fact]
    public void Debit_adjustment_never_goes_negative()
    {
        var wallet = JournalTests.TestWallet(available: 40_000m);

        Assert.Equal(ErrorCodes.InsufficientBalance, wallet.DebitAvailable(Rp(50_000m)).Error.Code);
        Assert.True(wallet.DebitAvailable(Rp(40_000m)).IsSuccess);
        Assert.True(wallet.AvailableBalance.IsZero);
    }

    [Fact]
    public void Frozen_wallet_blocks_new_debits_but_can_finalize_in_flight_reservations()
    {
        var wallet = JournalTests.TestWallet(available: 100m, reserved: 50m, status: WalletStatus.Frozen);

        Assert.Equal(ErrorCodes.WalletNotActive, wallet.Reserve(Rp(10m)).Error.Code);
        Assert.Equal(ErrorCodes.WalletNotActive, wallet.DebitAvailable(Rp(10m)).Error.Code);
        Assert.True(wallet.CommitReserved(Rp(50m)).IsSuccess);
    }

    [Fact]
    public void Inconsistent_projection_is_rejected_on_load()
    {
        var result = Wallet.Rehydrate(new WalletId(Guid.CreateVersion7()), Merchant, null, Rp(100m), Rp(60m), Rp(30m), WalletStatus.Active, 1);

        Assert.Equal(ErrorCodes.PersistedStateInvalid, result.Error.Code);
    }

    [Fact]
    public void Mixing_currencies_throws()
    {
        var wallet = JournalTests.TestWallet(available: 100m);

        Assert.Throws<global::Ransys.Domain.Monetary.CurrencyMismatchException>(() =>
            wallet.Reserve(global::Ransys.Domain.Monetary.Money.Create(1m, Usd).Value));
    }
}

public sealed class ReservationTests
{
    [Fact]
    public void Active_reservation_commits_or_releases_once()
    {
        var committed = Open();
        Assert.True(committed.Commit(T0).IsSuccess);
        Assert.Equal(ErrorCodes.ReservationNotActive, committed.Release(T0).Error.Code);
        Assert.Equal(ErrorCodes.ReservationNotActive, committed.Commit(T0).Error.Code);

        var released = Open();
        Assert.True(released.Release(T0).IsSuccess);
        Assert.Equal(ReservationStatus.Released, released.Status);
        Assert.Equal(ErrorCodes.ReservationNotActive, released.Commit(T0).Error.Code);
    }

    [Fact]
    public void In_doubt_only_changes_hold_reason()
    {
        var reservation = Open();

        Assert.True(reservation.ChangeHoldReason("IN_DOUBT").IsSuccess);

        Assert.Equal((ReservationStatus.Active, "IN_DOUBT"), (reservation.Status, reservation.HoldReason));
    }

    [Fact]
    public void Total_is_principal_plus_fee()
    {
        Assert.Equal(Rp(102_500m), Open().Total);
        Assert.Equal(
            ErrorCodes.ReserveAmountNotPositive,
            Reservation.Open(Guid.CreateVersion7(), new WalletId(Guid.CreateVersion7()), NewTransactionId(), Rp(0m), Rp(0m), "X", T0).Error.Code);
    }

    private static Reservation Open() => Reservation.Open(
        Guid.CreateVersion7(), new WalletId(Guid.CreateVersion7()), NewTransactionId(), Rp(100_000m), Rp(2_500m), "PAYMENT_PROCESSING", T0).Value;
}
