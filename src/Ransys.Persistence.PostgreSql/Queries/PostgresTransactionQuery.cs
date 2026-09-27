using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Processing;

namespace Ransys.Persistence.PostgreSql.Queries;

/// <summary>
/// Read model for <c>GET /api/v1/transactions/{id}</c>, scoped by channel in SQL so another channel's transaction is
/// never read. STAN/RRN are the provider values of the latest attempt with a recorded outcome.
/// </summary>
public sealed class PostgresTransactionQuery : ITransactionQuery
{
    static PostgresTransactionQuery() => DbValues.EnsureConfigured();

    public async Task<TransactionQueryRow?> FindForChannelAsync(
        IDatabaseSession session, ChannelId channelId, TransactionId transactionId, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT t.ransys_transaction_id, t.original_transaction_id, t.channel_id, t.client_reference, t.transaction_type,
                   t.processing_status, t.financial_status, t.reconciliation_status, t.settlement_status,
                   t.ransys_response_code, t.canonical_detail -> 'references' ->> 'merchantReference' AS merchant_reference,
                   COALESCE(t.latest_result_provider_stan, a.provider_stan) AS provider_stan,
                   COALESCE(t.latest_result_provider_rrn, a.provider_rrn) AS provider_rrn,
                   t.received_at, t.completed_at
            FROM core.transactions t
            LEFT JOIN LATERAL (
                SELECT provider_stan, provider_rrn
                FROM core.transaction_attempts
                WHERE ransys_transaction_id = t.ransys_transaction_id AND outcome_recorded_at IS NOT NULL
                ORDER BY attempt_no DESC
                LIMIT 1
            ) a ON true
            WHERE t.ransys_transaction_id = @Id AND t.channel_id = @ChannelId
            """,
            new { Id = transactionId.Value, ChannelId = channelId.Value }, s.Transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        return new TransactionQueryRow(
            new TransactionId(row.RansysTransactionId),
            row.OriginalTransactionId is { } original ? new TransactionId(original) : null,
            new ChannelId(row.ChannelId),
            row.ClientReference,
            CanonicalCodes.TransactionType.Parse(row.TransactionType),
            CanonicalCodes.ProcessingStatus.Parse(row.ProcessingStatus),
            CanonicalCodes.FinancialStatus.Parse(row.FinancialStatus),
            CanonicalCodes.ReconciliationStatus.Parse(row.ReconciliationStatus),
            CanonicalCodes.SettlementStatus.Parse(row.SettlementStatus),
            row.RansysResponseCode?.Trim(),
            row.MerchantReference,
            row.ProviderStan,
            row.ProviderRrn,
            DbValues.FromDb(row.ReceivedAt),
            DbValues.FromDb(row.CompletedAt));
    }

    private sealed class Row
    {
        public Guid RansysTransactionId { get; init; }

        public Guid? OriginalTransactionId { get; init; }

        public Guid ChannelId { get; init; }

        public string ClientReference { get; init; } = "";

        public string TransactionType { get; init; } = "";

        public string ProcessingStatus { get; init; } = "";

        public string FinancialStatus { get; init; } = "";

        public string ReconciliationStatus { get; init; } = "";

        public string SettlementStatus { get; init; } = "";

        public string? RansysResponseCode { get; init; }

        public string? MerchantReference { get; init; }

        public string? ProviderStan { get; init; }

        public string? ProviderRrn { get; init; }

        public DateTime ReceivedAt { get; init; }

        public DateTime? CompletedAt { get; init; }
    }
}
