namespace Ransys.Domain.Common;

/// <summary>
/// Internal domain error codes. These are reason codes, not the 4-digit canonical response codes;
/// the response code catalog is pending (main.md §20).
/// </summary>
public static class ErrorCodes
{
    public const string Required = "FIELD_REQUIRED";
    public const string TooLong = "FIELD_TOO_LONG";
    public const string InvalidFormat = "FIELD_INVALID_FORMAT";
    public const string OutOfRange = "FIELD_OUT_OF_RANGE";

    public const string CurrencyCodeInvalid = "CURRENCY_CODE_INVALID";
    public const string CurrencyScaleInvalid = "CURRENCY_SCALE_INVALID";
    public const string CurrencyVersionInvalid = "CURRENCY_VERSION_INVALID";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";

    public const string MoneyNegative = "MONEY_NEGATIVE";
    public const string MoneyPrecisionExceedsScale = "MONEY_PRECISION_EXCEEDS_SCALE";
    public const string MoneyOutOfRange = "MONEY_OUT_OF_RANGE";

    public const string FingerprintInvalid = "FINGERPRINT_INVALID";
    public const string OriginalTransactionSelfReference = "ORIGINAL_TRANSACTION_SELF_REFERENCE";

    public const string MetadataKeyInvalid = "METADATA_KEY_INVALID";
    public const string MetadataSensitiveKey = "METADATA_SENSITIVE_KEY";

    public const string FeeCurrencyMismatch = "FEE_CURRENCY_MISMATCH";

    public const string RoutingInvalidDecision = "ROUTING_INVALID_DECISION";
    public const string RoutingFailoverToSameProvider = "ROUTING_FAILOVER_TO_SAME_PROVIDER";

    /// <summary>Architecture Spec §21 canonical response 5001.</summary>
    public const string NoRouteAvailable = "NO_ROUTE_AVAILABLE";

    /// <summary>No active configuration version for a domain (fail closed).</summary>
    public const string ConfigurationNotAvailable = "CONFIGURATION_NOT_AVAILABLE";

    public const string AttemptInconsistentTransport = "ATTEMPT_INCONSISTENT_TRANSPORT";
    public const string AttemptOutcomeAlreadyRecorded = "ATTEMPT_OUTCOME_ALREADY_RECORDED";
    public const string AttemptNotAllowed = "ATTEMPT_NOT_ALLOWED";
    public const string AttemptNotFound = "ATTEMPT_NOT_FOUND";

    /// <summary>A provider result command named a provider that is not the transaction's current routed provider.</summary>
    public const string ProviderMismatch = "PROVIDER_MISMATCH";

    /// <summary>Finalization was asked to apply a result to a transaction id that does not exist.</summary>
    public const string TransactionNotFound = "TRANSACTION_NOT_FOUND";

    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";
    public const string InsufficientBalance = "INSUFFICIENT_BALANCE";
    public const string WalletNotFound = "WALLET_NOT_FOUND";
    public const string WalletNotActive = "WALLET_NOT_ACTIVE";
    public const string WalletClosed = "WALLET_CLOSED";
    public const string WalletNotEmpty = "WALLET_NOT_EMPTY";
    public const string WalletHasUnresolvedTransactions = "WALLET_HAS_UNRESOLVED_TRANSACTIONS";
    public const string ReservationNotFound = "RESERVATION_NOT_FOUND";
    public const string ReservationNotActive = "RESERVATION_NOT_ACTIVE";
    public const string ReservationAlreadyExists = "RESERVATION_ALREADY_EXISTS";
    public const string JournalInvalid = "JOURNAL_INVALID";
    public const string JournalUnbalanced = "JOURNAL_UNBALANCED";
    public const string PostingAlreadyExists = "POSTING_ALREADY_EXISTS";
    public const string PostingKeyConflict = "POSTING_KEY_CONFLICT";
    public const string PostingAmountMismatch = "POSTING_AMOUNT_MISMATCH";
    public const string OriginalPostingNotFound = "ORIGINAL_POSTING_NOT_FOUND";
    public const string AlreadyReversed = "ALREADY_REVERSED";
    public const string RefundExceedsPosted = "REFUND_EXCEEDS_POSTED";
    public const string ApprovalRequired = "APPROVAL_REQUIRED";

    /// <summary>Same client reference with a different fingerprint (PRD §11.3; canonical response 2003).</summary>
    public const string DuplicateReferenceConflict = "DUPLICATE_REFERENCE_CONFLICT";

    public const string PersistedStateInvalid = "PERSISTED_STATE_INVALID";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string ReferenceDataNotFound = "REFERENCE_DATA_NOT_FOUND";
    public const string OriginalTransactionRequired = "ORIGINAL_TRANSACTION_REQUIRED";
    public const string ReferenceMismatch = "REFERENCE_MISMATCH";
    public const string ReservationNotApplicable = "RESERVATION_NOT_APPLICABLE";
    public const string ReserveAmountNotPositive = "RESERVE_AMOUNT_NOT_POSITIVE";
    public const string FailoverNotAllowed = "FAILOVER_NOT_ALLOWED";
    public const string ReasonCodeMismatch = "REASON_CODE_MISMATCH";
    public const string ReversalNotAllowed = "REVERSAL_NOT_ALLOWED";
    public const string ReversalAlreadyActive = "REVERSAL_ALREADY_ACTIVE";
    public const string ReversalNotSupported = "REVERSAL_NOT_SUPPORTED";

    /// <summary>ADR-023: the original is not in a refundable state.</summary>
    public const string RefundNotAllowed = "REFUND_NOT_ALLOWED";

    /// <summary>ADR-019: the original is not in a voidable state.</summary>
    public const string VoidNotAllowed = "VOID_NOT_ALLOWED";
}
