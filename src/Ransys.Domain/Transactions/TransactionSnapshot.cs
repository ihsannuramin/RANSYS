using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;

namespace Ransys.Domain.Transactions;

/// <summary>
/// Complete persisted state of a <see cref="Transaction"/>, used only to rehydrate the aggregate
/// (Canonical Data Model §89–90: persistence rows are mapped to the aggregate, never exposed as it).
/// </summary>
public sealed record TransactionSnapshot(
    TransactionIdentity Identity,
    TransactionType Type,
    MerchantId MerchantId,
    ChannelId ChannelId,
    ProductId ProductId,
    Money Amount,
    FeeComponents? Fees,
    Money? ReserveAmount,
    Customer? Customer,
    TransactionEndpoint? Source,
    TransactionEndpoint? Destination,
    TransactionReferences References,
    RoutingDecision? Routing,
    TransactionConfigurationSnapshot Configuration,
    TransactionResultProjection? LatestProviderResult,
    ExtensionMetadata Metadata,
    ProcessingStatus ProcessingStatus,
    FinancialStatus FinancialStatus,
    ReconciliationStatus ReconciliationStatus,
    SettlementStatus SettlementStatus,
    string? ResponseCode,
    string? ReasonCode,
    string? ReasonDescription,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? FinancialPostedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt,
    long RowVersion)
{
    internal TransactionDraft ToDraft() => new(
        Identity, Type, MerchantId, ChannelId, ProductId, Amount, Customer, Source, Destination, References, Metadata, ReceivedAt);
}
