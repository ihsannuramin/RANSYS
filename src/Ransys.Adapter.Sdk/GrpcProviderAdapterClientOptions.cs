namespace Ransys.Adapter.Sdk;

/// <summary>Deadlines used by <see cref="GrpcProviderAdapterClient"/> and <see cref="GrpcProviderCallbackSinkClient"/>.</summary>
public sealed class GrpcProviderAdapterClientOptions
{
    /// <summary>
    /// Added to <c>ConnectTimeout + ReadTimeout</c> of the request's timeout policy to form the gRPC deadline of a
    /// transaction call, so the adapter can report its own provider timeout before the Core-side deadline fires.
    /// </summary>
    public TimeSpan DeadlineGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Deadline for non-financial calls (capabilities, health, provider balance, callback submission).</summary>
    public TimeSpan NonFinancialCallTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
