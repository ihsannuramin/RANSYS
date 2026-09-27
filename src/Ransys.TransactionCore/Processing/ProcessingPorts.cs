using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Domain.Monetary;
using Ransys.Domain.Transactions;

namespace Ransys.TransactionCore.Processing;

/// <summary>An ACTIVE product (<c>core.products</c>).</summary>
public sealed record ProductInfo(ProductId ProductId, string ProductCode);

/// <summary>A merchant's main wallet (<c>ledger.wallets</c> with <c>product_id IS NULL</c>) for one currency definition.</summary>
public sealed record WalletInfo(WalletId WalletId, WalletStatus Status);

/// <summary>Reference data Transaction Core needs to accept a request (M12d).</summary>
public interface IReferenceDataReader
{
    /// <summary>The product with <paramref name="productCode"/> if it is ACTIVE; null otherwise.</summary>
    Task<ProductInfo?> FindActiveProductByCodeAsync(IDatabaseSession session, string productCode, CancellationToken cancellationToken = default);

    /// <summary>Product code of a product (any status), for provider requests of child transactions.</summary>
    Task<string?> FindProductCodeAsync(IDatabaseSession session, ProductId productId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The id of the product with <paramref name="productCode"/>, whatever its status (any status; existence only). Used
    /// to compare an idempotent replay's payload against the original transaction's own product, which must not depend
    /// on whether the product is still ACTIVE now (ADR-025).
    /// </summary>
    Task<ProductId?> FindProductIdByCodeAsync(IDatabaseSession session, string productCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// The ACTIVE currency definition for <paramref name="currencyCode"/> effective at <paramref name="at"/> (highest version
    /// when several overlap); null when none. Public clients never send a definition version (OpenAPI v1 §5).
    /// </summary>
    Task<Result<CurrencyDefinition?>> FindActiveCurrencyAsync(
        IDatabaseSession session, string currencyCode, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The merchant's main wallet in <paramref name="currency"/> when it is ACTIVE or FROZEN; null when missing or CLOSED.</summary>
    Task<Result<WalletInfo?>> FindMerchantMainWalletAsync(
        IDatabaseSession session, MerchantId merchantId, CurrencyDefinition currency, CancellationToken cancellationToken = default);
}

/// <summary>Public view of one transaction for <c>GET /api/v1/transactions/{id}</c> (OpenAPI v1 §8). No internals.</summary>
public sealed record TransactionDetailView(
    TransactionId TransactionId,
    TransactionId? OriginalTransactionId,
    string ClientReference,
    TransactionType TransactionType,
    ProcessingStatus ProcessingStatus,
    FinancialStatus FinancialStatus,
    ReconciliationStatus ReconciliationStatus,
    SettlementStatus SettlementStatus,
    string ResponseCode,
    string ResponseMessage,
    PublicReferences References,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Raw read-model row; <see cref="TransactionProcessingService.GetTransactionAsync"/> derives the public view.</summary>
public sealed record TransactionQueryRow(
    TransactionId TransactionId,
    TransactionId? OriginalTransactionId,
    ChannelId ChannelId,
    string ClientReference,
    TransactionType TransactionType,
    ProcessingStatus ProcessingStatus,
    FinancialStatus FinancialStatus,
    ReconciliationStatus ReconciliationStatus,
    SettlementStatus SettlementStatus,
    string? ResponseCode,
    string? MerchantReference,
    string? Stan,
    string? Rrn,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Read model for transaction detail, scoped to the authenticated channel.</summary>
public interface ITransactionQuery
{
    /// <summary>The transaction if it exists <em>and</em> belongs to <paramref name="channelId"/>; otherwise null.</summary>
    Task<TransactionQueryRow?> FindForChannelAsync(
        IDatabaseSession session, ChannelId channelId, TransactionId transactionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A mandatory durable dependency (Transaction DB / ledger) was unavailable before any provider call. Nothing was sent to
/// a provider; the API returns 503 (OpenAPI v1 §6: "cannot establish durable transaction + reserve ⇒ no provider financial
/// request"). Whether the transaction row exists is unknown only if the commit itself failed; a retry with the same
/// client reference is safe (idempotency).
/// </summary>
public sealed class FinancialDependencyUnavailableException : Exception
{
    public FinancialDependencyUnavailableException()
        : base("A mandatory financial dependency is unavailable; no provider request was made.")
    {
    }

    public FinancialDependencyUnavailableException(string message)
        : base(message)
    {
    }

    public FinancialDependencyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
