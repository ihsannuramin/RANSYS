using Grpc.Core;
using Ransys.Adapter.Contracts.V1;
using Wire = Ransys.Provider.V1;

namespace Ransys.Adapter.Sdk;

/// <summary>
/// Server binding: hosts any <see cref="IProviderAdapter"/> as the <c>ransys.provider.v1.ProviderAdapter</c> gRPC
/// service. Every rpc maps the request with <see cref="ProtoMapper"/>, delegates to the same-named adapter
/// operation (TRANSFER → <see cref="IProviderAdapter.TransferAsync"/>, VOID → <see cref="IProviderAdapter.VoidAsync"/>;
/// nothing is remapped) and maps the reply back. No business logic and no retries.
/// </summary>
/// <remarks>
/// A transaction request that cannot be mapped never reaches the adapter, so it is answered with NOT_SENT
/// (<c>RequestSent = false</c>, category MAPPING). A result the adapter returned but that cannot be mapped is
/// answered with <see cref="StatusCode.Internal"/>, which the client reports as IN_DOUBT. Exceptions thrown by the
/// adapter propagate as gRPC errors and are likewise reported as IN_DOUBT by the client.
/// </remarks>
public class ProviderAdapterGrpcService : Wire.ProviderAdapter.ProviderAdapterBase
{
    private readonly IProviderAdapter _adapter;

    public ProviderAdapterGrpcService(IProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
    }

    public override async Task<Wire.GetCapabilitiesResponse> GetCapabilities(Wire.GetCapabilitiesRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var provider = MapInput(() => ProtoMapper.FromProto(request.Provider));
        var result = await _adapter.GetCapabilitiesAsync(provider, context.CancellationToken).ConfigureAwait(false);
        return MapOutput(() => ProtoMapper.ToProto(result));
    }

    public override async Task<Wire.HealthCheckResponse> HealthCheck(Wire.HealthCheckRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var provider = MapInput(() => ProtoMapper.FromProto(request.Provider));
        var result = await _adapter.HealthCheckAsync(provider, context.CancellationToken).ConfigureAwait(false);
        return MapOutput(() => ProtoMapper.ToProto(result));
    }

    public override Task<Wire.ProviderResult> Inquiry(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.InquiryAsync);

    public override Task<Wire.ProviderResult> Payment(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.PaymentAsync);

    public override Task<Wire.ProviderResult> Purchase(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.PurchaseAsync);

    public override Task<Wire.ProviderResult> Transfer(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.TransferAsync);

    public override Task<Wire.ProviderResult> Void(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.VoidAsync);

    public override Task<Wire.ProviderResult> StatusCheck(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.StatusCheckAsync);

    public override Task<Wire.ProviderResult> Reversal(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.ReversalAsync);

    public override Task<Wire.ProviderResult> Refund(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.RefundAsync);

    public override Task<Wire.ProviderResult> Advice(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.AdviceAsync);

    public override Task<Wire.ProviderResult> BalanceInquiry(Wire.ProviderTransactionRequest request, ServerCallContext context) =>
        ExecuteAsync(request, context, _adapter.BalanceInquiryAsync);

    public override async Task<Wire.GetProviderBalanceResponse> GetProviderBalance(Wire.GetProviderBalanceRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var provider = MapInput(() => ProtoMapper.FromProto(request.Provider));
        var reference = request.HasBalanceAccountReference ? request.BalanceAccountReference : null;
        var result = await _adapter.GetProviderBalanceAsync(provider, reference, context.CancellationToken).ConfigureAwait(false);
        return MapOutput(() => ProtoMapper.ToProto(result));
    }

    private static async Task<Wire.ProviderResult> ExecuteAsync(
        Wire.ProviderTransactionRequest request,
        ServerCallContext context,
        Func<ProviderTransactionRequest, CancellationToken, Task<ProviderResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        ProviderTransactionRequest mapped;
        try
        {
            mapped = ProtoMapper.FromProto(request);
        }
        catch (ProtoMappingException ex)
        {
            // The adapter was never invoked, so non-delivery is proven.
            var references = new ProviderTransactionReferences(
                request.References?.ClientReference ?? string.Empty, null, null, null, null, null, null, new Dictionary<string, string>());
            return ProtoMapper.ToProto(ProviderResults.NotSent(
                TransportStatus.NotSent,
                new ProviderError(ProviderErrorCategories.Mapping, "REQUEST_MAPPING_FAILED", ex.Message, RetryableTransportError: false, RawCode: null),
                ProviderResultCodes.InternalError,
                references,
                DateTimeOffset.UtcNow));
        }

        var result = await operation(mapped, context.CancellationToken).ConfigureAwait(false);
        return MapOutput(() => ProtoMapper.ToProto(result));
    }

    private static T MapInput<T>(Func<T> map)
    {
        try
        {
            return map();
        }
        catch (ProtoMappingException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    private static T MapOutput<T>(Func<T> map)
    {
        try
        {
            return map();
        }
        catch (ProtoMappingException ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Adapter reply could not be mapped: {ex.Message}"));
        }
    }
}
