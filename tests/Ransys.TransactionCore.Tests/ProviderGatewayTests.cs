using System.Collections.Immutable;
using System.Text.Json;
using Ransys.Adapter.Contracts.V1;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Testing;
using Ransys.TransactionCore.Providers;
using DomainTransport = Ransys.Domain.Attempts.TransportStatus;
using ProviderOutcome = Ransys.Adapter.Contracts.V1.ProviderOutcome;
using V1Transport = Ransys.Adapter.Contracts.V1.TransportStatus;

namespace Ransys.TransactionCore.Tests;

/// <summary>M12c provider gateway without a database: result interpretation, dispatch, timeouts and request building.</summary>
public sealed class ProviderGatewayTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;
    private static readonly ProviderReference ProviderA =
        ProviderReference.Create(new ProviderId(Guid.CreateVersion7()), "BANK_A", "ransys-adapter-bank-a").Value;

    // ---------- ProviderResultInterpreter ----------

    [Fact]
    public void Definitive_success_is_success_with_the_adapter_code_and_references()
    {
        var request = Request(TransactionType.Payment);
        var result = ScriptedProviderAdapter.Success(request, providerReference: "P-1", rrn: "RRN-1");

        var interpreted = ProviderResultInterpreter.Interpret(result, Now);

        Assert.Equal(AttemptResolutionKind.Success, interpreted.Resolution);
        Assert.Equal("0000", interpreted.ResponseCode);
        Assert.False(interpreted.WasNormalized);
        Assert.True(interpreted.Outcome.RequestSent);
        Assert.Equal(DomainTransport.Response, interpreted.Outcome.TransportStatus);
        Assert.Equal(("P-1", "RRN-1"), (interpreted.Outcome.ProviderReference, interpreted.Outcome.ProviderRrn));
    }

    [Theory]
    [InlineData(ProviderOutcome.Failed, ResultFinality.Definitive, AttemptResolutionKind.Failed)]
    [InlineData(ProviderOutcome.Pending, ResultFinality.NonFinal, AttemptResolutionKind.Pending)]
    public void Provider_statements_with_a_response_map_to_their_resolution(ProviderOutcome outcome, ResultFinality finality, AttemptResolutionKind expected)
    {
        var result = ScriptedProviderAdapter.Response(Request(TransactionType.Payment), outcome, finality, "1001", "51", null, null, null);

        Assert.Equal(expected, ProviderResultInterpreter.Interpret(result, Now).Resolution);
    }

    [Fact]
    public void Proven_not_sent_is_not_sent_and_allows_failover()
    {
        var interpreted = ProviderResultInterpreter.Interpret(ScriptedProviderAdapter.NotSent(Request(TransactionType.Payment)), Now);

        Assert.Equal(AttemptResolutionKind.NotSent, interpreted.Resolution);
        Assert.True(interpreted.Outcome.ProvesRequestNotSent);
    }

    [Theory]
    [InlineData(V1Transport.Timeout, DomainTransport.Timeout)]
    [InlineData(V1Transport.ProtocolError, DomainTransport.ProtocolError)]
    [InlineData(V1Transport.ConnectionError, DomainTransport.ConnectionError)]
    [InlineData(V1Transport.Sent, DomainTransport.Sent)]
    public void Ambiguous_results_are_in_doubt_1002_and_possibly_sent(V1Transport transport, DomainTransport expected)
    {
        var result = ProviderResults.InDoubt(transport, Error("X"), Request(TransactionType.Payment).References, Now);

        var interpreted = ProviderResultInterpreter.Interpret(result, Now);

        Assert.Equal(AttemptResolutionKind.InDoubt, interpreted.Resolution);
        Assert.Equal("1002", interpreted.ResponseCode);
        Assert.Equal(expected, interpreted.Outcome.TransportStatus);
        Assert.True(interpreted.Outcome.RequestSent);
        Assert.False(interpreted.Outcome.ProvesRequestNotSent);
    }

    [Fact]
    public void Success_without_a_sent_request_is_impossible_and_becomes_in_doubt()
    {
        var request = Request(TransactionType.Payment);
        var impossible = ScriptedProviderAdapter.Success(request) with
        {
            Transport = new ProviderTransportResult(V1Transport.NotSent, RequestSent: false, null, null, null),
        };

        var interpreted = ProviderResultInterpreter.Interpret(impossible, Now);

        Assert.Equal(AttemptResolutionKind.InDoubt, interpreted.Resolution);
        Assert.True(interpreted.WasNormalized);
        Assert.True(interpreted.Outcome.RequestSent);
        Assert.Equal(ProviderResultInterpreter.ReasonInvalidResult, interpreted.ReasonCode);
    }

    [Fact]
    public void Not_sent_claim_with_request_sent_or_protocol_error_is_never_not_sent()
    {
        var request = Request(TransactionType.Payment);
        var sent = ScriptedProviderAdapter.NotSent(request) with
        {
            Transport = new ProviderTransportResult(V1Transport.ConnectionError, RequestSent: true, null, null, null),
        };
        var protocol = ScriptedProviderAdapter.NotSent(request) with
        {
            Transport = new ProviderTransportResult(V1Transport.ProtocolError, RequestSent: false, null, null, null),
        };

        Assert.Equal(AttemptResolutionKind.InDoubt, ProviderResultInterpreter.Interpret(sent, Now).Resolution);
        Assert.Equal(AttemptResolutionKind.InDoubt, ProviderResultInterpreter.Interpret(protocol, Now).Resolution);
    }

    [Fact]
    public void Null_result_is_in_doubt()
    {
        var interpreted = ProviderResultInterpreter.Interpret(null, Now);

        Assert.Equal(AttemptResolutionKind.InDoubt, interpreted.Resolution);
        Assert.True(interpreted.Outcome.RequestSent);
    }

    [Fact]
    public void Values_that_do_not_fit_the_attempt_columns_move_to_metadata_and_the_outcome_is_kept()
    {
        var request = Request(TransactionType.Payment);
        var longReference = new string('R', 200);
        var result = ScriptedProviderAdapter.Success(request, code: "00") with
        {
            References = request.References with { ProviderReference = longReference, ProviderStan = "   " },
            ProviderResponseMessage = new string('m', 900),
        };

        var interpreted = ProviderResultInterpreter.Interpret(result, Now);

        Assert.Equal(AttemptResolutionKind.Success, interpreted.Resolution);
        Assert.Null(interpreted.Outcome.ProviderReference);
        Assert.Null(interpreted.Outcome.ProviderStan);
        Assert.Null(interpreted.Outcome.RansysResponseCode); // "00" is not a 4-digit canonical code
        Assert.Null(interpreted.ResponseCode);
        Assert.Equal(500, interpreted.Outcome.ProviderResponseMessage!.Length);
        Assert.Equal(longReference, interpreted.Outcome.Metadata.Values["extension.adapter.providerReference"].GetString());
        Assert.Equal("00", interpreted.Outcome.Metadata.Values["extension.adapter.ransysResponseCode"].GetString());
    }

    // ---------- ProviderInvoker ----------

    [Theory]
    [InlineData(TransactionType.Payment, AttemptType.Payment, nameof(IProviderAdapter.PaymentAsync))]
    [InlineData(TransactionType.Purchase, AttemptType.Payment, nameof(IProviderAdapter.PurchaseAsync))]
    [InlineData(TransactionType.Transfer, AttemptType.Transfer, nameof(IProviderAdapter.TransferAsync))]
    [InlineData(TransactionType.Inquiry, AttemptType.Inquiry, nameof(IProviderAdapter.InquiryAsync))]
    [InlineData(TransactionType.BalanceInquiry, AttemptType.BalanceInquiry, nameof(IProviderAdapter.BalanceInquiryAsync))]
    [InlineData(TransactionType.Refund, AttemptType.Refund, nameof(IProviderAdapter.RefundAsync))]
    [InlineData(TransactionType.Reversal, AttemptType.Reversal, nameof(IProviderAdapter.ReversalAsync))]
    [InlineData(TransactionType.Void, AttemptType.Void, nameof(IProviderAdapter.VoidAsync))]
    public async Task Dispatches_each_transaction_type_to_its_own_adapter_operation(TransactionType type, AttemptType attempt, string operation)
    {
        var adapter = new ScriptedProviderAdapter();
        var invoker = Invoker(adapter);

        var result = await invoker.InvokeAsync(ProviderA, type, attempt, Request(type));

        Assert.Equal(ProviderOutcome.Success, result.Outcome);
        Assert.Equal(operation, Assert.Single(adapter.Calls).Operation);
    }

    [Fact]
    public void Void_is_never_dispatched_as_reversal_or_refund()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderInvoker.OperationFor(TransactionType.Void, AttemptType.Reversal));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderInvoker.OperationFor(TransactionType.Void, AttemptType.Refund));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderInvoker.OperationFor(TransactionType.Payment, AttemptType.Reversal));
    }

    [Fact]
    public async Task Missing_adapter_registration_is_proven_not_sent()
    {
        var adapter = new ScriptedProviderAdapter();
        var registry = new InProcessProviderAdapterRegistry();
        var invoker = new ProviderInvoker(registry, new FixedClock(Now));

        var result = await invoker.InvokeAsync(ProviderA, TransactionType.Payment, AttemptType.Payment, Request(TransactionType.Payment));

        Assert.Equal(ProviderOutcome.NotSent, result.Outcome);
        Assert.False(result.Transport.RequestSent);
        Assert.Equal(0, adapter.CallCount);
        Assert.Equal(AttemptResolutionKind.NotSent, ProviderResultInterpreter.Interpret(result, Now).Resolution);
    }

    [Fact]
    public async Task Adapter_exception_after_the_call_started_is_in_doubt_and_possibly_sent()
    {
        var adapter = new ScriptedProviderAdapter().ThenThrow();

        var result = await Invoker(adapter).InvokeAsync(ProviderA, TransactionType.Payment, AttemptType.Payment, Request(TransactionType.Payment));

        Assert.Equal(ProviderOutcome.InDoubt, result.Outcome);
        Assert.True(result.Transport.RequestSent);
        Assert.True(ProviderResultRules.IsValid(result));
    }

    [Fact]
    public async Task Adapter_exceeding_the_budget_is_in_doubt_timeout()
    {
        var adapter = new ScriptedProviderAdapter().ThenHang();
        var invoker = new ProviderInvoker(
            new InProcessProviderAdapterRegistry().Register(ProviderA.ProviderId, adapter), new FixedClock(Now),
            new ProviderCallPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)));

        var result = await invoker.InvokeAsync(ProviderA, TransactionType.Payment, AttemptType.Payment, Request(TransactionType.Payment));

        Assert.Equal(ProviderOutcome.InDoubt, result.Outcome);
        Assert.Equal(V1Transport.Timeout, result.Transport.Status);
        Assert.True(result.Transport.RequestSent);
    }

    [Fact]
    public async Task Adapter_ignoring_cancellation_is_still_bounded_by_the_budget()
    {
        var adapter = new ScriptedProviderAdapter().Then(async (_, request, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            return ScriptedProviderAdapter.Success(request);
        });
        var invoker = new ProviderInvoker(
            new InProcessProviderAdapterRegistry().Register(ProviderA.ProviderId, adapter), new FixedClock(Now),
            new ProviderCallPolicy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)));

        var started = DateTime.UtcNow;
        var result = await invoker.InvokeAsync(ProviderA, TransactionType.Payment, AttemptType.Payment, Request(TransactionType.Payment));

        Assert.Equal(ProviderOutcome.InDoubt, result.Outcome);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    // ---------- ProviderRequestFactory ----------

    [Fact]
    public void Payment_request_carries_amount_currency_correlation_and_canonical_objects()
    {
        var (transaction, attempt) = ProcessingTransaction(TransactionType.Payment, 150_000m);

        var request = new ProviderRequestFactory().Create(transaction, attempt, "PLN-PREPAID");

        Assert.Equal(("PAYMENT", "PLN-PREPAID", 150_000m, "IDR", 1), (request.TransactionType, request.ProductCode, request.Amount, request.CurrencyCode, request.CurrencyDefinitionVersion));
        Assert.Equal(transaction.Id.Value, request.RansysTransactionId);
        Assert.Equal(attempt.Id.Value, request.AttemptId);
        Assert.Equal((attempt.CorrelationId, attempt.TraceId), (request.Correlation.CorrelationId, request.Correlation.TraceId));
        Assert.Equal("CUST-1", request.Customer["customerId"].GetString());
        Assert.Equal("BILLER", request.Destination["type"].GetString());
        Assert.Equal("5123", request.Metadata["product.pln.meterId"].GetString());
        Assert.Equal(0, request.ExecutionContext.TimeoutPolicy.MaxRetry);
        Assert.Equal(ProviderA.ProviderCode, request.ExecutionContext.Provider.ProviderCode);
    }

    [Fact]
    public void Inquiry_request_has_no_amount()
    {
        var (transaction, attempt) = ProcessingTransaction(TransactionType.Inquiry, 0m);

        var request = new ProviderRequestFactory().Create(transaction, attempt, "PLN-PREPAID");

        Assert.Null(request.Amount);
        Assert.Null(request.CurrencyCode);
    }

    [Fact]
    public void Child_request_carries_the_original_id_and_the_originals_provider_references()
    {
        var (original, _) = ProcessingTransaction(TransactionType.Payment, 100_000m);
        var (child, attempt) = ProcessingTransaction(TransactionType.Refund, 40_000m, original.Id);

        var request = new ProviderRequestFactory().Create(child, attempt, "PLN-PREPAID", original, new OriginalProviderReferences("ORIG-REF", "123456", "RRN-9"));

        Assert.Equal(child.Id.Value, request.RansysTransactionId);
        Assert.Equal(original.Id.Value, request.OriginalTransactionId);
        Assert.Equal(("ORIG-REF", "123456", "RRN-9"), (request.References.ProviderReference, request.References.ProviderStan, request.References.ProviderRrn));
        Assert.Equal(child.Identity.ClientReference, request.References.ClientReference);
        Assert.Equal(40_000m, request.Amount);
        Assert.Throws<ArgumentException>(() => new ProviderRequestFactory().Create(child, attempt, "PLN-PREPAID"));
    }

    // ---------- helpers ----------

    private static ProviderInvoker Invoker(IProviderAdapter adapter) =>
        new(new InProcessProviderAdapterRegistry().Register(ProviderA.ProviderId, adapter), new FixedClock(Now));

    private static ProviderError Error(string code) => new(ProviderErrorCategories.Timeout, code, "timeout", false, null);

    private static ProviderTransactionRequest Request(TransactionType type)
    {
        var original = type is TransactionType.Refund or TransactionType.Reversal or TransactionType.Void
            ? new TransactionId(Guid.CreateVersion7())
            : (TransactionId?)null;
        var (transaction, attempt) = ProcessingTransaction(type, type is TransactionType.Inquiry or TransactionType.BalanceInquiry ? 0m : 10_000m, original);
        var originalTransaction = original is null ? null : ProcessingTransaction(TransactionType.Payment, 10_000m, id: original).Transaction;
        return new ProviderRequestFactory().Create(transaction, attempt, "PRD", originalTransaction);
    }

    private static (Transaction Transaction, TransactionAttempt Attempt) ProcessingTransaction(
        TransactionType type, decimal amount, TransactionId? original = null, TransactionId? id = null)
    {
        var txId = id ?? new TransactionId(Guid.CreateVersion7());
        var merchant = new MerchantId(Guid.CreateVersion7());
        var channel = new ChannelId(Guid.CreateVersion7());
        var product = new ProductId(Guid.CreateVersion7());
        var money = Money.Create(amount, Idr).Value;
        var reference = $"REF-{txId.Value:N}";
        var fingerprint = TransactionFingerprint.Compute(new FingerprintInput(merchant, channel, type, product, null, null, money, reference));
        var metadata = ExtensionMetadata.Create([new KeyValuePair<string, JsonElement>("product.pln.meterId", JsonSerializer.SerializeToElement("5123"))]).Value;
        var transaction = Transaction.Create(new TransactionDraft(
            TransactionIdentity.Create(txId, reference, null, fingerprint, original).Value,
            type, merchant, channel, product, money,
            Customer.Create(customerId: "CUST-1").Value,
            null,
            TransactionEndpoint.Create(EndpointType.Biller, "PLN").Value,
            TransactionReferences.Create(reference).Value, metadata, Now)).Value;
        var context = TransitionContext.Create("TEST", ChangeSource.Core, Now).Value;
        transaction.Validate(null, TransactionConfigurationSnapshot.None, context);
        if (transaction.RequiresReservation)
        {
            transaction.MarkReserved(context);
        }

        transaction.BeginProcessing(RoutingDecision.Initial(ProviderA, 1, Now).Value, context);
        var attempt = TransactionAttempt.Start(
            new AttemptId(Guid.CreateVersion7()), transaction.Id, 1, Transaction.PrimaryAttemptTypeFor(type)!.Value, ProviderA,
            transaction.Id.ToString(), "trace-1", Now).Value;
        return (transaction, attempt);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
