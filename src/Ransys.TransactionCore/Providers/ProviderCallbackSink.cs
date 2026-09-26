using System.Data.Common;
using Ransys.Adapter.Contracts.V1;
using Ransys.Application;
using Ransys.Domain;
using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
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
    ITransactionRepository transactions,
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
            var loaded = await transactions.GetAsync(session, id, forUpdate: false, cancellationToken);
            if (loaded.IsFailure || loaded.Value is not { } transaction)
            {
                return new ProviderCallbackAck(false, loaded.IsFailure ? Rejected : UnknownTransaction);
            }

            // A provider may only report on requests routed to it.
            if (transaction.Routing?.CurrentProvider.ProviderId != new ProviderId(callback.ProviderId))
            {
                return new ProviderCallbackAck(false, ProviderMismatch);
            }

            var applied = await finalization.ApplyAsync(
                session,
                new ProviderResultCommand(
                    id,
                    interpreted.Resolution,
                    ChangeSource.Callback,
                    "CALLBACK_" + interpreted.ReasonCode,
                    interpreted.ResponseCode,
                    ReasonDescription: callback.CallbackId is { Length: > 0 and <= 400 } callbackId ? $"Provider callback {callbackId}" : null),
                cancellationToken);

            if (applied.IsFailure)
            {
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
