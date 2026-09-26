using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Transactions;

/// <summary>Drives a transaction into a named state through the public transition methods only.</summary>
internal static class TransactionBuilder
{
    public static TransitionContext Ctx(string reason = "TEST", ChangeSource source = ChangeSource.Core) =>
        TransitionContext.Create(reason, source, T0).Value;

    public static RoutingDecision InitialRouting { get; } = RoutingDecision.Initial(ProviderRefA, 1, T0).Value;

    public static FeeComponents Fee2500 { get; } = FeeComponents.Create(
        [FeeComponent.Create(FeeComponentType.MerchantServiceFee, Rp(2_500m), Rp(2_500m), FeeBeneficiary.Create("RANSYS").Value, false, 1).Value],
        Idr).Value;

    public static Transaction NewPayment(decimal amount = 100_000m) => New(TransactionType.Payment, Rp(amount));

    public static Transaction NewInquiry() => New(TransactionType.Inquiry, amount: null);

    public static Transaction New(TransactionType type, global::Ransys.Domain.Monetary.Money? amount, TransactionId? original = null)
    {
        var id = NewTransactionId();
        var fingerprint = TransactionFingerprint.Compute(FingerprintInput());
        var identity = TransactionIdentity.Create(id, "INV-001", null, fingerprint, original).Value;
        var draft = new TransactionDraft(
            identity, type, Merchant, Channel, Product, amount, null, null, null,
            TransactionReferences.Create("INV-001").Value, ExtensionMetadata.Empty, T0);

        var transaction = Transaction.Create(draft).Value;
        transaction.ClearPendingStateChanges();
        return transaction;
    }

    public static Transaction Validated(Transaction t)
    {
        Ok(t.Validate(t.Amount is null ? null : Fee2500, TransactionConfigurationSnapshot.None, Ctx("VALIDATION_OK")));
        return t;
    }

    public static Transaction Reserved(Transaction t)
    {
        Ok(Validated(t).MarkReserved(Ctx("PAYMENT_PROCESSING")));
        return t;
    }

    public static Transaction Processing(Transaction t)
    {
        if (t.RequiresReservation)
        {
            Reserved(t);
        }
        else
        {
            Validated(t);
        }

        Ok(t.BeginProcessing(InitialRouting, Ctx("PROVIDER_PROCESSING")));
        return t;
    }

    public static Transaction Pending(Transaction t)
    {
        Ok(Processing(t).MarkPending(Ctx("PROVIDER_EXPLICIT_PENDING")));
        return t;
    }

    public static Transaction InDoubt(Transaction t)
    {
        Ok(Processing(t).MarkInDoubt(Ctx("PROVIDER_READ_TIMEOUT", ChangeSource.SyncProviderResponse)));
        return t;
    }

    public static Transaction Success(Transaction t)
    {
        Ok(Processing(t).CompleteSuccess(Ctx("PROVIDER_SUCCESS", ChangeSource.SyncProviderResponse)));
        return t;
    }

    public static Transaction Failed(Transaction t)
    {
        Ok(Processing(t).CompleteFailure(Ctx("PROVIDER_DECLINED", ChangeSource.SyncProviderResponse)));
        return t;
    }

    public static Transaction ReversalPendingUnposted(Transaction t)
    {
        Ok(InDoubt(t).BeginReversal(Ctx("REVERSAL_REQUESTED")));
        return t;
    }

    public static Transaction ReversalPendingPosted(Transaction t)
    {
        Ok(Success(t).BeginReversal(Ctx("REVERSAL_REQUESTED")));
        return t;
    }

    public static Transaction InDoubtAfterPostedReversal(Transaction t)
    {
        Ok(ReversalPendingPosted(t).MarkInDoubt(Ctx("REVERSAL_READ_TIMEOUT")));
        return t;
    }

    public static Transaction ReversedReleased(Transaction t)
    {
        Ok(ReversalPendingUnposted(t).CompleteReversal(Ctx("REVERSAL_CONFIRMED")));
        return t;
    }

    public static Transaction ReversedCompensated(Transaction t)
    {
        Ok(ReversalPendingPosted(t).CompleteReversal(Ctx("REVERSAL_CONFIRMED")));
        return t;
    }

    /// <summary>Every reachable state, for payments (reserving) and inquiries (non-reserving).</summary>
    public static IEnumerable<(string Name, Func<Transaction> Build)> ReachableStates()
    {
        yield return ("payment:RECEIVED", () => NewPayment());
        yield return ("payment:VALIDATED", () => Validated(NewPayment()));
        yield return ("payment:VALIDATED+RESERVED", () => Reserved(NewPayment()));
        yield return ("payment:PROCESSING", () => Processing(NewPayment()));
        yield return ("payment:PENDING", () => Pending(NewPayment()));
        yield return ("payment:IN_DOUBT", () => InDoubt(NewPayment()));
        yield return ("payment:SUCCESS", () => Success(NewPayment()));
        yield return ("payment:FAILED", () => Failed(NewPayment()));
        yield return ("payment:REVERSAL_PENDING+RESERVED", () => ReversalPendingUnposted(NewPayment()));
        yield return ("payment:REVERSAL_PENDING+POSTED", () => ReversalPendingPosted(NewPayment()));
        yield return ("payment:IN_DOUBT+REVERSAL_PENDING", () => InDoubtAfterPostedReversal(NewPayment()));
        yield return ("payment:REVERSED+RELEASED", () => ReversedReleased(NewPayment()));
        yield return ("payment:REVERSED+REVERSED", () => ReversedCompensated(NewPayment()));
        yield return ("inquiry:RECEIVED", () => NewInquiry());
        yield return ("inquiry:VALIDATED", () => Validated(NewInquiry()));
        yield return ("inquiry:PROCESSING", () => Processing(NewInquiry()));
        yield return ("inquiry:SUCCESS", () => Success(NewInquiry()));
        yield return ("inquiry:FAILED", () => Failed(NewInquiry()));
    }

    private static void Ok(Result<TransitionOutcome> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Builder transition failed: {result.Error}");
        }
    }
}
