using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Ransys.Adapter.Contracts.V1;
using Wire = Ransys.Provider.V1;

namespace Ransys.Adapter.Sdk;

/// <summary>
/// Remote binding of <see cref="IProviderAdapter"/> over gRPC (Provider Adapter Contract v1 §25). It only maps
/// messages (<see cref="ProtoMapper"/>) and classifies failures of the gRPC call itself; it has no business logic.
/// </summary>
/// <remarks>
/// <para><b>No hidden retries.</b> Each method issues exactly one call. Do not configure a gRPC retry or hedging
/// policy on the channel used here: a retried financial request after a possible send is prohibited (§12).</para>
/// <para><b>Conservative RequestSent.</b> A transaction call that fails after it may have reached the adapter
/// returns IN_DOUBT + AMBIGUOUS with <c>RequestSent = true</c> (deadline → <c>Timeout</c>, unavailable →
/// <c>ConnectionError</c>, anything else → <c>ProtocolError</c>). NOT_SENT is returned only when non-delivery is
/// proven: the request could not be mapped (nothing was written) or the connection could not be established.
/// A reply that cannot be mapped is IN_DOUBT too. A valid reply is returned as the adapter sent it; Core
/// validates it with <see cref="ProviderResultRules"/>.</para>
/// <para>Cancellation requested by the caller throws <see cref="OperationCanceledException"/>; the attempt then
/// has no recorded outcome and Core treats it as possibly sent (ADR-005).</para>
/// </remarks>
public sealed class GrpcProviderAdapterClient : IProviderAdapter
{
    private static readonly TimeSpan DeadlineTolerance = TimeSpan.FromMilliseconds(50);

    private readonly Wire.ProviderAdapter.ProviderAdapterClient _client;
    private readonly GrpcProviderAdapterClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public GrpcProviderAdapterClient(
        CallInvoker callInvoker,
        GrpcProviderAdapterClientOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(callInvoker);
        _client = new Wire.ProviderAdapter.ProviderAdapterClient(callInvoker);
        _options = options ?? new GrpcProviderAdapterClientOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public GrpcProviderAdapterClient(
        GrpcChannel channel,
        GrpcProviderAdapterClientOptions? options = null,
        TimeProvider? timeProvider = null)
        : this((channel ?? throw new ArgumentNullException(nameof(channel))).CreateCallInvoker(), options, timeProvider)
    {
    }

    public async Task<ProviderCapabilities> GetCapabilitiesAsync(ProviderIdentity provider, CancellationToken cancellationToken)
    {
        var request = new Wire.GetCapabilitiesRequest { Provider = ProtoMapper.ToProto(provider) };
        var response = await _client.GetCapabilitiesAsync(request, NonFinancialOptions(cancellationToken)).ResponseAsync.ConfigureAwait(false);
        return ProtoMapper.FromProto(response);
    }

    /// <summary>A failed health call is reported as UNHEALTHY (§17: health reports connectivity facts).</summary>
    public async Task<ProviderHealthResult> HealthCheckAsync(ProviderIdentity provider, CancellationToken cancellationToken)
    {
        var request = new Wire.HealthCheckRequest { Provider = ProtoMapper.ToProto(provider) };
        try
        {
            var response = await _client.HealthCheckAsync(request, NonFinancialOptions(cancellationToken)).ResponseAsync.ConfigureAwait(false);
            return ProtoMapper.FromProto(response);
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderHealthResult("UNHEALTHY", null, "ADAPTER_UNREACHABLE", ex.Status.Detail, _timeProvider.GetUtcNow());
        }
        catch (ProtoMappingException ex)
        {
            return new ProviderHealthResult("UNHEALTHY", null, "ADAPTER_PROTOCOL_ERROR", ex.Message, _timeProvider.GetUtcNow());
        }
    }

    public Task<ProviderResult> InquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.InquiryAsync(r, o), cancellationToken);

