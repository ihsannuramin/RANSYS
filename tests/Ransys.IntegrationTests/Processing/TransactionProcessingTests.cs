using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using Ransys.Adapter.Contracts.V1;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using Ransys.Testing;
using Ransys.Testing.PostgreSql;
using Ransys.TransactionCore.Children;
using Ransys.TransactionCore.Processing;
using static Ransys.IntegrationTests.CoreHarness;

namespace Ransys.IntegrationTests.Processing;

/// <summary>
/// M12d end to end on a real PostgreSQL server with scripted in-process adapters: durable session before the provider
/// call, provider call outside any DB transaction, recorded outcome + finalization, failover, children, idempotency and
/// the fail-closed dependency rule.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TransactionProcessingTests(PostgresDatabaseFixture db)
{
    private readonly ProcessingHarness _h = new(db);

    [Fact]
    public async Task Payment_success_reserves_then_posts_and_captures_fee_and_config_versions()
    {
        var s = await _h.NewScenario();
        await _h.AddFeeRule(s, "PAYMENT", "FIXED", 2_500m);
        s.Adapter.ThenSuccess(rrn: "RRN-777");

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m, merchantReference: "INV-9")));

        Assert.Equal((ProcessingStatus.Success, "0000", "Success"), (result.ProcessingStatus, result.ResponseCode, result.ResponseMessage));
        Assert.Equal(("INV-9", "RRN-777"), (result.References.MerchantReference, result.References.Rrn));
        Assert.False(result.IsReplay);
        var call = Assert.Single(s.Adapter.Calls);
        Assert.Equal(nameof(IProviderAdapter.PaymentAsync), call.Operation);
        Assert.Equal((result.TransactionId.Value, 100_000m, "IDR", s.ProductCode), (call.Request.RansysTransactionId, call.Request.Amount, call.Request.CurrencyCode, call.Request.ProductCode));

        var transaction = await _h.Core.Load(result.TransactionId);
        Assert.Equal(FinancialStatus.Posted, transaction.FinancialStatus);
        Assert.Equal(2_500m, transaction.Fees!.MerchantChargeTotal.Amount);
        Assert.Equal(await _h.FeeConfigVersion(), transaction.Configuration.FeeConfigVersionId);
        Assert.Equal(db.Seed.ConfigVersionId, transaction.Configuration.RoutingConfigVersionId);
        Assert.Equal(1, await _h.Journals($"TX:{result.TransactionId}:RESERVE"));
        Assert.Equal(1, await _h.Journals($"TX:{result.TransactionId}:POST"));
        Assert.Equal((897_500m, 897_500m, 0m), await _h.Balances(s.Wallet));
        var attempt = Assert.Single(await _h.Core.LoadAttempts(result.TransactionId));
        Assert.True(attempt.IsOutcomeRecorded);
    }

    [Fact]
    public async Task Provider_timeout_is_in_doubt_1002_with_the_hold_kept_and_no_failover()
    {
        var s = await _h.NewScenario(providers: 2);
        s.Adapter.ThenHang();

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        Assert.Equal((ProcessingStatus.InDoubt, "1002"), (result.ProcessingStatus, result.ResponseCode));
        var transaction = await _h.Core.Load(result.TransactionId);
        Assert.Equal(FinancialStatus.Reserved, transaction.FinancialStatus);
        Assert.Equal((1_000_000m, 900_000m, 100_000m), await _h.Balances(s.Wallet));
        Assert.Equal("IN_DOUBT", await _h.Core.Query<string>(
            "SELECT hold_reason FROM ledger.balance_reservations WHERE ransys_transaction_id = @id", new { id = result.TransactionId.Value }));
        var attempt = Assert.Single(await _h.Core.LoadAttempts(result.TransactionId));
        Assert.True(attempt.Outcome!.RequestSent);
        Assert.Equal(Domain.Attempts.TransportStatus.Timeout, attempt.Outcome.TransportStatus);
        Assert.Equal(0, s.Providers[1].Adapter.CallCount);
    }

    [Fact]
    public async Task Not_sent_on_the_primary_fails_over_to_the_secondary()
    {
        var s = await _h.NewScenario(providers: 2);
        s.Providers[0].Adapter.ThenNotSent();

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        Assert.Equal(ProcessingStatus.Success, result.ProcessingStatus);
        Assert.Equal((1, 1), (s.Providers[0].Adapter.CallCount, s.Providers[1].Adapter.CallCount));
        var transaction = await _h.Core.Load(result.TransactionId);
        Assert.Equal(s.Providers[1].Id, transaction.Routing!.CurrentProvider.ProviderId);
        Assert.Equal(s.Providers[0].Id, transaction.Routing.InitialProvider.ProviderId);
        Assert.Equal(1, transaction.Routing.FailoverCount);
        var attempts = await _h.Core.LoadAttempts(result.TransactionId);
        Assert.Equal(2, attempts.Count);
        Assert.True(attempts[0].ProvesRequestNotSent);
        Assert.Equal((900_000m, 900_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task All_routes_not_sent_fails_and_releases_5001()
    {
        var s = await _h.NewScenario(providers: 2);
        s.Providers[0].Adapter.ThenNotSent();
        s.Providers[1].Adapter.ThenNotSent();

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        Assert.Equal((ProcessingStatus.Failed, "5001"), (result.ProcessingStatus, result.ResponseCode));
        Assert.Equal(FinancialStatus.Released, (await _h.Core.Load(result.TransactionId)).FinancialStatus);
        Assert.Equal(1, await _h.Journals($"TX:{result.TransactionId}:RELEASE"));
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Missing_adapter_binding_is_not_sent_and_the_payment_fails_over()
    {
        var s = await _h.NewScenario(providers: 2);
        _h.Registry.Unregister(s.Providers[0].Id);

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        Assert.Equal(ProcessingStatus.Success, result.ProcessingStatus);
        Assert.Equal(1, s.Providers[1].Adapter.CallCount);
    }

    [Fact]
    public async Task No_route_fails_and_releases_5001_without_a_provider_call()
    {
        var s = await _h.NewScenario();
        await _h.Execute("UPDATE integration.provider_operational_state SET health_state = 'UNHEALTHY' WHERE provider_id = @id", new { id = s.Providers[0].Id.Value });

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        Assert.Equal((ProcessingStatus.Failed, "5001", "No route available"), (result.ProcessingStatus, result.ResponseCode, result.ResponseMessage));
        Assert.Equal(FinancialStatus.Released, (await _h.Core.Load(result.TransactionId)).FinancialStatus);
        Assert.Equal(0, s.Adapter.CallCount);
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Insufficient_balance_fails_4001_committed_without_a_provider_call()
    {
        var s = await _h.NewScenario(balance: 50_000m);

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        Assert.Equal((ProcessingStatus.Failed, "4001", "Insufficient balance"), (result.ProcessingStatus, result.ResponseCode, result.ResponseMessage));
        var transaction = await _h.Core.Load(result.TransactionId);
        Assert.Equal((FinancialStatus.None, ErrorCodes.InsufficientBalance), (transaction.FinancialStatus, transaction.ReasonCode));
        Assert.Equal(0, s.Adapter.CallCount);
        Assert.Empty(await _h.Core.LoadAttempts(result.TransactionId));
        Assert.Equal((50_000m, 50_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Frozen_wallet_fails_without_a_provider_call()
    {
        var s = await _h.NewScenario();
        await _h.Execute("UPDATE ledger.wallets SET status = 'FROZEN' WHERE wallet_id = @id", new { id = s.Wallet.Value });

        var result = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        Assert.Equal(ProcessingStatus.Failed, result.ProcessingStatus);
        Assert.Equal(ErrorCodes.WalletNotActive, (await _h.Core.Load(result.TransactionId)).ReasonCode);
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Idempotent_retry_returns_the_same_transaction_without_a_second_provider_call()
    {
        var s = await _h.NewScenario();
        var command = Payment(s, 100_000m);

        var first = Ok(await _h.Service.PayAsync(command));
        var retry = Ok(await _h.Service.PayAsync(command with { RequestTimestamp = DateTimeOffset.UtcNow.AddSeconds(5) }));

        Assert.Equal(first.TransactionId, retry.TransactionId);
        Assert.Equal((ProcessingStatus.Success, "0000", true), (retry.ProcessingStatus, retry.ResponseCode, retry.IsReplay));
        Assert.Equal(first.References.Rrn, retry.References.Rrn);
        Assert.Equal(1, s.Adapter.CallCount);
        Assert.Equal((900_000m, 900_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Concurrent_duplicates_create_one_transaction_one_reservation_and_one_provider_call()
    {
        var s = await _h.NewScenario();
        s.Adapter.Then(async (_, request, _) =>
        {
            await Task.Delay(100, CancellationToken.None);
            return ScriptedProviderAdapter.Success(request);
        });
        var command = Payment(s, 100_000m);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => _h.Service.PayAsync(command))));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Single(results.Select(r => r.Value.TransactionId).Distinct());
        Assert.Single(results, r => !r.Value.IsReplay);
        Assert.Equal(1, s.Adapter.CallCount);
        Assert.Equal(1, await _h.Journals($"TX:{results[0].Value.TransactionId}:RESERVE"));
        Assert.Equal(1, await _h.Core.Query<int>(
            "SELECT count(*)::int FROM core.transactions WHERE channel_id = @channel", new { channel = s.Channel.Value }));
        Assert.Equal((900_000m, 900_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Same_reference_with_a_different_payload_is_a_duplicate_conflict()
    {
        var s = await _h.NewScenario();
        var command = Payment(s, 100_000m);
        Ok(await _h.Service.PayAsync(command));

        var conflict = await _h.Service.PayAsync(command with { Amount = new MoneyInput(100_001m, "IDR") });

        Assert.Equal(ErrorCodes.DuplicateReferenceConflict, conflict.Error.Code);
        Assert.Equal(1, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Inquiry_calls_inquiry_without_amount_and_returns_provider_data()
    {
        var s = await _h.NewScenario();
        var data = ImmutableDictionary<string, JsonElement>.Empty.Add("billAmount", JsonSerializer.SerializeToElement("125000.00"));
        s.Adapter.ThenResult(r => ScriptedProviderAdapter.Success(r, data: data));

        var result = Ok(await _h.Service.InquireAsync(new InquiryCommand(
            s.Channel, s.Merchant, Reference(), null, s.ProductCode, new CustomerInput(CustomerId: "CUST-1"),
            new EndpointInput(EndpointType.Biller, "PLN-123"), DateTimeOffset.UtcNow)));

        Assert.Equal((ProcessingStatus.Success, TransactionType.Inquiry), (result.ProcessingStatus, result.TransactionType));
        Assert.Equal("125000.00", result.Data["billAmount"].GetString());
        var call = Assert.Single(s.Adapter.Calls);
        Assert.Equal(nameof(IProviderAdapter.InquiryAsync), call.Operation);
        Assert.Null(call.Request.Amount);
        Assert.Equal("PLN-123", call.Request.Destination["identifier"].GetString());
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Transfer_calls_transfer_and_posts()
    {
        var s = await _h.NewScenario();

        var result = Ok(await _h.Service.TransferAsync(new TransferCommand(
            s.Channel, s.Merchant, Reference(), null, s.ProductCode, new MoneyInput(250_000m, "IDR"),
            new EndpointInput(EndpointType.Merchant, "MRC-1"), new EndpointInput(EndpointType.BankAccount, "1234567890", "BANKX"),
            null, DateTimeOffset.UtcNow)));

        Assert.Equal(ProcessingStatus.Success, result.ProcessingStatus);
        Assert.Equal(nameof(IProviderAdapter.TransferAsync), Assert.Single(s.Adapter.Calls).Operation);
        Assert.Equal(FinancialStatus.Posted, (await _h.Core.Load(result.TransactionId)).FinancialStatus);
        Assert.Equal((750_000m, 750_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Reversal_child_reaches_the_original_provider_and_reverses_the_original()
    {
        var s = await _h.NewScenario(providers: 2);
        s.Adapter.ThenSuccess(providerReference: "ORIG-REF", rrn: "ORIG-RRN");
        var payment = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        var reversal = Ok(await _h.Service.ReverseAsync(new ReversalCommand(
            s.Channel, s.Merchant, Reference(), null, payment.TransactionId.Value, "customer cancelled", DateTimeOffset.UtcNow)));

        Assert.Equal((ProcessingStatus.Success, TransactionType.Reversal), (reversal.ProcessingStatus, reversal.TransactionType));
        Assert.NotEqual(payment.TransactionId, reversal.TransactionId);
        var call = s.Adapter.Calls[1];
        Assert.Equal(nameof(IProviderAdapter.ReversalAsync), call.Operation);
        Assert.Equal((reversal.TransactionId.Value, payment.TransactionId.Value), (call.Request.RansysTransactionId, call.Request.OriginalTransactionId));
        Assert.Equal(("ORIG-REF", "ORIG-RRN"), (call.Request.References.ProviderReference, call.Request.References.ProviderRrn));
        Assert.Equal(0, s.Providers[1].Adapter.CallCount);
        var original = await _h.Core.Load(payment.TransactionId);
        Assert.Equal((ProcessingStatus.Reversed, FinancialStatus.Reversed), (original.ProcessingStatus, original.FinancialStatus));
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(s.Wallet));
        Assert.Equal("customer cancelled", await _h.Core.Query<string>(
            "SELECT reason_description FROM core.transaction_state_history WHERE ransys_transaction_id = @id AND new_status = 'VALIDATED'",
            new { id = reversal.TransactionId.Value }));
    }

    [Fact]
    public async Task Refund_children_use_the_captured_pro_rata_fee_policy_and_the_cumulative_cap()
    {
        var s = await _h.NewScenario();
        await _h.AddFeeRule(s, "PAYMENT", "FIXED", 2_500m);
        var payment = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));
        await _h.Execute(
            "UPDATE core.transaction_fee_components SET refund_policy = 'PRO_RATA', refundable = true WHERE ransys_transaction_id = @id",
            new { id = payment.TransactionId.Value });

        var first = Ok(await _h.Service.RefundAsync(Refund(s, payment.TransactionId, 40_000m)));
        var tooMuch = await _h.Service.RefundAsync(Refund(s, payment.TransactionId, 60_000.01m));
        var rest = Ok(await _h.Service.RefundAsync(Refund(s, payment.TransactionId, 60_000m)));
        var afterFull = await _h.Service.RefundAsync(Refund(s, payment.TransactionId, 1m));

        Assert.Equal((ProcessingStatus.Success, TransactionType.Refund), (first.ProcessingStatus, first.TransactionType));
        Assert.Equal(ErrorCodes.RefundExceedsPosted, tooMuch.Error.Code);
        Assert.Equal(ProcessingStatus.Success, rest.ProcessingStatus);
        Assert.Equal(ErrorCodes.RefundNotAllowed, afterFull.Error.Code);
        Assert.Equal(3, s.Adapter.CallCount); // payment + two refunds; rejected refunds never reach the provider
        Assert.All(s.Adapter.Calls.Skip(1), c => Assert.Equal(nameof(IProviderAdapter.RefundAsync), c.Operation));
        var original = await _h.Core.Load(payment.TransactionId);
        Assert.Equal((ProcessingStatus.Refunded, FinancialStatus.Refunded), (original.ProcessingStatus, original.FinancialStatus));
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(s.Wallet)); // 41,000 + 61,500 returned
    }

    [Fact]
    public async Task Refund_in_another_currency_or_for_a_foreign_original_is_rejected()
    {
        var s = await _h.NewScenario();
        var other = await _h.NewScenario();
        var payment = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));

        var currency = await _h.Service.RefundAsync(Refund(s, payment.TransactionId, 1_000m) with { RefundAmount = new MoneyInput(1_000m, "USD") });
        var foreign = await _h.Service.RefundAsync(Refund(other, payment.TransactionId, 1_000m));
        var unknown = await _h.Service.ReverseAsync(new ReversalCommand(
            s.Channel, s.Merchant, Reference(), null, Guid.CreateVersion7(), "x", DateTimeOffset.UtcNow));

        Assert.Equal(ErrorCodes.CurrencyMismatch, currency.Error.Code);
        Assert.Equal((ProcessingErrorCodes.OriginalTransactionInvalid, "originalTransactionId"), (foreign.Error.Code, foreign.Error.Field));
        Assert.Equal(ProcessingErrorCodes.OriginalTransactionInvalid, unknown.Error.Code);
        Assert.Equal(1, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Void_child_calls_void_and_flags_the_original_without_moving_money()
    {
        var s = await _h.NewScenario();
        var payment = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));
        var command = new VoidCommand(s.Channel, s.Merchant, Reference(), null, payment.TransactionId.Value, "duplicate order", DateTimeOffset.UtcNow);

        var voided = Ok(await _h.Service.VoidAsync(command));
        var second = await _h.Service.VoidAsync(command with { ClientReference = Reference() });
        var replay = Ok(await _h.Service.VoidAsync(command));

        Assert.Equal((ProcessingStatus.Success, TransactionType.Void), (voided.ProcessingStatus, voided.TransactionType));
        Assert.Equal(nameof(IProviderAdapter.VoidAsync), s.Adapter.Calls[1].Operation);
        Assert.Equal(ChildTransactionService.VoidAlreadyActive, second.Error.Code);
        Assert.Equal((voided.TransactionId, true), (replay.TransactionId, replay.IsReplay));
        Assert.Equal(2, s.Adapter.CallCount);
        var original = await _h.Core.Load(payment.TransactionId);
        Assert.Equal((ProcessingStatus.Success, FinancialStatus.Posted, ReconciliationStatus.Exception),
            (original.ProcessingStatus, original.FinancialStatus, original.ReconciliationStatus));
        Assert.Equal((900_000m, 900_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Reused_child_reference_for_another_original_is_a_conflict_not_the_other_result()
    {
        var s = await _h.NewScenario();
        var first = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));
        var second = Ok(await _h.Service.PayAsync(Payment(s, 100_000m)));
        var reference = Reference();

        Ok(await _h.Service.ReverseAsync(new ReversalCommand(s.Channel, s.Merchant, reference, null, first.TransactionId.Value, "r", DateTimeOffset.UtcNow)));
        var reused = await _h.Service.ReverseAsync(new ReversalCommand(s.Channel, s.Merchant, reference, null, second.TransactionId.Value, "r", DateTimeOffset.UtcNow));

        Assert.Equal(ErrorCodes.DuplicateReferenceConflict, reused.Error.Code);
        Assert.Equal(ProcessingStatus.Success, (await _h.Core.Load(second.TransactionId)).ProcessingStatus);
    }

    [Fact]
    public async Task Transaction_detail_is_visible_only_to_the_owning_channel()
    {
        var s = await _h.NewScenario();
        s.Adapter.ThenSuccess(rrn: "RRN-1");
        var payment = Ok(await _h.Service.PayAsync(Payment(s, 100_000m, merchantReference: "INV-1")));

        var detail = await _h.Service.GetTransactionAsync(s.Channel, payment.TransactionId);
        var foreign = await _h.Service.GetTransactionAsync(new ChannelId(db.Seed.ChannelId), payment.TransactionId);

        Assert.NotNull(detail);
        Assert.Equal((TransactionType.Payment, ProcessingStatus.Success, FinancialStatus.Posted, "0000"),
            (detail.TransactionType, detail.ProcessingStatus, detail.FinancialStatus, detail.ResponseCode));
        Assert.Equal(("INV-1", "RRN-1"), (detail.References.MerchantReference, detail.References.Rrn));
        Assert.NotNull(detail.CompletedAt);
        Assert.Null(foreign);
    }

    [Fact]
    public async Task Database_unavailable_is_a_dependency_error_and_the_adapter_is_never_invoked()
    {
        var s = await _h.NewScenario();
        await using var unreachable = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "RANSYS_PG_UNREACHABLE",
            Username = "nobody",
            Timeout = 2,
        }.ConnectionString);
        var broken = new ProcessingHarness(db, unreachable);
        broken.Registry.Register(s.Providers[0].Id, s.Adapter);

        await Assert.ThrowsAsync<FinancialDependencyUnavailableException>(() => broken.Service.PayAsync(Payment(s, 100_000m)));
        await Assert.ThrowsAsync<FinancialDependencyUnavailableException>(() => broken.Service.GetTransactionAsync(s.Channel, new TransactionId(Guid.CreateVersion7())));

        Assert.Equal(0, s.Adapter.CallCount);
        Assert.Equal((1_000_000m, 1_000_000m, 0m), await _h.Balances(s.Wallet));
    }

    [Fact]
    public async Task Unknown_product_currency_and_bad_amount_are_validation_errors()
    {
        var s = await _h.NewScenario();

        var product = await _h.Service.PayAsync(Payment(s, 1m) with { ProductCode = "NO-SUCH-PRODUCT" });
        var currency = await _h.Service.PayAsync(Payment(s, 1m) with { Amount = new MoneyInput(1m, "XXX") });
        var scale = await _h.Service.PayAsync(Payment(s, 1.001m));
        var zero = await _h.Service.PayAsync(Payment(s, 0m));
        var metadata = await _h.Service.PayAsync(Payment(s, 1m) with
        {
            Metadata = new Dictionary<string, JsonElement> { ["pin"] = JsonSerializer.SerializeToElement("1234") },
        });

        Assert.Equal((ProcessingErrorCodes.ProductNotAvailable, "productCode"), (product.Error.Code, product.Error.Field));
        Assert.Equal((ProcessingErrorCodes.CurrencyNotSupported, "amount.currency"), (currency.Error.Code, currency.Error.Field));
        Assert.Equal((ErrorCodes.MoneyPrecisionExceedsScale, "amount.value"), (scale.Error.Code, scale.Error.Field));
        Assert.Equal(ErrorCodes.OutOfRange, zero.Error.Code);
        Assert.Equal(ErrorCodes.MetadataKeyInvalid, metadata.Error.Code);
        Assert.Equal(ErrorCategory.Validation, product.Error.Category);
        Assert.Equal(0, s.Adapter.CallCount);
    }

    [Fact]
    public async Task Merchant_specific_fee_rule_wins_and_percentage_rounds_half_away_from_zero()
    {
        var s = await _h.NewScenario();
        await _h.AddFeeRule(s, "PAYMENT", "FIXED", 1_000m);
        await _h.AddFeeRule(s, "PAYMENT", "PERCENTAGE", 0.01m, min: 1m, max: 50_000m, merchantSpecific: true);

        var result = Ok(await _h.Service.PayAsync(Payment(s, 333_350m)));   // 1 % = 3,333.50 exactly; then 333.50 → 3.335

        Assert.Equal(3_333.50m, (await _h.Core.Load(result.TransactionId)).Fees!.MerchantChargeTotal.Amount);
        var small = Ok(await _h.Service.PayAsync(Payment(s, 333.50m)));
        Assert.Equal(3.34m, (await _h.Core.Load(small.TransactionId)).Fees!.MerchantChargeTotal.Amount);
    }

    private static PaymentCommand Payment(Scenario s, decimal amount, string? merchantReference = null) =>
        new(s.Channel, s.Merchant, Reference(), null, s.ProductCode, new MoneyInput(amount, "IDR"),
            new CustomerInput(CustomerId: "CUST-1"), null, new EndpointInput(EndpointType.Biller, "PLN-1"), merchantReference, DateTimeOffset.UtcNow);

    private static RefundCommand Refund(Scenario s, TransactionId original, decimal amount) =>
        new(s.Channel, s.Merchant, Reference(), null, original.Value, new MoneyInput(amount, "IDR"), "partial refund", DateTimeOffset.UtcNow);

    private static string Reference() => $"REF-{Guid.CreateVersion7():N}";
}
