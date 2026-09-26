using Ransys.Domain;

namespace Ransys.TransactionCore.Processing;

/// <summary>An ACTIVE channel of an ACTIVE merchant: the identity an authenticated API client acts as (M12e, ADR-022).</summary>
public sealed record ChannelIdentity(ChannelId ChannelId, MerchantId MerchantId);

/// <summary>Client → channel identity mapping used by request authentication.</summary>
public interface IChannelDirectory
{
    /// <summary>
    /// The channel and its merchant when both are ACTIVE; null when the channel is unknown, inactive, or its merchant is
    /// not ACTIVE (the caller rejects the request, fail closed). Throws when the store is unavailable.
    /// </summary>
    Task<ChannelIdentity?> FindActiveChannelAsync(ChannelId channelId, CancellationToken cancellationToken = default);
}
