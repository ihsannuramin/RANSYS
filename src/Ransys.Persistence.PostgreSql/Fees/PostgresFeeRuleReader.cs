using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Monetary;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Fees;

namespace Ransys.Persistence.PostgreSql.Fees;

/// <summary>Reads <c>config.fee_rules</c> of one FEE configuration version (ADR-020). Read-only.</summary>
public sealed class PostgresFeeRuleReader : IFeeRuleReader
{
    static PostgresFeeRuleReader() => DbValues.EnsureConfigured();

    public async Task<IReadOnlyList<FeeRule>> GetRulesAsync(
        IDatabaseSession session,
        Guid configVersionId,
        ProductId productId,
        TransactionType transactionType,
        MerchantId merchantId,
        CurrencyDefinition currency,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var rows = await s.Connection.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT r.fee_rule_id, r.merchant_id, r.fee_type, r.fee_value, r.minimum_fee, r.maximum_fee
            FROM config.fee_rules r
            JOIN core.currency_definitions cd ON cd.currency_definition_id = r.currency_definition_id
            WHERE r.config_version_id = @ConfigVersionId
              AND r.product_id = @ProductId
              AND r.transaction_type = @TransactionType
              AND (r.merchant_id IS NULL OR r.merchant_id = @MerchantId)
              AND cd.currency_code = @Code
              AND cd.version_no = @Version
              AND r.effective_from <= @At
              AND (r.effective_until IS NULL OR r.effective_until > @At)
            ORDER BY r.fee_rule_id
            """,
            new
            {
                ConfigVersionId = configVersionId,
                ProductId = productId.Value,
                TransactionType = CanonicalCodes.TransactionType.ToCode(transactionType),
                MerchantId = merchantId.Value,
                currency.Code,
                currency.Version,
                At = DbValues.ToDb(at),
            },
            s.Transaction, cancellationToken: cancellationToken));

        return rows.Select(r => new FeeRule(
                r.FeeRuleId,
                r.MerchantId is { } m ? new MerchantId(m) : null,
                r.FeeType switch
                {
                    "FIXED" => FeeRuleType.Fixed,
                    "PERCENTAGE" => FeeRuleType.Percentage,
                    _ => throw new InvalidOperationException($"Fee rule {r.FeeRuleId} has unknown fee_type '{r.FeeType}'."),
                },
                r.FeeValue,
                r.MinimumFee,
                r.MaximumFee))
            .ToList();
    }

    private sealed class Row
    {
        public Guid FeeRuleId { get; init; }

        public Guid? MerchantId { get; init; }

        public string FeeType { get; init; } = "";

        public decimal FeeValue { get; init; }

        public decimal? MinimumFee { get; init; }

        public decimal? MaximumFee { get; init; }
    }
}
