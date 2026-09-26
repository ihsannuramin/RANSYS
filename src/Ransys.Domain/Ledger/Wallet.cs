using Ransys.Domain.Common;
using Ransys.Domain.Monetary;

namespace Ransys.Domain.Ledger;

/// <summary>
/// Wallet projection used for realtime decisions (Ledger Posting Rule Matrix §2, §8). Prefund only:
/// <c>available &gt;= 0</c>, <c>reserved &gt;= 0</c> and <c>ledger = available + reserved</c> always.
/// Balances never change without a journal; the Ledger Posting Service applies the journal and the
/// projection change in the same database transaction.
/// </summary>
public sealed class Wallet
{
    private Wallet(
        WalletId id,
        MerchantId merchantId,
        ProductId? productId,
        Money ledgerBalance,
        Money availableBalance,
        Money reservedBalance,
        WalletStatus status,
        long versionNo)
    {
        Id = id;
        MerchantId = merchantId;
        ProductId = productId;
        LedgerBalance = ledgerBalance;
        AvailableBalance = availableBalance;
        ReservedBalance = reservedBalance;
        Status = status;
        VersionNo = versionNo;
    }

    public WalletId Id { get; }

    public MerchantId MerchantId { get; }

    /// <summary>Null for the merchant main wallet (baseline); set for a future product-specific wallet.</summary>
    public ProductId? ProductId { get; }

    public CurrencyDefinition Currency => LedgerBalance.Currency;

    public Money LedgerBalance { get; private set; }

    public Money AvailableBalance { get; private set; }

    public Money ReservedBalance { get; private set; }

    public WalletStatus Status { get; }

    /// <summary>Row version (<c>version_no</c>) of the loaded projection.</summary>
    public long VersionNo { get; }

    public static Result<Wallet> Rehydrate(
        WalletId id,
        MerchantId merchantId,
        ProductId? productId,
        Money ledgerBalance,
        Money availableBalance,
        Money reservedBalance,
        WalletStatus status,
        long versionNo)
    {
        ArgumentNullException.ThrowIfNull(ledgerBalance);
        ArgumentNullException.ThrowIfNull(availableBalance);
        ArgumentNullException.ThrowIfNull(reservedBalance);

        var consistent = availableBalance.HasSameCurrency(ledgerBalance)
            && reservedBalance.HasSameCurrency(ledgerBalance)
            && availableBalance.Add(reservedBalance) is { IsSuccess: true } sum
            && sum.Value == ledgerBalance;
        if (!consistent || versionNo <= 0)
        {
            return new RansysError(
                ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal,
                $"Wallet {id} projection is inconsistent: ledger must equal available + reserved.");
        }

        return new Wallet(id, merchantId, productId, ledgerBalance, availableBalance, reservedBalance, status, versionNo);
    }

    /// <summary>OP-02: available → reserved. Fails with INSUFFICIENT_BALANCE rather than going negative.</summary>
    public Result Reserve(Money total)
    {
        var check = EnsureCanDebit(total);
        if (check.IsFailure)
        {
            return check;
        }

        AvailableBalance = AvailableBalance.Subtract(total).Value;
        ReservedBalance = ReservedBalance.Add(total).Value;
        return Result.Success();
    }

    /// <summary>OP-03: reserved funds are consumed; ledger balance decreases.</summary>
    public Result CommitReserved(Money total) => MoveOutOfReserved(total, toAvailable: false);

    /// <summary>OP-04 / OP-07 / OP-08: reserved funds return to available.</summary>
    public Result ReleaseReserved(Money total) => MoveOutOfReserved(total, toAvailable: true);

    /// <summary>Top-up, refund, reversal after post, credit adjustment: available and ledger increase.</summary>
    public Result CreditAvailable(Money amount)
    {
        EnsureCurrency(amount);
        if (Status == WalletStatus.Closed)
        {
            return NotActive("credit");
        }

        var available = AvailableBalance.Add(amount);
        var ledger = LedgerBalance.Add(amount);
        if (available.IsFailure || ledger.IsFailure)
        {
            return available.IsFailure ? available.Error : ledger.Error;
        }

        AvailableBalance = available.Value;
        LedgerBalance = ledger.Value;
        return Result.Success();
    }

    /// <summary>Debit adjustment: available and ledger decrease; never below zero (OP-14).</summary>
    public Result DebitAvailable(Money amount)
    {
        var check = EnsureCanDebit(amount);
        if (check.IsFailure)
        {
            return check;
        }

        AvailableBalance = AvailableBalance.Subtract(amount).Value;
        LedgerBalance = LedgerBalance.Subtract(amount).Value;
        return Result.Success();
    }

    // In-flight reservations must always be finalizable, so commit/release are allowed for any wallet status.
    private Result MoveOutOfReserved(Money total, bool toAvailable)
    {
        EnsureCurrency(total);
        var reserved = ReservedBalance.Subtract(total);
        if (reserved.IsFailure)
        {
            return new RansysError(
                ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal,
                $"Wallet {Id} reserved balance {ReservedBalance} is smaller than the reservation {total}.");
        }

        ReservedBalance = reserved.Value;
        if (toAvailable)
        {
            AvailableBalance = AvailableBalance.Add(total).Value;
        }
        else
        {
            LedgerBalance = LedgerBalance.Subtract(total).Value;
        }

        return Result.Success();
    }

    /// <summary>
    /// New debits and reservations need an ACTIVE wallet and enough available balance.
    /// TODO / Architecture Decision Required: FROZEN/CLOSED semantics beyond "no new debits" are not specified.
    /// </summary>
    private Result EnsureCanDebit(Money amount)
    {
        EnsureCurrency(amount);
        if (Status != WalletStatus.Active)
        {
            return NotActive("debit");
        }

        return AvailableBalance.IsGreaterThanOrEqualTo(amount)
            ? Result.Success()
            : RansysError.Financial(
                ErrorCodes.InsufficientBalance,
                $"Wallet {Id} available balance {AvailableBalance.ToCanonicalAmountString()} is less than {amount.ToCanonicalAmountString()}.");
    }

    private RansysError NotActive(string operation) =>
        RansysError.Financial(ErrorCodes.WalletNotActive, $"Wallet {Id} is {Status}; {operation} is not allowed.");

    private void EnsureCurrency(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        if (amount.Currency != Currency)
        {
            throw new CurrencyMismatchException(Currency, amount.Currency);
        }
    }
}
