using Dapper;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Ledger;
using Ransys.Domain.Monetary;
using Ransys.Ledger;
using Ransys.Persistence.PostgreSql.ReferenceData;

namespace Ransys.Persistence.PostgreSql.Ledger;

/// <summary>
/// PostgreSQL implementation of <see cref="ILedgerStore"/>. Wallet and reservation reads used for mutation take
/// row locks (<c>FOR UPDATE</c>) so the balance check and the change are atomic (Architecture Spec §11).
/// Ledger rows are only ever inserted; there is no update path for posted journals (ADR-002).
/// </summary>
public sealed class PostgresLedgerStore : ILedgerStore
{
    private readonly ReferenceDataStore _referenceData;

    static PostgresLedgerStore() => DbValues.EnsureConfigured();

    public PostgresLedgerStore(ReferenceDataStore referenceData) =>
        _referenceData = referenceData ?? throw new ArgumentNullException(nameof(referenceData));

    public async Task<WalletId?> FindReservationWalletAsync(
        IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var id = await s.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT wallet_id FROM ledger.balance_reservations WHERE ransys_transaction_id = @Id",
            new { Id = transactionId.Value }, s.Transaction, cancellationToken: cancellationToken));
        return id is { } walletId ? new WalletId(walletId) : null;
    }

    public async Task<Result<Wallet?>> LockWalletAsync(IDatabaseSession session, WalletId walletId, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<WalletRow>(new CommandDefinition(
            """
            SELECT w.wallet_id, w.merchant_id, w.product_id, w.ledger_balance, w.available_balance, w.reserved_balance,
                   w.status, w.version_no, cd.currency_code, cd.version_no AS currency_version_no, cd.scale AS currency_scale
            FROM ledger.wallets w
            JOIN core.currency_definitions cd ON cd.currency_definition_id = w.currency_definition_id
            WHERE w.wallet_id = @Id
            FOR UPDATE OF w
            """,
            new { Id = walletId.Value }, s.Transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return Result<Wallet?>.Success(null);
        }

        var currency = CurrencyDefinition.Create(row.CurrencyCode, row.CurrencyVersionNo, row.CurrencyScale);
        if (currency.IsFailure || !CanonicalCodes.WalletStatus.TryParse(row.Status, out var status))
        {
            return Corrupt($"wallet {row.WalletId}");
        }

        var ledger = Money.Create(row.LedgerBalance, currency.Value);
        var available = Money.Create(row.AvailableBalance, currency.Value);
        var reserved = Money.Create(row.ReservedBalance, currency.Value);
        if (ledger.IsFailure || available.IsFailure || reserved.IsFailure)
        {
            return Corrupt($"wallet {row.WalletId} balances");
        }

        var wallet = Wallet.Rehydrate(
            new WalletId(row.WalletId), new MerchantId(row.MerchantId), row.ProductId is { } p ? new ProductId(p) : null,
            ledger.Value, available.Value, reserved.Value, status, row.VersionNo);
        return wallet.IsSuccess ? wallet.Value : wallet.Error;
    }

    public async Task<Result<Reservation?>> LockReservationAsync(
        IDatabaseSession session, TransactionId transactionId, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<ReservationRow>(new CommandDefinition(
            """
            SELECT r.reservation_id, r.wallet_id, r.ransys_transaction_id, r.principal_amount, r.fee_amount, r.status,
                   r.hold_reason, r.created_at, r.committed_at, r.released_at,
                   cd.currency_code, cd.version_no AS currency_version_no, cd.scale AS currency_scale
            FROM ledger.balance_reservations r
            JOIN ledger.wallets w ON w.wallet_id = r.wallet_id
            JOIN core.currency_definitions cd ON cd.currency_definition_id = w.currency_definition_id
            WHERE r.ransys_transaction_id = @Id
            FOR UPDATE OF r
            """,
            new { Id = transactionId.Value }, s.Transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return Result<Reservation?>.Success(null);
        }

        var currency = CurrencyDefinition.Create(row.CurrencyCode, row.CurrencyVersionNo, row.CurrencyScale);
        if (currency.IsFailure || !CanonicalCodes.ReservationStatus.TryParse(row.Status, out var status))
        {
            return Corrupt($"reservation {row.ReservationId}");
        }

        var principal = Money.Create(row.PrincipalAmount, currency.Value);
        var fee = Money.Create(row.FeeAmount, currency.Value);
        if (principal.IsFailure || fee.IsFailure)
        {
            return Corrupt($"reservation {row.ReservationId} amounts");
        }

        var reservation = Reservation.Rehydrate(
            row.ReservationId, new WalletId(row.WalletId), new TransactionId(row.RansysTransactionId), principal.Value, fee.Value,
            status, row.HoldReason, DbValues.FromDb(row.CreatedAt), DbValues.FromDb(row.CommittedAt), DbValues.FromDb(row.ReleasedAt));
        return reservation.IsSuccess ? reservation.Value : reservation.Error;
    }

    public async Task<Result<PostedJournalSummary?>> FindJournalAsync(
        IDatabaseSession session, PostingKey postingKey, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var row = await s.Connection.QuerySingleOrDefaultAsync<JournalSummaryRow>(new CommandDefinition(
            """
            SELECT t.ledger_transaction_id, t.operation_type,
                   (SELECT COALESCE(SUM(e.amount), 0) FROM ledger.ledger_entries e
                     WHERE e.ledger_transaction_id = t.ledger_transaction_id AND e.entry_side = 'D') AS total_debit,
                   cd.currency_code, cd.version_no AS currency_version_no, cd.scale AS currency_scale
            FROM ledger.ledger_transactions t
            JOIN LATERAL (SELECT currency_definition_id FROM ledger.ledger_entries
                          WHERE ledger_transaction_id = t.ledger_transaction_id LIMIT 1) first_entry ON true
            JOIN core.currency_definitions cd ON cd.currency_definition_id = first_entry.currency_definition_id
            WHERE t.posting_key = @Key
            """,
            new { Key = postingKey.Value }, s.Transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return Result<PostedJournalSummary?>.Success(null);
        }

        var currency = CurrencyDefinition.Create(row.CurrencyCode, row.CurrencyVersionNo, row.CurrencyScale);
        if (currency.IsFailure || !CanonicalCodes.LedgerOperationType.TryParse(row.OperationType, out var operation))
        {
            return Corrupt($"journal {row.LedgerTransactionId}");
        }

        var total = Money.Create(row.TotalDebit, currency.Value);
        return total.IsSuccess
            ? new PostedJournalSummary(row.LedgerTransactionId, operation, total.Value)
            : Corrupt($"journal {row.LedgerTransactionId} total");
    }

    public async Task<Result<IReadOnlyList<JournalLine>>> GetJournalLinesAsync(
        IDatabaseSession session, Guid journalId, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var rows = await s.Connection.QueryAsync<JournalLineRow>(new CommandDefinition(
            """
            SELECT e.entry_side, e.amount, a.account_code, a.account_name, a.owner_type, a.owner_id, a.wallet_id,
                   a.account_class, a.account_type, a.normal_side,
                   cd.currency_code, cd.version_no AS currency_version_no, cd.scale AS currency_scale
            FROM ledger.ledger_entries e
            JOIN ledger.ledger_accounts a ON a.ledger_account_id = e.ledger_account_id
            JOIN core.currency_definitions cd ON cd.currency_definition_id = e.currency_definition_id
            WHERE e.ledger_transaction_id = @Id
            ORDER BY e.entry_sequence
            """,
            new { Id = journalId }, s.Transaction, cancellationToken: cancellationToken));

        var lines = new List<JournalLine>();
        foreach (var row in rows)
        {
            var currency = CurrencyDefinition.Create(row.CurrencyCode, row.CurrencyVersionNo, row.CurrencyScale);
            if (currency.IsFailure
                || !CanonicalCodes.EntrySide.TryParse(row.EntrySide, out var side)
                || !CanonicalCodes.EntrySide.TryParse(row.NormalSide, out var normalSide)
                || !CanonicalCodes.AccountClass.TryParse(row.AccountClass, out var accountClass))
            {
                return Corrupt($"journal {journalId} entries");
            }

            var amount = Money.Create(row.Amount, currency.Value);
            if (amount.IsFailure)
            {
                return Corrupt($"journal {journalId} entry amount");
            }

            var account = new LedgerAccountSpec(
                row.AccountCode, row.AccountName, row.OwnerType, row.OwnerId,
                row.WalletId is { } w ? new WalletId(w) : null, accountClass, row.AccountType, currency.Value, normalSide);
            lines.Add(new JournalLine(account, side, amount.Value));
        }

        return lines;
    }

    public async Task<bool> IsCompensatedAsync(IDatabaseSession session, Guid journalId, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        return await s.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM ledger.ledger_transactions WHERE compensates_ledger_transaction_id = @Id)",
            new { Id = journalId }, s.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<decimal> SumCreditsAsync(
        IDatabaseSession session, string postingKeyPrefix, string accountCode, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        return await s.Connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            """
            SELECT COALESCE(SUM(e.amount), 0)
            FROM ledger.ledger_transactions t
            JOIN ledger.ledger_entries e ON e.ledger_transaction_id = t.ledger_transaction_id
            JOIN ledger.ledger_accounts a ON a.ledger_account_id = e.ledger_account_id
            WHERE starts_with(t.posting_key, @Prefix) AND a.account_code = @AccountCode AND e.entry_side = 'C'
            """,
            new { Prefix = postingKeyPrefix, AccountCode = accountCode }, s.Transaction, cancellationToken: cancellationToken));
    }

    public async Task InsertReservationAsync(IDatabaseSession session, Reservation reservation, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ledger.balance_reservations
                (reservation_id, wallet_id, ransys_transaction_id, principal_amount, fee_amount, total_reserved_amount,
                 status, hold_reason, created_at, committed_at, released_at)
            VALUES (@Id, @WalletId, @TransactionId, @Principal, @Fee, @Total, @Status, @HoldReason, @CreatedAt, NULL, NULL)
            """,
            new
            {
                reservation.Id,
                WalletId = reservation.WalletId.Value,
                TransactionId = reservation.TransactionId.Value,
                Principal = reservation.Principal.Amount,
                Fee = reservation.Fee.Amount,
                Total = reservation.Total.Amount,
                Status = CanonicalCodes.ReservationStatus.ToCode(reservation.Status),
                reservation.HoldReason,
                CreatedAt = DbValues.ToDb(reservation.CreatedAt),
            },
            s.Transaction, cancellationToken: cancellationToken));
    }

    public async Task UpdateReservationAsync(IDatabaseSession session, Reservation reservation, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var affected = await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ledger.balance_reservations
            SET status = @Status, hold_reason = @HoldReason, committed_at = @CommittedAt, released_at = @ReleasedAt
            WHERE reservation_id = @Id
            """,
            new
            {
                reservation.Id,
                Status = CanonicalCodes.ReservationStatus.ToCode(reservation.Status),
                reservation.HoldReason,
                CommittedAt = DbValues.ToDb(reservation.CommittedAt),
                ReleasedAt = DbValues.ToDb(reservation.ReleasedAt),
            },
            s.Transaction, cancellationToken: cancellationToken));
        EnsureOneRow(affected, $"reservation {reservation.Id}");
    }

    public async Task<Result> InsertJournalAsync(IDatabaseSession session, Journal journal, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var currencyId = await _referenceData.ResolveCurrencyDefinitionIdAsync(s, journal.Currency, cancellationToken);
        if (currencyId.IsFailure)
        {
            return currencyId.Error;
        }

        var accountIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var account in journal.Lines.Select(l => l.Account).DistinctBy(a => a.Code))
        {
            accountIds[account.Code] = await EnsureAccountAsync(s, account, currencyId.Value, journal.CreatedAt, cancellationToken);
        }

        await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ledger.ledger_transactions
                (ledger_transaction_id, posting_key, ransys_transaction_id, reservation_id, approval_request_id,
                 operation_type, business_reference, description, status, effective_at, created_at,
                 created_by_type, created_by_id, compensates_ledger_transaction_id)
            VALUES (@Id, @PostingKey, @TransactionId, @ReservationId, @ApprovalRequestId, @OperationType,
                    @BusinessReference, @Description, 'POSTED', @EffectiveAt, @CreatedAt,
                    @CreatedByType, @CreatedById, @CompensatesId)
            """,
            new
            {
                journal.Id,
                PostingKey = journal.PostingKey.Value,
                TransactionId = journal.TransactionId?.Value,
                journal.ReservationId,
                journal.ApprovalRequestId,
                OperationType = CanonicalCodes.LedgerOperationType.ToCode(journal.OperationType),
                journal.BusinessReference,
                journal.Description,
                EffectiveAt = DbValues.ToDb(journal.EffectiveAt),
                CreatedAt = DbValues.ToDb(journal.CreatedAt),
                CreatedByType = journal.CreatedBy.Type,
                CreatedById = journal.CreatedBy.Id,
                CompensatesId = journal.CompensatesJournalId,
            },
            s.Transaction, cancellationToken: cancellationToken));

        var entries = journal.Lines.Select((line, index) => new
        {
            Id = Guid.CreateVersion7(),
            JournalId = journal.Id,
            AccountId = accountIds[line.Account.Code],
            Sequence = (short)(index + 1),
            Side = CanonicalCodes.EntrySide.ToCode(line.Side),
            line.Amount.Amount,
            CurrencyId = currencyId.Value,
            CreatedAt = DbValues.ToDb(journal.CreatedAt),
        });
        await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ledger.ledger_entries
                (ledger_entry_id, ledger_transaction_id, ledger_account_id, entry_sequence, entry_side, amount,
                 currency_definition_id, created_at)
            VALUES (@Id, @JournalId, @AccountId, @Sequence, @Side, @Amount, @CurrencyId, @CreatedAt)
            """,
            entries, s.Transaction, cancellationToken: cancellationToken));

        return Result.Success();
    }

    public async Task UpdateWalletAsync(IDatabaseSession session, Wallet wallet, CancellationToken cancellationToken)
    {
        var s = Pg(session);
        var affected = await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ledger.wallets
            SET ledger_balance = @Ledger, available_balance = @Available, reserved_balance = @Reserved,
                version_no = version_no + 1, updated_at = now()
            WHERE wallet_id = @Id AND version_no = @VersionNo
            """,
            new
            {
                Id = wallet.Id.Value,
                Ledger = wallet.LedgerBalance.Amount,
                Available = wallet.AvailableBalance.Amount,
                Reserved = wallet.ReservedBalance.Amount,
                wallet.VersionNo,
            },
            s.Transaction, cancellationToken: cancellationToken));

        // The row is locked FOR UPDATE by this session, so a mismatch means the lock order was violated.
        EnsureOneRow(affected, $"wallet {wallet.Id}");
    }

    private static async Task<Guid> EnsureAccountAsync(
        PostgresSession s, LedgerAccountSpec account, Guid currencyId, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await s.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ledger.ledger_accounts
                (ledger_account_id, account_code, account_name, owner_type, owner_id, wallet_id, account_class,
                 account_type, currency_definition_id, normal_side, status, created_at)
            VALUES (@Id, @Code, @Name, @OwnerType, @OwnerId, @WalletId, @AccountClass, @AccountType, @CurrencyId,
                    @NormalSide, 'ACTIVE', @CreatedAt)
            ON CONFLICT (account_code) DO NOTHING
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                account.Code,
                account.Name,
                account.OwnerType,
                account.OwnerId,
                WalletId = account.WalletId?.Value,
                AccountClass = CanonicalCodes.AccountClass.ToCode(account.AccountClass),
                account.AccountType,
                CurrencyId = currencyId,
                NormalSide = CanonicalCodes.EntrySide.ToCode(account.NormalSide),
                CreatedAt = DbValues.ToDb(createdAt),
            },
            s.Transaction, cancellationToken: cancellationToken));

        return await s.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT ledger_account_id FROM ledger.ledger_accounts WHERE account_code = @Code",
            new { account.Code }, s.Transaction, cancellationToken: cancellationToken));
    }

    private static void EnsureOneRow(int affected, string target)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException($"Expected to update exactly one row for {target}, updated {affected}.");
        }
    }

    private static PostgresSession Pg(IDatabaseSession session) =>
        session as PostgresSession
        ?? throw new ArgumentException($"Expected a {nameof(PostgresSession)}.", nameof(session));

    private static RansysError Corrupt(string what) =>
        new(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal, $"Persisted {what} is inconsistent.");

    private sealed class WalletRow
    {
        public Guid WalletId { get; init; }

        public Guid MerchantId { get; init; }

        public Guid? ProductId { get; init; }

        public decimal LedgerBalance { get; init; }

        public decimal AvailableBalance { get; init; }

        public decimal ReservedBalance { get; init; }

        public string Status { get; init; } = "";

        public long VersionNo { get; init; }

        public string CurrencyCode { get; init; } = "";

        public int CurrencyVersionNo { get; init; }

        public short CurrencyScale { get; init; }
    }

    private sealed class ReservationRow
    {
        public Guid ReservationId { get; init; }

        public Guid WalletId { get; init; }

        public Guid RansysTransactionId { get; init; }

        public decimal PrincipalAmount { get; init; }

        public decimal FeeAmount { get; init; }

        public string Status { get; init; } = "";

        public string HoldReason { get; init; } = "";

        public DateTime CreatedAt { get; init; }

        public DateTime? CommittedAt { get; init; }

        public DateTime? ReleasedAt { get; init; }

        public string CurrencyCode { get; init; } = "";

        public int CurrencyVersionNo { get; init; }

        public short CurrencyScale { get; init; }
    }

    private sealed class JournalSummaryRow
    {
        public Guid LedgerTransactionId { get; init; }

        public string OperationType { get; init; } = "";

        public decimal TotalDebit { get; init; }

        public string CurrencyCode { get; init; } = "";

        public int CurrencyVersionNo { get; init; }

        public short CurrencyScale { get; init; }
    }

    private sealed class JournalLineRow
    {
        public string EntrySide { get; init; } = "";

        public decimal Amount { get; init; }

        public string AccountCode { get; init; } = "";

        public string AccountName { get; init; } = "";

        public string OwnerType { get; init; } = "";

        public Guid? OwnerId { get; init; }

        public Guid? WalletId { get; init; }

        public string AccountClass { get; init; } = "";

        public string AccountType { get; init; } = "";

        public string NormalSide { get; init; } = "";

        public string CurrencyCode { get; init; } = "";

        public int CurrencyVersionNo { get; init; }

        public short CurrencyScale { get; init; }
    }
}
