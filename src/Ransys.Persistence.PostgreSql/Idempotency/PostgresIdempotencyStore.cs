using Dapper;
using Npgsql;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Idempotency;

namespace Ransys.Persistence.PostgreSql.Idempotency;

/// <summary>
/// <c>core.idempotency_records</c>. Uniqueness of the active claim is enforced by the partial unique index
/// <c>ux_idempotency_active_reference (channel_id, client_reference) WHERE active</c> (DDL v1.1).
/// </summary>
public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    private const string UniqueViolation = "23505";
    private const string ActiveReferenceIndex = "ux_idempotency_active_reference";

    static PostgresIdempotencyStore() => DbValues.EnsureConfigured();

    public async Task<Result<IdempotencyRecord?>> FindActiveAsync(
        IDatabaseSession session, ChannelId channelId, string clientReference, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            """
            SELECT idempotency_record_id, channel_id, client_reference, idempotency_key, fingerprint,
                   ransys_transaction_id, active, created_at, expires_at
            FROM core.idempotency_records
            WHERE channel_id = @ChannelId AND client_reference = @ClientReference AND active
            """,
            new { ChannelId = channelId.Value, ClientReference = clientReference }, s.Transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return Result<IdempotencyRecord?>.Success(null);
        }

        var fingerprint = TransactionFingerprint.Parse(row.Fingerprint);
        if (fingerprint.IsFailure)
        {
            return new RansysError(
                ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal,
                $"Idempotency record {row.IdempotencyRecordId} has an invalid fingerprint.");
        }

        return new IdempotencyRecord(
            row.IdempotencyRecordId, new ChannelId(row.ChannelId), row.ClientReference, row.IdempotencyKey, fingerprint.Value,
            new TransactionId(row.RansysTransactionId), row.Active, DbValues.FromDb(row.CreatedAt), DbValues.FromDb(row.ExpiresAt));
    }

    public async Task<bool> ExpireAsync(IDatabaseSession session, Guid recordId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var affected = await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE core.idempotency_records
            SET active = false, expired_at = @Now
            WHERE idempotency_record_id = @Id AND active AND expires_at <= @Now
            """,
            new { Id = recordId, Now = DbValues.ToDb(now) }, s.Transaction, cancellationToken: cancellationToken));
        return affected == 1;
    }

    public async Task<bool> TryInsertAsync(IDatabaseSession session, IdempotencyRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var s = Pg(session);
        try
        {
            await s.Connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO core.idempotency_records
                    (idempotency_record_id, channel_id, client_reference, idempotency_key, fingerprint,
                     ransys_transaction_id, active, created_at, expires_at)
                VALUES (@Id, @ChannelId, @ClientReference, @IdempotencyKey, @Fingerprint, @TransactionId, true,
                        @CreatedAt, @ExpiresAt)
                """,
                new
                {
                    record.Id,
                    ChannelId = record.ChannelId.Value,
                    record.ClientReference,
                    record.IdempotencyKey,
                    Fingerprint = record.Fingerprint.ToPersistedString(),
                    TransactionId = record.TransactionId.Value,
                    CreatedAt = DbValues.ToDb(record.CreatedAt),
                    ExpiresAt = DbValues.ToDb(record.ExpiresAt),
                },
                s.Transaction, cancellationToken: cancellationToken));
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation && ex.ConstraintName == ActiveReferenceIndex)
        {
            return false;
        }
    }

    public async Task<int> ExpireDueAsync(IDatabaseSession session, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var s = Pg(session);

        // SKIP LOCKED lets several sweepers (or a request expiring lazily) run without blocking each other.
        return await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE core.idempotency_records r
            SET active = false, expired_at = @Now
            WHERE r.idempotency_record_id IN (
                SELECT idempotency_record_id
                FROM core.idempotency_records
                WHERE active AND expires_at <= @Now
                ORDER BY expires_at
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED)
            """,
            new { Now = DbValues.ToDb(now), BatchSize = batchSize }, s.Transaction, cancellationToken: cancellationToken));
    }

    private static PostgresSession Pg(IDatabaseSession session) =>
        session as PostgresSession
        ?? throw new ArgumentException($"Expected a {nameof(PostgresSession)}.", nameof(session));

    private sealed class Row
    {
        public Guid IdempotencyRecordId { get; init; }

        public Guid ChannelId { get; init; }

        public string ClientReference { get; init; } = "";

        public string? IdempotencyKey { get; init; }

        public string Fingerprint { get; init; } = "";

        public Guid RansysTransactionId { get; init; }

        public bool Active { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime ExpiresAt { get; init; }
    }
}
