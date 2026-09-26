using Ransys.Api.Contracts.V1;
using Ransys.Domain;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Processing;

namespace Ransys.Api.Mapping;

/// <summary>Application results → public response DTOs (handoff §9, §14–15). Only public fields cross this boundary.</summary>
public static class ResponseMapper
{
    public static TransactionResponse ToResponse(TransactionProcessingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new TransactionResponse
        {
            RansysTransactionId = result.TransactionId.Value.ToString("D"),
            ClientReference = result.ClientReference,
            ResponseCode = result.ResponseCode,
            ResponseMessage = result.ResponseMessage,
            TransactionStatus = TransactionStatus(result.ProcessingStatus),
            References = References(result.References),
            Data = result.Data,
            Timestamp = result.Timestamp,
        };
    }

    public static TransactionDetailResponse ToResponse(TransactionDetailView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new TransactionDetailResponse
        {
            RansysTransactionId = view.TransactionId.Value.ToString("D"),
            OriginalTransactionId = view.OriginalTransactionId?.Value.ToString("D"),
            ClientReference = view.ClientReference,
            TransactionType = CanonicalCodes.TransactionType.ToCode(view.TransactionType),
            ProcessingStatus = TransactionStatus(view.ProcessingStatus),
            FinancialStatus = CanonicalCodes.FinancialStatus.ToCode(view.FinancialStatus),
            ReconciliationStatus = CanonicalCodes.ReconciliationStatus.ToCode(view.ReconciliationStatus),
            SettlementStatus = CanonicalCodes.SettlementStatus.ToCode(view.SettlementStatus),
            ResponseCode = view.ResponseCode,
            ResponseMessage = view.ResponseMessage,
            References = References(view.References),
            ReceivedAt = view.ReceivedAt,
            CompletedAt = view.CompletedAt,
        };
    }

    /// <summary>
    /// OpenAPI <c>TransactionStatus</c>. REVERSAL_PENDING / REFUND_PENDING are not in the public enum and are never set by
    /// the current model (ADR-012, ADR-023); a legacy row in either state is reported as PENDING (not final).
    /// </summary>
    public static string TransactionStatus(ProcessingStatus status) => status switch
    {
        ProcessingStatus.ReversalPending or ProcessingStatus.RefundPending => CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus.Pending),
        _ => CanonicalCodes.ProcessingStatus.ToCode(status),
    };

    /// <summary><c>references</c> only when at least one public reference is present.</summary>
    private static PublicReferencesDto? References(PublicReferences references) =>
        references.MerchantReference is null && references.Stan is null && references.Rrn is null
            ? null
            : new PublicReferencesDto { MerchantReference = references.MerchantReference, Stan = references.Stan, Rrn = references.Rrn };
}