    public Task<ProviderResult> PaymentAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.PaymentAsync(r, o), cancellationToken);

    public Task<ProviderResult> PurchaseAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.PurchaseAsync(r, o), cancellationToken);

    public Task<ProviderResult> TransferAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.TransferAsync(r, o), cancellationToken);

    public Task<ProviderResult> VoidAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.VoidAsync(r, o), cancellationToken);

    public Task<ProviderResult> StatusCheckAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.StatusCheckAsync(r, o), cancellationToken);

    public Task<ProviderResult> ReversalAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.ReversalAsync(r, o), cancellationToken);

    public Task<ProviderResult> RefundAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.RefundAsync(r, o), cancellationToken);

    public Task<ProviderResult> AdviceAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.AdviceAsync(r, o), cancellationToken);

    public Task<ProviderResult> BalanceInquiryAsync(ProviderTransactionRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, static (c, r, o) => c.BalanceInquiryAsync(r, o), cancellationToken);

    public async Task<ProviderBalanceResult> GetProviderBalanceAsync(
        ProviderIdentity provider,
        string? balanceAccountReference,
        CancellationToken cancellationToken)
    {
        var request = ProtoMapper.ToProto(provider, balanceAccountReference);
        var response = await _client.GetProviderBalanceAsync(request, NonFinancialOptions(cancellationToken)).ResponseAsync.ConfigureAwait(false);
        return ProtoMapper.FromProto(response);
    }

    private CallOptions NonFinancialOptions(CancellationToken cancellationToken) =>
        new(deadline: DateTime.UtcNow + _options.NonFinancialCallTimeout, cancellationToken: cancellationToken);

    private async Task<ProviderResult> ExecuteAsync(
        ProviderTransactionRequest request,
        Func<Wire.ProviderAdapter.ProviderAdapterClient, Wire.ProviderTransactionRequest, CallOptions, AsyncUnaryCall<Wire.ProviderResult>> call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var references = request.References
            ?? new ProviderTransactionReferences(string.Empty, null, null, null, null, null, null, new Dictionary<string, string>());

        Wire.ProviderTransactionRequest wire;
        try
        {
            wire = ProtoMapper.ToProto(request);
        }
        catch (ProtoMappingException ex)
        {
            // Nothing has been written to the network: non-delivery is proven.
            return ProviderResults.NotSent(
                TransportStatus.NotSent,
                new ProviderError(ProviderErrorCategories.Mapping, "REQUEST_MAPPING_FAILED", ex.Message, RetryableTransportError: false, RawCode: null),
                ProviderResultCodes.InternalError,
                references,
                _timeProvider.GetUtcNow());
        }

        var timeout = request.ExecutionContext.TimeoutPolicy;
        var deadline = DateTime.UtcNow + timeout.ConnectTimeout + timeout.ReadTimeout + _options.DeadlineGrace;
        var started = Stopwatch.GetTimestamp();

        Wire.ProviderResult response;
        try
        {
            response = await call(_client, wire, new CallOptions(deadline: deadline, cancellationToken: cancellationToken))
                .ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Provider adapter call was cancelled by the caller.", ex, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException ex)
        {
            return Classify(ex, deadline, references, Stopwatch.GetElapsedTime(started));
        }
#pragma warning disable CA1031 // Any other failure of the call: the request may have been sent, so report IN_DOUBT.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ProviderResults.InDoubt(
                TransportStatus.ProtocolError,
                new ProviderError(ProviderErrorCategories.Unknown, "ADAPTER_CALL_FAILED", ex.Message, RetryableTransportError: false, RawCode: ex.GetType().Name),
                references,
                _timeProvider.GetUtcNow(),
                Stopwatch.GetElapsedTime(started));
        }

        try
        {
            return ProtoMapper.FromProto(response);
        }
        catch (ProtoMappingException ex)
        {
            return ProviderResults.InDoubt(
                TransportStatus.ProtocolError,
                new ProviderError(ProviderErrorCategories.Protocol, "RESULT_MAPPING_FAILED", ex.Message, RetryableTransportError: false, RawCode: null),
                references,
                _timeProvider.GetUtcNow(),
                Stopwatch.GetElapsedTime(started));
        }
    }

    private ProviderResult Classify(RpcException exception, DateTime deadline, ProviderTransactionReferences references, TimeSpan elapsed)
    {
        var rawCode = exception.StatusCode.ToString();
        var now = _timeProvider.GetUtcNow();

        if (GrpcTransportFailure.ProvesNotSent(exception))
        {
            return ProviderResults.NotSent(
                TransportStatus.ConnectionError,
                new ProviderError(ProviderErrorCategories.Connection, "ADAPTER_CONNECT_FAILED", exception.Status.Detail, RetryableTransportError: true, rawCode),
                ProviderResultCodes.ProviderLinkDown,
                references,
                now);
        }

        // The server observes the propagated deadline (grpc-timeout, millisecond resolution) and may answer first
        // with its own status. Once our deadline has (nearly) passed, the call is a timeout after a possible send.
        // This only chooses the transport label: every branch below is IN_DOUBT with RequestSent = true.
        var deadlinePassed = exception.StatusCode == StatusCode.DeadlineExceeded
            || DateTime.UtcNow >= deadline - DeadlineTolerance;
        var (status, category, code) = exception.StatusCode switch
        {
            _ when deadlinePassed => (TransportStatus.Timeout, ProviderErrorCategories.Timeout, "ADAPTER_DEADLINE_EXCEEDED"),
            StatusCode.Unavailable or StatusCode.Cancelled => (TransportStatus.ConnectionError, ProviderErrorCategories.Connection, "ADAPTER_CONNECTION_LOST"),
            _ => (TransportStatus.ProtocolError, ProviderErrorCategories.Unknown, "ADAPTER_CALL_FAILED"),
        };

        return ProviderResults.InDoubt(
            status,
            new ProviderError(category, code, exception.Status.Detail, RetryableTransportError: false, rawCode),
            references,
            now,
            elapsed);
    }
}
