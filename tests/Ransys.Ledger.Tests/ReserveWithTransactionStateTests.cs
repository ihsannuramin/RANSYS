using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Ledger;
using Ransys.Domain.Transactions;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.ReferenceData;
using Ransys.Persistence.PostgreSql.Transactions;
using Ransys.Testing.PostgreSql;
using static Ransys.Ledger.Tests.LedgerHarness;

namespace Ransys.Ledger.Tests;

/// <summary>
/// SD-01 reserve step: transaction state (VALIDATED + RESERVED), reservation, balanced journal, wallet
/// projection, history and outbox commit in one database transaction, or not at all (ERD v1.1 §43).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReserveWithTransactionStateTests(PostgresDatabaseFixture db)
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    private readonly LedgerHarness _h = new(db);
    private readonly TransactionStore _transactions = new(new ReferenceDataStore());

    [Fact]
    public async Task Aggregate_reserve_and_ledger_reserve_commit_together()
    {
        var wallet = await _h.NewFundedWallet(1_000_000m);
        var transaction = await SavedValidatedPayment(100_000m);

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            var loaded = (await _transactions.GetAsync(session, transaction, RowLock.ForUpdate)).Value!;   // lock order 1
            var outcome = loaded.MarkReserved(Ctx("PAYMENT_PROCESSING")).Value;
            Assert.Equal(LedgerAction.Reserve, outcome.LedgerAction);

            var reserved = await _h.Service.ReserveAsync(session, new ReserveRequest(                      // lock order 2-4
                loaded.Id, wallet, loaded.Amount, loaded.Fees!.GuaranteedReserveFeeTotal, "PAYMENT_PROCESSING"));
            Assert.True(reserved.IsSuccess);
            Assert.True((await _transactions.UpdateAsync(session, loaded)).IsSuccess);
            await session.CommitAsync();
        }

        var after = await Load(transaction);
        Assert.Equal((ProcessingStatus.Validated, FinancialStatus.Reserved), (after.ProcessingStatus, after.FinancialStatus));
        Assert.Equal(Rp(102_500m), after.ReserveAmount);
        Assert.Equal((1_000_000m, 897_500m, 102_500m), await _h.Balances(wallet));
    }

    [Fact]
    public async Task Insufficient_balance_rolls_back_the_transaction_state_too()
    {
        var wallet = await _h.NewFundedWallet(50_000m);
        var transaction = await SavedValidatedPayment(100_000m);

        await using (var session = await PostgresSession.BeginAsync(db.DataSource))
        {
            var loaded = (await _transactions.GetAsync(session, transaction, RowLock.ForUpdate)).Value!;
            loaded.MarkReserved(Ctx("PAYMENT_PROCESSING"));

            var reserved = await _h.Service.ReserveAsync(session, new ReserveRequest(
                loaded.Id, wallet, loaded.Amount, loaded.Fees!.GuaranteedReserveFeeTotal, "PAYMENT_PROCESSING"));

            Assert.Equal(ErrorCodes.InsufficientBalance, reserved.Error.Code);
            await session.RollbackAsync(); // fail closed: nothing about this reserve attempt is persisted
        }

        var after = await Load(transaction);
        Assert.Equal((ProcessingStatus.Validated, FinancialStatus.None), (after.ProcessingStatus, after.FinancialStatus));
        Assert.Equal((50_000m, 50_000m, 0m), await _h.Balances(wallet));
        Assert.Null(await _h.ReservationState(transaction));
    }

    private static TransitionContext Ctx(string reason) => TransitionContext.Create(reason, ChangeSource.Core, T0).Value;

    private async Task<TransactionId> SavedValidatedPayment(decimal amount)
    {
        var id = new TransactionId(Guid.CreateVersion7());
        var reference = $"INV-{id.Value:N}";
        var merchant = new MerchantId(db.Seed.MerchantId);
        var channel = new ChannelId(db.Seed.ChannelId);
        var product = new ProductId(db.Seed.ProductId);
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(
            merchant, channel, TransactionType.Payment, product, null, null, Rp(amount), reference));

        var transaction = Transaction.Create(new TransactionDraft(
            TransactionIdentity.Create(id, reference, null, fingerprint).Value,
            TransactionType.Payment, merchant, channel, product, Rp(amount), null, null, null,
            TransactionReferences.Create(reference).Value, ExtensionMetadata.Empty, T0)).Value;
        var fees = FeeComponents.Create(
            [FeeComponent.Create(FeeComponentType.MerchantServiceFee, Rp(2_500m), Rp(2_500m), FeeBeneficiary.Create("RANSYS").Value, FeeRefundPolicy.None, 1).Value],
            Idr).Value;
        transaction.Validate(fees, TransactionConfigurationSnapshot.None, Ctx("VALIDATION_OK"));

        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        Assert.True((await _transactions.InsertAsync(session, transaction)).IsSuccess);
        await session.CommitAsync();
        return id;
    }

    private async Task<Transaction> Load(TransactionId id)
    {
        await using var session = await PostgresSession.BeginAsync(db.DataSource);
        return (await _transactions.GetAsync(session, id)).Value!;
    }
}
