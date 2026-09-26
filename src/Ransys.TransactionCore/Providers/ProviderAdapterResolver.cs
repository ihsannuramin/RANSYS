using System.Collections.Concurrent;
using Ransys.Adapter.Contracts.V1;
using Ransys.Domain;

namespace Ransys.TransactionCore.Providers;

/// <summary>
/// Finds the adapter binding for a provider (Provider Adapter Contract v1 §1): an in-process adapter (Lite) or a gRPC
/// client (<c>GrpcProviderAdapterClient</c>) per configured endpoint. Returns null when no binding is registered; the
/// caller then knows for certain that nothing was sent (NOT_SENT, <c>requestSent = false</c>).
/// </summary>
public interface IProviderAdapterResolver
{
    IProviderAdapter? Resolve(ProviderId providerId);
}

/// <summary>
/// Adapters registered by <see cref="ProviderId"/>, for Lite deployments and tests. Thread-safe. Registering a provider
/// again replaces its binding.
/// </summary>
public sealed class InProcessProviderAdapterRegistry : IProviderAdapterResolver
{
    private readonly ConcurrentDictionary<ProviderId, IProviderAdapter> _adapters = new();

    public InProcessProviderAdapterRegistry Register(ProviderId providerId, IProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters[providerId] = adapter;
        return this;
    }

    public bool Unregister(ProviderId providerId) => _adapters.TryRemove(providerId, out _);

    public IProviderAdapter? Resolve(ProviderId providerId) => _adapters.GetValueOrDefault(providerId);
}
