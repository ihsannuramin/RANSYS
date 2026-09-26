using Dapper;
using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Persistence.PostgreSql.ReferenceData;

/// <summary>Read access to master/reference data needed to map aggregates to rows.</summary>
public sealed class ReferenceDataStore
{
    static ReferenceDataStore() => DbValues.EnsureConfigured();

    /// <summary>
    /// Resolves <c>currency_definition_id</c> for a currency snapshot (code + version) and verifies the
    /// persisted scale matches. A mismatch means configuration and transaction disagree: fail closed.
    /// </summary>
    public async Task<Result<Guid>> ResolveCurrencyDefinitionIdAsync(
        PostgresSession session, CurrencyDefinition currency, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(currency);

        const string sql = """
            SELECT currency_definition_id AS Id, scale AS Scale
            FROM core.currency_definitions
            WHERE currency_code = @Code AND version_no = @Version
            """;

        var row = await session.Connection.QuerySingleOrDefaultAsync<(Guid Id, short Scale)?>(new CommandDefinition(
            sql, new { currency.Code, currency.Version }, session.Transaction, cancellationToken: cancellationToken));

        if (row is null)
        {
            return new RansysError(
                ErrorCodes.ReferenceDataNotFound, ErrorCategory.Internal, $"Currency definition {currency} does not exist.");
        }

        if (row.Value.Scale != currency.Scale)
        {
            return new RansysError(
                ErrorCodes.PersistedStateInvalid,
                ErrorCategory.Internal,
                $"Currency definition {currency.Code} v{currency.Version} has scale {row.Value.Scale} in the database, not {currency.Scale}.");
        }

        return row.Value.Id;
    }
}
