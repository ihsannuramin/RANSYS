namespace Ransys.Domain.Transactions;

// Canonical enums (Canonical Contracts v1.0, State Transition Matrix §3).
// Persisted and wire values come from CanonicalCodes, never from member names.

public enum TransactionType
{
    Inquiry,
    Payment,
    Purchase,
    Transfer,
    Refund,
    Reversal,
    Void,
    Advice,
    BalanceInquiry,
    StatusCheck,
    Settlement,
    TopUp,
    Adjustment,
}

/// <summary>Online processing truth (State Transition Matrix §3.1).</summary>
public enum ProcessingStatus
{
    Received,
    Validated,
    Processing,
    Pending,
    InDoubt,
    Success,
    Failed,
    ReversalPending,
    Reversed,
    RefundPending,
    PartiallyRefunded,
    Refunded,
}

/// <summary>Financial truth (State Transition Matrix §3.2).</summary>
public enum FinancialStatus
{
    None,
    Reserved,
    Posted,
    Released,
    ReversalPending,
    Reversed,
    RefundPending,
    PartiallyRefunded,
    Refunded,
    Adjusted,
}

/// <summary>Reconciliation truth (State Transition Matrix §3.3).</summary>
public enum ReconciliationStatus
{
    Unmatched,
    Pending,
    Matched,
    Exception,
    Resolved,
}

/// <summary>Settlement truth (State Transition Matrix §3.4).</summary>
public enum SettlementStatus
{
    NotApplicable,
    Pending,
    Included,
    Approved,
    ReadyToPay,
    Settled,
    Adjusted,
}

/// <summary>Generic source/destination kind (Canonical Data Model §20).</summary>
public enum EndpointType
{
    Merchant,
    Customer,
    BankAccount,
    Wallet,
    Biller,
    Provider,
    VirtualAccount,
    MobileNumber,
    Custom,
}
