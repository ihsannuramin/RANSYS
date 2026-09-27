using System.Collections.Immutable;
using System.Text.Json;
using Ransys.Adapter.Contracts.V1;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Providers;

/// <summary>Timeouts passed to adapters in <see cref="ProviderExecutionContext"/> and enforced by <see cref="ProviderInvoker"/>.</summary>
/// <remarks>
/// TODO / Architecture Decision Required: per-provider policy (<c>integration.provider_policies</c>) and the
/// Configuration Schema v1 are not designed yet; until then one policy applies to every provider. MaxRetry is always 0:
/// Core never lets an adapter retry a financial request after a possible send (Provider Adapter Contract v1 §12).
/// </remarks>
public sealed record ProviderCallPolicy(TimeSpan ConnectTimeout, TimeSpan ReadTimeout)
{
    public static ProviderCallPolicy Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

    /// <summary>Total time Core waits for an adapter call before treating it as possibly sent (IN_DOUBT).</summary>
    public TimeSpan Budget => ConnectTimeout + ReadTimeout;
}

/// <summary>
/// References of the original transaction as the provider knows them (from the original's last attempt with a recorded
/// outcome). Child requests (reversal, refund, void) carry them so the adapter can address the original at the provider
/// (Provider Adapter Contract v1 §4).
/// </summary>
public sealed record OriginalProviderReferences(string? ProviderReference, string? ProviderStan, string? ProviderRrn)
{
    public static OriginalProviderReferences None { get; } = new(null, null, null);

    /// <summary>Takes the references of the latest attempt with a recorded outcome that carries any provider reference.</summary>
    public static OriginalProviderReferences From(IEnumerable<TransactionAttempt> originalAttempts)
    {
        ArgumentNullException.ThrowIfNull(originalAttempts);

        var outcome = originalAttempts
            .Where(a => a.Outcome is { } o && (o.ProviderReference ?? o.ProviderStan ?? o.ProviderRrn) is not null)
            .OrderByDescending(a => a.AttemptNumber)
            .Select(a => a.Outcome!)
            .FirstOrDefault();
        return outcome is null ? None : new(outcome.ProviderReference, outcome.ProviderStan, outcome.ProviderRrn);
    }

    /// <summary>
    /// ADR-027 (V3 fix): prefers the original transaction's latest-provider-result projection (a later/final async
    /// report, e.g. a callback that arrived after the attempt outcome that first resolved the original was already
    /// immutable) over the per-attempt scan, which only sees the outcome recorded at the time. Shares the exact same
    /// "projection exists → trust it, even when its own reference/STAN/RRN are all null" rule as GET
    /// (<c>PostgresTransactionQuery</c>) and replay (<c>TransactionProcessingService.Build</c>): a final accepted
    /// report with no reference is not the same thing as no report at all, so it must not fall back to an older,
    /// possibly superseded provisional reference from <paramref name="originalAttempts"/> (e.g. a PENDING callback's
    /// temporary reference on an attempt outcome that a later, reference-less final SUCCESS/FAILED superseded). The
    /// attempt scan is used only when there is genuinely no projection yet (a legacy transaction that only ever went
    /// through the sync path, before ADR-027 existed).
    /// </summary>
    public static OriginalProviderReferences From(Transaction original, IEnumerable<TransactionAttempt> originalAttempts)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (original.LatestProviderResult?.Evidence is { } evidence)
        {
            return new(evidence.ProviderReference, evidence.ProviderStan, evidence.ProviderRrn);
        }

        return From(originalAttempts);
    }
}

