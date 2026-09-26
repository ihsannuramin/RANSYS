using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ransys.Adapter.Contracts.V1;
using Ransys.Adapter.Sdk;

namespace Ransys.Adapter.Tests;

/// <summary>In-memory gRPC host (TestServer) exposing a fake adapter and a fake callback sink through the SDK services.</summary>
internal sealed class GrpcTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GrpcTestHost(WebApplication app, GrpcChannel channel)
    {
        _app = app;
        Channel = channel;
    }

    public GrpcChannel Channel { get; }

    public static async Task<GrpcTestHost> StartAsync(IProviderAdapter adapter, IProviderCallbackSink? sink = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(adapter);
        builder.Services.AddSingleton(sink ?? new FakeCallbackSink());

        var app = builder.Build();
        app.MapGrpcService<ProviderAdapterGrpcService>();
        app.MapGrpcService<ProviderCallbackSinkGrpcService>();
        await app.StartAsync();

        var server = app.GetTestServer();
        var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        return new GrpcTestHost(app, channel);
    }

    /// <summary>An address on which nothing listens, so connecting is refused before any request is written.</summary>
    public static Uri ClosedAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    public async ValueTask DisposeAsync()
    {
        Channel.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>Scripted adapter that records which operation was invoked.</summary>
internal sealed class FakeProviderAdapter : IProviderAdapter
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public List<ProviderTransactionRequest> Requests { get; } = [];

    public Func<string, ProviderTransactionRequest, CancellationToken, Task<ProviderResult>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(TestData.Result(ProviderOutcome.Success));

    public Task<ProviderCapabilities> GetCapabilitiesAsync(ProviderIdentity provider, CancellationToken cancellationToken)
    {
        Calls.Enqueue(nameof(GetCapabilitiesAsync));
        return Task.FromResult(new ProviderCapabilities(new HashSet<string> { ProviderCapabilityCodes.Payment, ProviderCapabilityCodes.Transfer }, "1.0"));
    }

    public Task<ProviderHealthResult> HealthCheckAsync(ProviderIdentity provider, CancellationToken cancellationToken)
    {
        Calls.Enqueue(nameof(HealthCheckAsync));
        return Task.FromResult(new ProviderHealthResult("DEGRADED", 120, "SLOW", "latency high", TestData.Now));
    }

    public Task<ProviderResult> InquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Inquiry", request, cancellationToken);

    public Task<ProviderResult> PaymentAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Payment", request, cancellationToken);

    public Task<ProviderResult> PurchaseAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Purchase", request, cancellationToken);

    public Task<ProviderResult> TransferAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Transfer", request, cancellationToken);

    public Task<ProviderResult> VoidAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Void", request, cancellationToken);

    public Task<ProviderResult> StatusCheckAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("StatusCheck", request, cancellationToken);

    public Task<ProviderResult> ReversalAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Reversal", request, cancellationToken);

    public Task<ProviderResult> RefundAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Refund", request, cancellationToken);

    public Task<ProviderResult> AdviceAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("Advice", request, cancellationToken);

    public Task<ProviderResult> BalanceInquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) => Run("BalanceInquiry", request, cancellationToken);

    public Task<ProviderBalanceResult> GetProviderBalanceAsync(ProviderIdentity provider, string? balanceAccountReference, CancellationToken cancellationToken)
    {
        Calls.Enqueue(nameof(GetProviderBalanceAsync));
        return Task.FromResult(new ProviderBalanceResult(987654321.87654321m, "IDR", 1, TestData.Now, balanceAccountReference ?? "none"));
    }

    private Task<ProviderResult> Run(string operation, ProviderTransactionRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue(operation);
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Handler(operation, request, cancellationToken);
    }
}

internal sealed class FakeCallbackSink : IProviderCallbackSink
{
    public List<ProviderCallback> Received { get; } = [];

    public Task<ProviderCallbackAck> SubmitAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        lock (Received)
        {
            Received.Add(callback);
        }

        return Task.FromResult(new ProviderCallbackAck(true, "ACK-" + callback.CallbackId));
    }
}
