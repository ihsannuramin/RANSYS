using Ransys.Application;
using Ransys.Domain.Common;

namespace Ransys.Configuration;

/// <summary>Configuration domains (<c>config.config_versions.config_domain</c>).</summary>
public static class ConfigDomains
{
    public const string Routing = "ROUTING";
    public const string Fee = "FEE";
}

/// <summary>One configuration version (Architecture Spec §34).</summary>
public sealed record ConfigVersion(Guid Id, string Domain, long VersionNo, DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveUntil);

public interface IConfigVersionStore
{
    /// <summary>
    /// The ACTIVE version of <paramref name="domain"/> effective at <paramref name="at"/>
    /// (highest version number when several overlap), or null.
    /// </summary>
    Task<ConfigVersion?> GetActiveAsync(IDatabaseSession session, string domain, DateTimeOffset at, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read side of the versioned configuration lifecycle (DRAFT → PENDING_APPROVAL → APPROVED → SCHEDULED → ACTIVE →
/// EXPIRED). Changing configuration (maker-checker, resync) belongs to the configuration schema/API design, which
/// is deferred (main.md §20).
/// </summary>
public sealed class ConfigurationService(IConfigVersionStore store, IClock clock)
{
    /// <summary>Fails closed with CONFIGURATION_NOT_AVAILABLE when no version is active.</summary>
    public async Task<Result<ConfigVersion>> GetActiveAsync(
        IDatabaseSession session, string domain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var version = await store.GetActiveAsync(session, domain, clock.UtcNow, cancellationToken);
        return version is not null
            ? version
            : new RansysError(ErrorCodes.ConfigurationNotAvailable, ErrorCategory.Routing, $"No active {domain} configuration version.");
    }
}
