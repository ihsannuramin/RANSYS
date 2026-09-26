namespace Ransys.Adapter.Contracts.V1;

/// <summary>Values of <see cref="ProviderError.Category"/> (Provider Adapter Contract v1 §21).</summary>
public static class ProviderErrorCategories
{
    public const string Connection = "CONNECTION";
    public const string Timeout = "TIMEOUT";
    public const string Protocol = "PROTOCOL";
    public const string Authentication = "AUTHENTICATION";
    public const string Mapping = "MAPPING";
    public const string ProviderDecline = "PROVIDER_DECLINE";
    public const string CapabilityUnsupported = "CAPABILITY_UNSUPPORTED";
    public const string Backpressure = "BACKPRESSURE";
    public const string Unknown = "UNKNOWN";

    /// <summary>All categories, in contract order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Connection, Timeout, Protocol, Authentication, Mapping, ProviderDecline, CapabilityUnsupported, Backpressure,
        Unknown,
    ];
}
