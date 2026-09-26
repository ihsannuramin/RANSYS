using Grpc.Net.Client;
using Ransys.Adapter.Contracts.V1;
using Ransys.Adapter.Sdk;
using Wire = Ransys.Provider.V1;

namespace Ransys.Adapter.Tests;

/// <summary>
/// The remote binding (GrpcProviderAdapterClient → ProviderAdapterGrpcService → IProviderAdapter) carries the
/// adapter's results unchanged and maps failures of the gRPC call itself conservatively.
/// </summary>
public sealed class GrpcBindingTests
{
    public static TheoryData<ProviderOutcome> Outcomes() =>
        [ProviderOutcome.Success, ProviderOutcome.Failed, ProviderOutcome.Pending, ProviderOutcome.InDoubt, ProviderOutcome.NotSent];

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task Adapter_results_cross_the_binding_unchanged(ProviderOutcome outcome)
    {
        var expected = TestData.Result(outcome);
        var adapter = new FakeProviderAdapter { Handler = (_, _, _) => Task.FromResult(expected) };
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);

        var result = await client.PaymentAsync(TestData.Request(), CancellationToken.None);

        Assert.Equal(TestData.Canonical(expected), TestData.Canonical(result));
        Assert.True(ProviderResultRules.IsValid(result));
        Assert.Equal(outcome != ProviderOutcome.NotSent, result.Transport.RequestSent);
    }

    [Fact]
    public async Task Adapter_receives_the_request_unchanged()
    {
        var adapter = new FakeProviderAdapter();
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);
        var request = TestData.Request();

        await client.PaymentAsync(request, CancellationToken.None);

        Assert.Equal(TestData.Canonical(request), TestData.Canonical(Assert.Single(adapter.Requests)));
    }

    public static TheoryData<string> Operations() =>
        ["Inquiry", "Payment", "Purchase", "Transfer", "Void", "StatusCheck", "Reversal", "Refund", "Advice", "BalanceInquiry"];

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_operation_reaches_the_same_named_adapter_method(string operation)
    {
        var adapter = new FakeProviderAdapter();
        await using var host = await GrpcTestHost.StartAsync(adapter);
        IProviderAdapter client = new GrpcProviderAdapterClient(host.Channel);
        var request = TestData.Request(ToTransactionType(operation));

        var call = operation switch
        {
            "Inquiry" => client.InquiryAsync(request, CancellationToken.None),
            "Payment" => client.PaymentAsync(request, CancellationToken.None),
            "Purchase" => client.PurchaseAsync(request, CancellationToken.None),
            "Transfer" => client.TransferAsync(request, CancellationToken.None),
            "Void" => client.VoidAsync(request, CancellationToken.None),
            "StatusCheck" => client.StatusCheckAsync(request, CancellationToken.None),
            "Reversal" => client.ReversalAsync(request, CancellationToken.None),
            "Refund" => client.RefundAsync(request, CancellationToken.None),
            "Advice" => client.AdviceAsync(request, CancellationToken.None),
            _ => client.BalanceInquiryAsync(request, CancellationToken.None),
        };
        await call;

        Assert.Equal([operation], adapter.Calls);
    }

    [Fact]
    public async Task Transfer_goes_to_TransferAsync_and_Void_to_VoidAsync_never_reversal_or_refund()
    {
        var adapter = new FakeProviderAdapter();
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);

        await client.TransferAsync(TestData.Request("TRANSFER"), CancellationToken.None);
        await client.VoidAsync(TestData.Request("VOID"), CancellationToken.None);

        Assert.Equal(["Transfer", "Void"], adapter.Calls);
        Assert.DoesNotContain("Reversal", adapter.Calls);
        Assert.DoesNotContain("Refund", adapter.Calls);
        Assert.Equal(["TRANSFER", "VOID"], adapter.Requests.Select(r => r.TransactionType));
    }

    [Fact]
    public async Task Unsupported_capability_is_a_not_sent_capability_failure()
    {
        var adapter = new FakeProviderAdapter
        {
            Handler = (_, request, _) => Task.FromResult(
                ProviderResults.CapabilityUnsupported(ProviderCapabilityCodes.Void, request.References, TestData.Now)),
        };
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);

        var result = await client.VoidAsync(TestData.Request("VOID"), CancellationToken.None);

        Assert.Equal(ProviderOutcome.NotSent, result.Outcome);
        Assert.Equal(ResultFinality.NotApplicable, result.Finality);
        Assert.False(result.Transport.RequestSent);
        Assert.Equal(TransportStatus.NotSent, result.Transport.Status);
        Assert.Equal(ProviderErrorCategories.CapabilityUnsupported, result.Transport.Error!.Category);
        Assert.True(ProviderResultRules.IsValid(result));
    }

    [Fact]
    public async Task Deadline_after_send_is_in_doubt_with_request_sent()
    {
        var adapter = new FakeProviderAdapter
        {
            Handler = async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return TestData.Result(ProviderOutcome.Success);
            },
        };
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel, new GrpcProviderAdapterClientOptions { DeadlineGrace = TimeSpan.FromMilliseconds(100) });
        var request = TestData.Request(connectTimeout: TimeSpan.FromMilliseconds(100), readTimeout: TimeSpan.FromMilliseconds(200));

        var result = await client.PaymentAsync(request, CancellationToken.None);

        Assert.Equal(ProviderOutcome.InDoubt, result.Outcome);
        Assert.Equal(ResultFinality.Ambiguous, result.Finality);
        Assert.True(result.Transport.Status == TransportStatus.Timeout, $"{result.Transport.Error}");
        Assert.True(result.Transport.RequestSent);
        Assert.Equal(ProviderErrorCategories.Timeout, result.Transport.Error!.Category);
        Assert.Equal(ProviderResultCodes.InDoubt, result.RansysResponseCode);
        Assert.Equal(request.References.ClientReference, result.References.ClientReference);
        Assert.True(ProviderResultRules.IsValid(result));
        Assert.Equal(["Payment"], adapter.Calls);
    }

    [Fact]
    public async Task Connect_failure_before_send_is_not_sent_so_failover_is_possible()
    {
        using var channel = GrpcChannel.ForAddress(GrpcTestHost.ClosedAddress());
        var client = new GrpcProviderAdapterClient(channel);

        var result = await client.PaymentAsync(TestData.Request(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.NotSent, result.Outcome);
        Assert.Equal(ResultFinality.NotApplicable, result.Finality);
        Assert.Equal(TransportStatus.ConnectionError, result.Transport.Status);
        Assert.False(result.Transport.RequestSent);
        Assert.Equal(ProviderErrorCategories.Connection, result.Transport.Error!.Category);
        Assert.True(ProviderResultRules.IsValid(result));
    }

    [Fact]
    public async Task Adapter_throwing_mid_call_is_in_doubt_with_request_sent()
    {
        var adapter = new FakeProviderAdapter { Handler = (_, _, _) => throw new InvalidOperationException("socket reset after write") };
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);

        var result = await client.PaymentAsync(TestData.Request(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.InDoubt, result.Outcome);
        Assert.Equal(ResultFinality.Ambiguous, result.Finality);
        Assert.True(result.Transport.RequestSent);
        Assert.NotEqual(TransportStatus.NotSent, result.Transport.Status);
        Assert.True(ProviderResultRules.IsValid(result));
    }

    [Fact]
    public async Task Adapter_reply_that_cannot_be_mapped_is_in_doubt()
    {
        var adapter = new FakeProviderAdapter
        {
            Handler = (_, _, _) => Task.FromResult(TestData.Result(ProviderOutcome.Success) with
            {
                Data = TestData.Json("""{"amount":123456789.12345678123}"""),
            }),
        };
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);

        var result = await client.PaymentAsync(TestData.Request(), CancellationToken.None);

        Assert.Equal(ProviderOutcome.InDoubt, result.Outcome);
        Assert.True(result.Transport.RequestSent);
        Assert.Equal(["Payment"], adapter.Calls);
    }

    [Fact]
    public async Task Request_that_cannot_be_mapped_client_side_is_not_sent_and_never_reaches_the_adapter()
    {
        var adapter = new FakeProviderAdapter();
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);

        var result = await client.PaymentAsync(TestData.Request("SALE"), CancellationToken.None);

        Assert.Equal(ProviderOutcome.NotSent, result.Outcome);
        Assert.False(result.Transport.RequestSent);
        Assert.Equal(ProviderErrorCategories.Mapping, result.Transport.Error!.Category);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Request_that_cannot_be_mapped_server_side_is_not_sent_and_never_reaches_the_adapter()
    {
        var adapter = new FakeProviderAdapter();
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var raw = new Wire.ProviderAdapter.ProviderAdapterClient(host.Channel);
        var wire = ProtoMapper.ToProto(TestData.Request());
        wire.Amount.Value = "12,50";

        var result = ProtoMapper.FromProto(await raw.PaymentAsync(wire));

        Assert.Equal(ProviderOutcome.NotSent, result.Outcome);
        Assert.False(result.Transport.RequestSent);
        Assert.Equal(ProviderErrorCategories.Mapping, result.Transport.Error!.Category);
        Assert.True(ProviderResultRules.IsValid(result));
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_throws_instead_of_inventing_an_outcome()
    {
        var adapter = new FakeProviderAdapter
        {
            Handler = async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return TestData.Result(ProviderOutcome.Success);
            },
        };
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PaymentAsync(TestData.Request(), cts.Token));
    }

    [Fact]
    public async Task Capabilities_health_and_balance_cross_the_binding()
    {
        var adapter = new FakeProviderAdapter();
        await using var host = await GrpcTestHost.StartAsync(adapter);
        var client = new GrpcProviderAdapterClient(host.Channel);

        var capabilities = await client.GetCapabilitiesAsync(TestData.Identity, CancellationToken.None);
        var health = await client.HealthCheckAsync(TestData.Identity, CancellationToken.None);
        var balance = await client.GetProviderBalanceAsync(TestData.Identity, "ACC-1", CancellationToken.None);

        Assert.True(capabilities.CapabilityCodes.SetEquals([ProviderCapabilityCodes.Payment, ProviderCapabilityCodes.Transfer]));
        Assert.Equal(new ProviderHealthResult("DEGRADED", 120, "SLOW", "latency high", TestData.Now), health);
        Assert.Equal(new ProviderBalanceResult(987654321.87654321m, "IDR", 1, TestData.Now, "ACC-1"), balance);
    }

    [Fact]
    public async Task Unreachable_adapter_reports_unhealthy()
    {
        using var channel = GrpcChannel.ForAddress(GrpcTestHost.ClosedAddress());
        var client = new GrpcProviderAdapterClient(channel);

        var health = await client.HealthCheckAsync(TestData.Identity, CancellationToken.None);

        Assert.Equal("UNHEALTHY", health.State);
        Assert.Equal("ADAPTER_UNREACHABLE", health.DiagnosticCode);
    }

    [Fact]
    public async Task Callback_round_trips_through_the_callback_sink_binding()
    {
        var sink = new FakeCallbackSink();
        await using var host = await GrpcTestHost.StartAsync(new FakeProviderAdapter(), sink);
        var client = new GrpcProviderCallbackSinkClient(host.Channel);
        var callback = TestData.Callback();

        var ack = await client.SubmitAsync(callback, CancellationToken.None);

        Assert.Equal(new ProviderCallbackAck(true, "ACK-CB-1"), ack);
        Assert.Equal(TestData.Canonical(callback), TestData.Canonical(Assert.Single(sink.Received)));
    }

    private static string ToTransactionType(string operation) => operation switch
    {
        "StatusCheck" => "STATUS_CHECK",
        "BalanceInquiry" => "BALANCE_INQUIRY",
        _ => operation.ToUpperInvariant(),
    };
}
