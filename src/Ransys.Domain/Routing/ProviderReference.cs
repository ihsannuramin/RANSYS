using Ransys.Domain.Common;

namespace Ransys.Domain.Routing;

/// <summary>
/// Provider identity (Canonical Data Model §26). <see cref="ProviderId"/> is authoritative;
/// display names are a presentation concern.
/// </summary>
public sealed record ProviderReference
{
    /// <summary>DDL v1.1 <c>provider_code varchar(64)</c>.</summary>
    public const int MaxProviderCodeLength = 64;

    /// <summary>DDL v1.1 <c>adapter_service_name varchar(128)</c>.</summary>
    public const int MaxAdapterServiceLength = 128;

    private ProviderReference(ProviderId providerId, string providerCode, string adapterService)
    {
        ProviderId = providerId;
        ProviderCode = providerCode;
        AdapterService = adapterService;
    }

    public ProviderId ProviderId { get; }

    public string ProviderCode { get; }

    public string AdapterService { get; }

    public static Result<ProviderReference> Create(ProviderId providerId, string? providerCode, string? adapterService)
    {
        var error = Text.FirstError(
            Text.Required(providerCode, "provider.providerCode", MaxProviderCodeLength),
            Text.Required(adapterService, "provider.adapterService", MaxAdapterServiceLength));

        return error is null
            ? new ProviderReference(providerId, providerCode!, adapterService!)
            : error;
    }
}