/// <summary>
/// Builds the V1 <see cref="ProviderTransactionRequest"/> for one attempt (Provider Adapter Contract v1 §5, §24).
/// Pure: everything it needs is passed in. Canonical objects become JSON dictionaries with camelCase keys; metadata is
/// forwarded as governed extension data only.
/// </summary>
public sealed class ProviderRequestFactory(ProviderCallPolicy policy)
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Empty = ImmutableDictionary<string, JsonElement>.Empty;

    public ProviderRequestFactory()
        : this(ProviderCallPolicy.Default)
    {
    }

    /// <param name="transaction">The transaction whose attempt is sent (a child for reversal/refund/void).</param>
    /// <param name="attempt">The started attempt; its provider must be the transaction's current routed provider.</param>
    /// <param name="productCode">Product code of <see cref="Transaction.ProductId"/> (reference data).</param>
    /// <param name="original">The original transaction for child types; null otherwise.</param>
    /// <param name="originalReferences">Provider references of the original (child types).</param>
    public ProviderTransactionRequest Create(
        Transaction transaction,
        TransactionAttempt attempt,
        string productCode,
        Transaction? original = null,
        OriginalProviderReferences? originalReferences = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);

        if (attempt.TransactionId != transaction.Id)
        {
            throw new ArgumentException("The attempt does not belong to the transaction.", nameof(attempt));
        }

        if (TransactionTypeRules.RequiresOriginalTransaction(transaction.Type)
            && (original is null || transaction.Identity.OriginalTransactionId != original.Id))
        {
            throw new ArgumentException("Child transactions need their original transaction.", nameof(original));
        }

        var carriesAmount = transaction.Type is not (TransactionType.Inquiry or TransactionType.BalanceInquiry);
        var provider = attempt.Provider;
        var references = BuildReferences(transaction, original is null ? null : originalReferences ?? OriginalProviderReferences.None);

        return new ProviderTransactionRequest(
            transaction.Id.Value,
            transaction.Identity.OriginalTransactionId?.Value,
            attempt.Id.Value,
            CanonicalCodes.TransactionType.ToCode(transaction.Type),
            productCode,
            carriesAmount ? transaction.Amount.Amount : null,
            carriesAmount ? transaction.Amount.CurrencyCode : null,
            carriesAmount ? transaction.Amount.CurrencyDefinitionVersion : null,
            Customer(transaction.Customer),
            Endpoint(transaction.Source),
            Endpoint(transaction.Destination),
            references,
            new ProviderExecutionContext(
                new ProviderIdentity(provider.ProviderId.Value, provider.ProviderCode, provider.AdapterService),
                EndpointProfileId: null,
                ProductMappingVersion: null,
                new ProviderTimeoutPolicy(
                    policy.ConnectTimeout, policy.ReadTimeout, MaxRetry: 0, RetryDelay: TimeSpan.Zero, RetryBackoff: "NONE",
                    StatusCheckAfterTimeout: false, ReversalAfterTimeout: false),
                new ProviderConcurrencyPolicy(null, null, null),
                ImmutableHashSet<string>.Empty,
                AuthenticationProfileReference: null,
                PolicyVersion: transaction.Configuration.ProviderPolicyVersion ?? 0),
            new CorrelationContext(transaction.Id.Value, attempt.CorrelationId, attempt.TraceId),
            Metadata(transaction.Metadata));
    }

    /// <summary>
    /// For a child the provider references are the original's (the provider acted on the original); the client and
    /// merchant references stay the child's own.
    /// </summary>
    private static ProviderTransactionReferences BuildReferences(Transaction transaction, OriginalProviderReferences? original)
    {
        var refs = transaction.References;
        var external = refs.ExternalReferences
            .GroupBy(r => r.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

        return new ProviderTransactionReferences(
            refs.ClientReference,
            refs.MerchantReference,
            refs.Stan,
            refs.Rrn,
            original?.ProviderReference ?? refs.ProviderReference,
            original?.ProviderStan ?? refs.ProviderStan,
            original?.ProviderRrn ?? refs.ProviderRrn,
            external);
    }

    private static IReadOnlyDictionary<string, JsonElement> Customer(Customer? customer)
    {
        if (customer is null)
        {
            return Empty;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Add(values, "customerId", customer.CustomerId);
        Add(values, "externalCustomerReference", customer.ExternalCustomerReference);
        Add(values, "accountNumber", customer.AccountNumber);
        Add(values, "phoneNumber", customer.PhoneNumber);
        Add(values, "name", customer.Name);
        AddMetadata(values, customer.Metadata);
        return values;
    }

    private static IReadOnlyDictionary<string, JsonElement> Endpoint(TransactionEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return Empty;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Add(values, "type", CanonicalCodes.EndpointType.ToCode(endpoint.Type));
        Add(values, "identifier", endpoint.Identifier);
        Add(values, "institutionCode", endpoint.InstitutionCode);
        Add(values, "accountReference", endpoint.AccountReference);
        AddMetadata(values, endpoint.Metadata);
        return values;
    }

    private static IReadOnlyDictionary<string, JsonElement> Metadata(ExtensionMetadata metadata) =>
        metadata.Count == 0 ? Empty : metadata.Values.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);

    private static void Add(Dictionary<string, JsonElement> values, string key, string? value)
    {
        if (value is not null)
        {
            values[key] = JsonSerializer.SerializeToElement(value);
        }
    }

    private static void AddMetadata(Dictionary<string, JsonElement> values, ExtensionMetadata metadata)
    {
        if (metadata.Count > 0)
        {
            values["metadata"] = JsonSerializer.SerializeToElement(metadata.Values);
        }
    }
}
