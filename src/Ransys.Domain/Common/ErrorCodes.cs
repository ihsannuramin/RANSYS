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

    public const string AttemptInconsistentTransport = "ATTEMPT_INCONSISTENT_TRANSPORT";
    public const string AttemptOutcomeAlreadyRecorded = "ATTEMPT_OUTCOME_ALREADY_RECORDED";

    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";
    public const string PersistedStateInvalid = "PERSISTED_STATE_INVALID";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string ReferenceDataNotFound = "REFERENCE_DATA_NOT_FOUND";
    public const string OriginalTransactionRequired = "ORIGINAL_TRANSACTION_REQUIRED";
    public const string ReferenceMismatch = "REFERENCE_MISMATCH";
    public const string ReservationNotApplicable = "RESERVATION_NOT_APPLICABLE";
    public const string ReserveAmountNotPositive = "RESERVE_AMOUNT_NOT_POSITIVE";
    public const string FailoverNotAllowed = "FAILOVER_NOT_ALLOWED";
    public const string ReasonCodeMismatch = "REASON_CODE_MISMATCH";
}
