using System.Data.Common;
using Ransys.Adapter.Contracts.V1;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Finalization;

namespace Ransys.TransactionCore.Providers;

/// <summary>
/// Core-side sink for normalized provider callbacks (Provider Adapter Contract v1 §15–§16). Callbacks are at least
/// once, so every path is idempotent and goes through <see cref="TransactionFinalizationService.ApplyAsync(IDatabaseSession, ProviderResultCommand, CancellationToken)"/>
/// with <see cref="ChangeSource.Callback"/>; no other code path touches the ledger.
/// <para>
/// Correlation: <see cref="ProviderCallback.OriginalRansysTransactionId"/> identifies the transaction <em>the provider
/// acted on</em>, i.e. the transaction whose request carried that <c>ransysTransactionId</c>. For a reversal, refund or
/// void this is the <b>child</b> id, not the original payment's id (the child's own request is what the provider
/// answers; the effect on the original follows from finalizing the child, ADR-012/023/019).
/// </para>
/// <para>
/// Lock order (ADR-027, RR1): the sink itself never locks any row. It only interprets the callback and hands
/// <see cref="ProviderResultCommand.ExpectedProvider"/> / <see cref="ProviderResultCommand.Evidence"/> to
/// <see cref="TransactionFinalizationService.ApplyAsync(IDatabaseSession, ProviderResultCommand, CancellationToken)"/>,
/// which is the only place that locks the transaction (parent before child, ADR-012/023/019) and correlates the
/// provider to its attempt under that lock. A callback and the sync completion path for the same child therefore
/// always take locks in the same order and cannot deadlock against each other.
/// </para>
/// Acknowledgements:
/// <list type="bullet">
/// <item>applied, duplicate (no change) or contradicting (reconciliation EXCEPTION recorded) ⇒ <c>Accepted = true</c>;</item>
/// <item>a stale PENDING / IN_DOUBT report for a transaction that already has a final result ⇒ <c>Accepted = true</c>,
/// nothing changes;</item>
/// <item>unknown transaction, a provider other than the transaction's routed provider, a NOT_SENT callback (a callback
/// proves the provider saw the request, so NOT_SENT is not a valid callback), or a temporary failure ⇒
/// <c>Accepted = false</c> (the adapter may redeliver).</item>
/// </list>
/// </summary>
public sealed class ProviderCallbackSink(
    IDatabaseSessionFactory sessions,
    TransactionFinalizationService finalization,
    IClock clock) : IProviderCallbackSink
{
    public const string Applied = "APPLIED";
    public const string Duplicate = "DUPLICATE";
    public const string ConflictRecorded = "CONFLICT_RECORDED";
    public const string StaleIgnored = "STALE_IGNORED";
    public const string UnknownTransaction = "UNKNOWN_TRANSACTION";
    public const string ProviderMismatch = "PROVIDER_MISMATCH";
    public const string NotSentInvalid = "NOT_SENT_IS_NOT_A_CALLBACK";
    public const string Rejected = "REJECTED";
    public const string TemporarilyUnavailable = "TEMPORARILY_UNAVAILABLE";

    public async Task<ProviderCallbackAck> SubmitAsync(ProviderCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (callback.OriginalRansysTransactionId == Guid.Empty || callback.ProviderId == Guid.Empty)
        {
            return new ProviderCallbackAck(false, UnknownTransaction);
        }

        var interpreted = ProviderResultInterpreter.Interpret(callback.Result, clock.UtcNow);
        if (interpreted.Resolution == AttemptResolutionKind.NotSent)
        {
            return new ProviderCallbackAck(false, NotSentInvalid);
        }

        try
        {
            await using var session = await sessions.BeginAsync(cancellationToken);
            var id = new TransactionId(callback.OriginalRansysTransactionId);

            var applied = await finalization.ApplyAsync(
                session,
                new ProviderResultCommand(
                    id,
                    interpreted.Resolution,
                    ChangeSource.Callback,
                    "CALLBACK_" + interpreted.ReasonCode,
                    interpreted.ResponseCode,
                    AttemptId: null,
                    ReasonDescription: callback.CallbackId is { Length: > 0 and <= 400 } callbackId ? $"Provider callback {callbackId}" : null,
                    ExpectedProvider: new ProviderId(callback.ProviderId),
                    Evidence: interpreted.Outcome),
                cancellationToken);

            if (applied.IsFailure)
            {
                if (applied.Error.Code == ErrorCodes.TransactionNotFound)
                {
                    return new ProviderCallbackAck(false, UnknownTransaction);
                }

                if (applied.Error.Code == ErrorCodes.ProviderMismatch)
                {
                    return new ProviderCallbackAck(false, ProviderMismatch);
                }

                // PENDING / IN_DOUBT after a final result is an out-of-order report: acknowledge, change nothing.
                return applied.Error.Code == ErrorCodes.InvalidStateTransition
                       && interpreted.Resolution is AttemptResolutionKind.Pending or AttemptResolutionKind.InDoubt
                    ? new ProviderCallbackAck(true, StaleIgnored)
                    : new ProviderCallbackAck(false, Rejected);
            }

            await session.CommitAsync(cancellationToken);
            return new ProviderCallbackAck(true, applied.Value.Kind switch
            {
                TransitionKind.NoChange => Duplicate,
                TransitionKind.ConflictRecorded => ConflictRecorded,
                _ => Applied,
            });
        }
        catch (DbException)
        {
            // Nothing was committed; the adapter redelivers (at least once).
            return new ProviderCallbackAck(false, TemporarilyUnavailable);
        }
    }
}
