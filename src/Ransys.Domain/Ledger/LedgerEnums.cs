namespace Ransys.Domain.Ledger;

/// <summary>Ledger operation types (Ledger Posting Rule Matrix §32, ADR-008).</summary>
public enum LedgerOperationType
{
    TopUp,
    Reserve,
    Post,
    Release,
    Reversal,
    Refund,
    AdjustmentCredit,
    AdjustmentDebit,
    SettlementClear,
    RefundClear,
}

public enum EntrySide
{
    Debit,
    Credit,
}

/// <summary>DDL v1.1 <c>ck_ledger_account_class</c>.</summary>
public enum AccountClass
{
    Asset,
    Liability,
    Revenue,
    Expense,
    Control,
}

/// <summary>DDL v1.1 <c>ck_wallet_status</c>.</summary>
public enum WalletStatus
{
    Active,
    Frozen,
    Closed,
}

/// <summary>Ledger Posting Rule Matrix §33: ACTIVE → COMMITTED | RELEASED, never back.</summary>
public enum ReservationStatus
{
    Active,
    Committed,
    Released,
}
