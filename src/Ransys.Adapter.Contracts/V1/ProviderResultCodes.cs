namespace Ransys.Adapter.Contracts.V1;

/// <summary>
/// <see cref="ProviderResult.RansysResponseCode"/> values for results that the contract helpers and the SDK
/// synthesize themselves (no provider mapping involved).
/// </summary>
/// <remarks>
/// TODO / Architecture Decision Required: the RANSYS Response Code Catalog v1 does not exist yet
/// (OpenAPI v1 §"responseCode", Canonical Data Model §"Response Code Catalog"). Until it does, these reuse only
/// the example codes already approved in Architecture Spec §21 / PRD; no new permanent code is invented here.
/// Replace them when the catalog is published.
/// </remarks>
public static class ProviderResultCodes
{
    /// <summary>1002 RANSYS_PROCESSING_TIMEOUT: result unknown / possibly sent (IN_DOUBT).</summary>
    public const string InDoubt = "1002";

    /// <summary>1001 RANSYS_INTERNAL_ERROR: request could not be built or mapped; never sent.</summary>
    public const string InternalError = "1001";

    /// <summary>0091 PROVIDER_LINK_DOWN: connection to the adapter/provider could not be established; never sent.</summary>
    public const string ProviderLinkDown = "0091";

    /// <summary>
    /// 5001 NO_ROUTE_AVAILABLE, interim code for an operation the adapter does not support
    /// (capability failure, §3). TODO: replace with the catalog's capability code.
    /// </summary>
    public const string CapabilityUnsupported = "5001";
}
