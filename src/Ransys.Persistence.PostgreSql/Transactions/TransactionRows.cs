namespace Ransys.Persistence.PostgreSql.Transactions;

// Persistence row shapes (Canonical Data Model §89–90): never exposed outside the persistence layer.

/// <summary><c>core.transactions</c> joined with its currency definition and routing providers.</summary>
internal sealed class TransactionRow
{
    public Guid RansysTransactionId { get; init; }

    public Guid MerchantId { get; init; }

    public Guid ChannelId { get; init; }

    public Guid ProductId { get; init; }

    public string TransactionType { get; init; } = "";

    public string ClientReference { get; init; } = "";

    public string? IdempotencyKey { get; init; }

    public string TransactionFingerprint { get; init; } = "";

    public Guid? OriginalTransactionId { get; init; }

    public decimal Amount { get; init; }

    public decimal MerchantChargeAmount { get; init; }

    public decimal TotalReserveAmount { get; init; }

    public string CurrencyCode { get; init; } = "";

    public int CurrencyVersionNo { get; init; }

    public short CurrencyScale { get; init; }

    public string ProcessingStatus { get; init; } = "";

    public string FinancialStatus { get; init; } = "";

    public string ReconciliationStatus { get; init; } = "";

    public string SettlementStatus { get; init; } = "";

    public string? RansysResponseCode { get; init; }

    public string? ReasonCode { get; init; }

    public string? ReasonDescription { get; init; }

    public Guid? InitialSelectedProviderId { get; init; }

    public string? InitialProviderCode { get; init; }

    public string? InitialAdapterServiceName { get; init; }

    public Guid? ActualProviderId { get; init; }

    public string? ActualProviderCode { get; init; }

    public string? ActualAdapterServiceName { get; init; }

    public long? RoutingRuleVersion { get; init; }

    public int FailoverCount { get; init; }

    public string? FailoverReason { get; init; }

    public DateTime? RoutingDecidedAt { get; init; }

    public Guid? ConfigVersionId { get; init; }

    public Guid? RoutingConfigVersionId { get; init; }

    public Guid? FeeConfigVersionId { get; init; }

    public long? ProviderPolicyVersion { get; init; }

    public string Metadata { get; init; } = "{}";

    public string CanonicalDetail { get; init; } = "{}";

    /// <summary>ADR-027: the latest provider evidence for this transaction, from any source (always overwritable).</summary>
    public string? LatestResultProviderReference { get; init; }

    public string? LatestResultProviderStan { get; init; }

    public string? LatestResultProviderRrn { get; init; }

    public string? LatestResultData { get; init; }

    public string? LatestResultSource { get; init; }

    public DateTime? LatestResultRecordedAt { get; init; }

    public DateTime ReceivedAt { get; init; }

    public DateTime? ValidatedAt { get; init; }

    public DateTime? FinancialPostedAt { get; init; }

    public DateTime? CompletedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public long RowVersion { get; init; }
}

/// <summary><c>core.transaction_fee_components</c>.</summary>
internal sealed class FeeComponentRow
{
    public string ComponentType { get; init; } = "";

    public decimal ChargedAmount { get; init; }

    public decimal AccountingAmount { get; init; }

    public string BeneficiaryType { get; init; } = "";

    public Guid? BeneficiaryId { get; init; }

    public bool Refundable { get; init; }

    public string RefundPolicy { get; init; } = "NONE";

    public long? CalculationRuleVersion { get; init; }
}
