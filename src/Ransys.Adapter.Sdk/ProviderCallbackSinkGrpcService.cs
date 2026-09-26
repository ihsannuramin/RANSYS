using Grpc.Core;
using Ransys.Adapter.Contracts.V1;
using Wire = Ransys.Provider.V1;

namespace Ransys.Adapter.Sdk;

/// <summary>
/// Server binding of the Core callback sink (<c>ransys.provider.v1.ProviderCallbackSink</c>): maps the callback and
/// delegates to the injected <see cref="IProviderCallbackSink"/>. No business logic. A callback that cannot be
/// mapped is rejected with <see cref="StatusCode.InvalidArgument"/> and never reaches the sink.
/// </summary>
public class ProviderCallbackSinkGrpcService : Wire.ProviderCallbackSink.ProviderCallbackSinkBase
{
    private readonly IProviderCallbackSink _sink;

    public ProviderCallbackSinkGrpcService(IProviderCallbackSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public override async Task<Wire.ProviderCallbackAck> SubmitCallback(Wire.ProviderCallback request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        ProviderCallback callback;
        try
        {
            callback = ProtoMapper.FromProto(request);
        }
        catch (ProtoMappingException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        var ack = await _sink.SubmitAsync(callback, context.CancellationToken).ConfigureAwait(false);
        return ProtoMapper.ToProto(ack);
    }
}
