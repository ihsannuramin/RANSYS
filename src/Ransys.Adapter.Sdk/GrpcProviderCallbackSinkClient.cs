using Grpc.Core;
using Grpc.Net.Client;
using Ransys.Adapter.Contracts.V1;
using Wire = Ransys.Provider.V1;

namespace Ransys.Adapter.Sdk;

/// <summary>
/// Adapter-side binding of <see cref="IProviderCallbackSink"/>: forwards a normalized callback to Core over gRPC
/// (Provider Adapter Contract v1 §15). One call per submission, no hidden retries.
/// </summary>
/// <remarks>
/// A failed call propagates its <see cref="RpcException"/>: delivery is then unconfirmed and the adapter may
/// redeliver, which is safe because callbacks are at-least-once and Core processing is idempotent (§15–§16).
/// A callback that cannot be mapped throws <see cref="ProtoMappingException"/> before anything is sent.
/// </remarks>
public sealed class GrpcProviderCallbackSinkClient : IProviderCallbackSink
{
    private readonly Wire.ProviderCallbackSink.ProviderCallbackSinkClient _client;
    private readonly GrpcProviderAdapterClientOptions _options;

    public GrpcProviderCallbackSinkClient(CallInvoker callInvoker, GrpcProviderAdapterClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(callInvoker);
        _client = new Wire.ProviderCallbackSink.ProviderCallbackSinkClient(callInvoker);
        _options = options ?? new GrpcProviderAdapterClientOptions();
    }

    public GrpcProviderCallbackSinkClient(GrpcChannel channel, GrpcProviderAdapterClientOptions? options = null)
        : this((channel ?? throw new ArgumentNullException(nameof(channel))).CreateCallInvoker(), options)
    {
    }

    public async Task<ProviderCallbackAck> SubmitAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        var request = ProtoMapper.ToProto(callback);
        var options = new CallOptions(deadline: DateTime.UtcNow + _options.NonFinancialCallTimeout, cancellationToken: cancellationToken);
        var ack = await _client.SubmitCallbackAsync(request, options).ResponseAsync.ConfigureAwait(false);
        return ProtoMapper.FromProto(ack);
    }
}
