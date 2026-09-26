using Ransys.Api.Contracts.V1;
using Ransys.Domain.Common;
using Ransys.TransactionCore.Children;
using Ransys.TransactionCore.Fees;
using Ransys.TransactionCore.Processing;

namespace Ransys.Api.Errors;

/// <summary>An HTTP error answer: status plus the <see cref="ApiErrorResponse"/> fields (ADR-021).</summary>
public sealed record ApiError(int StatusCode, string ErrorCode, string ErrorMessage, string? Field = null)
{
    public static ApiError Validation(string message, string? field) =>
        new(StatusCodes.Status400BadRequest, RansysResponseCodes.InvalidRequest, message, field);

    public static ApiError Unauthorized() =>
        new(StatusCodes.Status401Unauthorized, RansysResponseCodes.InvalidSignature, "Request authentication failed.");

    public static ApiError NotFound() =>
        new(StatusCodes.Status404NotFound, RansysResponseCodes.InvalidRequest, "Transaction not found.", "ransysTransactionId");

    public static ApiError TooManyRequests() =>
        new(StatusCodes.Status429TooManyRequests, RansysResponseCodes.InternalError, "Too many requests; retry later.");

    public static ApiError DependencyUnavailable() =>
        new(StatusCodes.Status503ServiceUnavailable, RansysResponseCodes.InternalError,
            "Service temporarily unavailable; no provider request was made. Retry with the same clientReference.");

    public IResult ToResult(HttpContext context) => Results.Json(ToBody(context), ApiJson.Options, "application/json", StatusCode);

    public ApiErrorResponse ToBody(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ApiErrorResponse
        {
            ErrorCode = ErrorCode,
            ErrorMessage = ErrorMessage,
            Field = Field,
            CorrelationId = context.TraceIdentifier,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    public Task WriteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = StatusCode;
        return context.Response.WriteAsJsonAsync(ToBody(context), ApiJson.Options, "application/json", context.RequestAborted);
    }
}

/// <summary>
/// ADR-021: maps a Transaction Core <see cref="RansysError"/> (returned before a transaction was accepted into normal
/// processing) to HTTP. Only the statuses declared by OpenAPI v1 (400/401/403/409/429/503; 404 on GET) and only the
/// approved codes 1001, 2001, 2003, 3001, 4001, 5001 are used. Business outcomes of an accepted transaction are HTTP 200
/// with <c>transactionStatus</c> + <c>responseCode</c> and never reach this mapper.
/// </summary>
public static class ApiErrorMapper
{
    /// <summary>Rejections because of the original transaction's state (children); reported on <c>originalTransactionId</c>.</summary>
    private static readonly HashSet<string> OriginalStateRejections = new(StringComparer.Ordinal)
    {
        ErrorCodes.RefundNotAllowed,
        ErrorCodes.ReversalNotAllowed,
        ErrorCodes.ReversalAlreadyActive,
        ErrorCodes.VoidNotAllowed,
        ChildTransactionService.VoidAlreadyActive,
        ErrorCodes.InvalidStateTransition,
        ErrorCodes.OriginalTransactionRequired,
        ErrorCodes.OriginalTransactionSelfReference,
    };

    public static ApiError Map(RansysError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error.Code)
        {
            case ErrorCodes.DuplicateReferenceConflict:
                return new ApiError(StatusCodes.Status409Conflict, RansysResponseCodes.DuplicateReferenceConflict,
                    "clientReference was already used with a different request.", "clientReference");

            case ProcessingErrorCodes.OriginalTransactionInvalid:
                return ApiError.Validation("originalTransactionId does not identify a transaction of this client.", "originalTransactionId");

            case ErrorCodes.RefundExceedsPosted:
                return ApiError.Validation("refundAmount exceeds the remaining refundable amount.", "refundAmount.value");

            case ErrorCodes.WalletNotFound:
                // The merchant has no usable main wallet in this currency: the request cannot be accepted.
                return ApiError.Validation("No usable merchant wallet for this currency.", "amount.currency");

            case ErrorCodes.InsufficientBalance:
                return new ApiError(StatusCodes.Status400BadRequest, RansysResponseCodes.InsufficientBalance, "Insufficient balance.");

            case ErrorCodes.NoRouteAvailable or ErrorCodes.ReversalNotSupported:
                // A child whose original provider lacks the capability (children never fail over).
                return new ApiError(StatusCodes.Status400BadRequest, RansysResponseCodes.NoRouteAvailable,
                    "The original transaction's provider does not support this operation.", "originalTransactionId");

            case ErrorCodes.ConfigurationNotAvailable or FeeResolver.AmbiguousFeeRule:
                // Mandatory configuration is missing or ambiguous: fail closed, nothing was created or sent.
                return new ApiError(StatusCodes.Status503ServiceUnavailable, RansysResponseCodes.NoRouteAvailable,
                    "Configuration is not available; the request was not processed.");
        }

        if (OriginalStateRejections.Contains(error.Code))
        {
            return ApiError.Validation("The original transaction does not allow this operation in its current state.", "originalTransactionId");
        }

        return error.Category switch
        {
            ErrorCategory.Validation => ApiError.Validation(error.Message, error.Field),
            ErrorCategory.Authentication => ApiError.Unauthorized(),
            ErrorCategory.Authorization => new ApiError(StatusCodes.Status403Forbidden, RansysResponseCodes.InvalidSignature, "Forbidden."),
            ErrorCategory.Financial => ApiError.Validation("The request cannot be accepted.", error.Field),

            // Internal, Infrastructure, Provider, Routing, Conflict (e.g. CONCURRENCY_CONFLICT, PERSISTED_STATE_INVALID):
            // nothing was committed or sent; a retry with the same clientReference is safe.
            _ => ApiError.DependencyUnavailable(),
        };
    }
}
