using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Ledger;

/// <summary>ADR-016 wallet status semantics.</summary>
public sealed class WalletStatusTests
{
    [Fact]
    public void Frozen_blocks_new_consumption_but_finalizes_existing_transactions()
    {
        var wallet = JournalTests.TestWallet(available: 100m, reserved: 60m);
        Assert.True(wallet.Freeze().IsSuccess);

        Assert.Equal(ErrorCodes.WalletNotActive, wallet.Reserve(Rp(1m)).Error.Code);
        Assert.Equal(ErrorCodes.WalletNotActive, wallet.DebitAvailable(Rp(1m)).Error.Code);
        Assert.True(wallet.CommitReserved(Rp(40m)).IsSuccess);   // payment success
        Assert.True(wallet.ReleaseReserved(Rp(20m)).IsSuccess);  // failure / reversal release
        Assert.True(wallet.CreditAvailable(Rp(10m)).IsSuccess);  // refund, compensating reversal, top-up
    }

    [Fact]
    public void Unfreeze_restores_normal_operation_and_both_are_idempotent()
    {
        var wallet = JournalTests.TestWallet(available: 100m);

        Assert.True(wallet.Freeze().IsSuccess);
        Assert.True(wallet.Freeze().IsSuccess);
        Assert.True(wallet.Unfreeze().IsSuccess);
        Assert.True(wallet.Unfreeze().IsSuccess);

        Assert.Equal(WalletStatus.Active, wallet.Status);
        Assert.True(wallet.Reserve(Rp(1m)).IsSuccess);
    }

    [Fact]
    public void Close_requires_zero_balances()
    {
        Assert.Equal(ErrorCodes.WalletNotEmpty, JournalTests.TestWallet(available: 0.01m).Close(false).Error.Code);
        Assert.Equal(ErrorCodes.WalletNotEmpty, JournalTests.TestWallet(available: 0m, reserved: 5m).Close(false).Error.Code);
    }

    [Fact]
    public void Close_requires_no_unresolved_financial_activity()
    {
        var wallet = JournalTests.TestWallet(available: 0m);

        Assert.Equal(ErrorCodes.WalletHasUnresolvedTransactions, wallet.Close(hasUnresolvedFinancialActivity: true).Error.Code);
        Assert.Equal(WalletStatus.Active, wallet.Status);
    }

    [Fact]
    public void Closed_is_terminal_and_blocks_everything()
    {
        var wallet = JournalTests.TestWallet(available: 0m, status: WalletStatus.Frozen);
        Assert.True(wallet.Close(false).IsSuccess);
        Assert.True(wallet.Close(false).IsSuccess); // idempotent

        Assert.Equal(ErrorCodes.WalletClosed, wallet.Unfreeze().Error.Code);
        Assert.Equal(ErrorCodes.WalletClosed, wallet.Freeze().Error.Code);
        Assert.Equal(ErrorCodes.WalletClosed, wallet.Reserve(Rp(1m)).Error.Code);
        Assert.Equal(ErrorCodes.WalletClosed, wallet.DebitAvailable(Rp(1m)).Error.Code);
        Assert.Equal(ErrorCodes.WalletClosed, wallet.CreditAvailable(Rp(1m)).Error.Code);
        Assert.Equal(ErrorCodes.WalletClosed, wallet.ReleaseReserved(Rp(1m)).Error.Code);
        Assert.Equal(WalletStatus.Closed, wallet.Status);
    }
}
