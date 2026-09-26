using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Domain.Monetary;
using Ransys.TransactionCore.Processing;

namespace Ransys.Persistence.PostgreSql.ReferenceData;

/// <summary>Reads products, currency definitions and merchant wallets for request intake (M12d). Read-only.</summary>
public sealed class PostgresReferenceDataReader : IReferenceDataReader
{
    static PostgresReferenceDataReader() => DbValues.EnsureConfigured();

    public async Task<ProductInfo?> FindActiveProductByCodeAsync(
        IDatabaseSession session, string productCode, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<(Guid Id, string Code)?>(new CommandDefinition(
            "SELECT product_id, product_code FROM core.products WHERE product_code = @Code AND status = 'ACTIVE'",
            new { Code = productCode }, s.Transaction, cancellationToken: cancellationToken));
        return row is { } r ? new ProductInfo(new ProductId(r.Id), r.Code) : null;
    }

    public async Task<string?> FindProductCodeAsync(IDatabaseSession session, ProductId productId, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        return await s.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT product_code FROM core.products WHERE product_id = @Id",
            new { Id = productId.Value }, s.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<Result<CurrencyDefinition?>> FindActiveCurrencyAsync(
        IDatabaseSession session, string currencyCode, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<(string Code, int Version, short Scale)?>(new CommandDefinition(
            """
            SELECT currency_code, version_no, scale
            FROM core.currency_definitions
            WHERE currency_code = @Code
              AND status = 'ACTIVE'
              AND effective_from <= @At
              AND (effective_until IS NULL OR effective_until > @At)
            ORDER BY version_no DESC
            LIMIT 1
            """,
            new { Code = currencyCode, At = DbValues.ToDb(at) }, s.Transaction, cancellationToken: cancellationToken));
        if (row is not { } r)
        {
            return Result<CurrencyDefinition?>.Success(null);
        }

        var currency = CurrencyDefinition.Create(r.Code.Trim(), r.Version, r.Scale);
        return currency.IsSuccess
            ? currency.Value
            : new RansysError(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal, $"Currency definition {r.Code} v{r.Version} is invalid.");
    }

    public async Task<Result<WalletInfo?>> FindMerchantMainWalletAsync(
        IDatabaseSession session, MerchantId merchantId, CurrencyDefinition currency, CancellationToken cancellationToken = default)
    {
        var s = PostgresSessionCast.From(session);

        // ux_wallet_main: at most one main wallet (product_id IS NULL) per merchant + currency definition.
        var row = await s.Connection.QuerySingleOrDefaultAsync<(Guid Id, string Status)?>(new CommandDefinition(
            """
            SELECT w.wallet_id, w.status
            FROM ledger.wallets w
            JOIN core.currency_definitions cd ON cd.currency_definition_id = w.currency_definition_id
            WHERE w.merchant_id = @MerchantId
              AND w.product_id IS NULL
              AND cd.currency_code = @Code
              AND cd.version_no = @Version
              AND w.status IN ('ACTIVE', 'FROZEN')
            """,
            new { MerchantId = merchantId.Value, currency.Code, currency.Version }, s.Transaction, cancellationToken: cancellationToken));
        if (row is not { } r)
        {
            return Result<WalletInfo?>.Success(null);
        }

        return CanonicalCodes.WalletStatus.TryParse(r.Status, out var status)
            ? new WalletInfo(new WalletId(r.Id), status)
            : new RansysError(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal, $"Wallet {r.Id} has an unknown status.");
    }
}
