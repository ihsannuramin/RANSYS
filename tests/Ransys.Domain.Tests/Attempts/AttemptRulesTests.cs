using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using Ransys.Domain.Transactions;
using static Ransys.Domain.Tests.TestData;
using static Ransys.Domain.Tests.Transactions.TransactionBuilder;

namespace Ransys.Domain.Tests.Attempts;

public sealed class AuthorizeAttemptTests
{
    [Fact]
    public void First_payment_attempt_goes_to_the_routed_provider_while_processing()
    {
        var t = Processing(NewPayment());

        Assert.True(t.AuthorizeAttempt(AttemptType.Payment, ProviderRefA, []).IsSuccess);
        Assert.Equal(ErrorCodes.AttemptNotAllowed, t.AuthorizeAttempt(AttemptType.Payment, ProviderRefB, []).Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_new_financial_request_after_a_possibly_sent_one(bool recordTimeout)
    {
        var t = Processing(NewPayment());
        var first = Attempt(t, 1, AttemptType.Payment, ProviderRefA);
        if (recordTimeout)
        {
            first.RecordOutcome(AttemptOutcome.Create(true, TransportStatus.Timeout).Value);
        }

        var result = t.AuthorizeAttempt(AttemptType.Payment, ProviderRefA, [first]);

        Assert.Equal(ErrorCodes.FailoverNotAllowed, result.Error.Code);
        Assert.Equal(ErrorCategory.Financial, result.Error.Category);
    }

    [Fact]
    public void Proven_not_sent_attempt_allows_failover_and_a_new_attempt_on_the_next_provider()
    {
        var t = Processing(NewPayment());
        var first = Attempt(t, 1, AttemptType.Payment, ProviderRefA);
        first.RecordOutcome(AttemptOutcome.Create(false, TransportStatus.ConnectionError).Value);

        Assert.True(t.RecordFailover(first, ProviderRefB, "PROVIDER_LINK_DOWN", T0).IsSuccess);
        Assert.True(t.AuthorizeAttempt(AttemptType.Payment, ProviderRefB, [first]).IsSuccess);
        Assert.Equal(ErrorCodes.AttemptNotAllowed, t.AuthorizeAttempt(AttemptType.Payment, ProviderRefA, [first]).Error.Code);
    }

    [Fact]
    public void Status_checks_are_allowed_while_the_outcome_is_open()
    {
        Assert.True(InDoubt(NewPayment()).AuthorizeAttempt(AttemptType.StatusCheck, ProviderRefA, []).IsSuccess);
        Assert.True(Pending(NewPayment()).AuthorizeAttempt(AttemptType.StatusCheck, ProviderRefA, []).IsSuccess);
        Assert.True(InDoubt(NewReversalChild()).AuthorizeAttempt(AttemptType.StatusCheck, ProviderRefA, []).IsSuccess);
        Assert.Equal(ErrorCodes.AttemptNotAllowed, Success(NewPayment()).AuthorizeAttempt(AttemptType.StatusCheck, ProviderRefA, []).Error.Code);
        Assert.Equal(ErrorCodes.AttemptNotAllowed, Failed(NewPayment()).AuthorizeAttempt(AttemptType.StatusCheck, ProviderRefA, []).Error.Code);
    }

    [Fact]
    public void Financial_request_is_never_resent_once_in_doubt()
    {
        var t = InDoubt(NewPayment());

        Assert.Equal(ErrorCodes.AttemptNotAllowed, t.AuthorizeAttempt(AttemptType.Payment, ProviderRefA, []).Error.Code);
    }

    [Fact]
    public void Reversal_request_is_sent_only_by_a_processing_reversal_child()
    {
        // ADR-012: never by the original, whatever its state.
        Assert.Equal(ErrorCodes.AttemptNotAllowed, InDoubt(NewPayment()).AuthorizeAttempt(AttemptType.Reversal, ProviderRefA, []).Error.Code);
        Assert.Equal(ErrorCodes.AttemptNotAllowed, Success(NewPayment()).AuthorizeAttempt(AttemptType.Reversal, ProviderRefA, []).Error.Code);

        var child = Processing(NewReversalChild());
        Assert.True(child.AuthorizeAttempt(AttemptType.Reversal, ProviderRefA, []).IsSuccess);

        var first = Attempt(child, 1, AttemptType.Reversal, ProviderRefA);
        first.RecordOutcome(AttemptOutcome.Create(true, TransportStatus.Timeout).Value);
        Assert.Equal(ErrorCodes.FailoverNotAllowed, child.AuthorizeAttempt(AttemptType.Reversal, ProviderRefA, [first]).Error.Code);
    }

    public static TheoryData<TransactionType, AttemptType> PrimaryRequestTypes() => new()
    {
        { TransactionType.Payment, AttemptType.Payment },
        { TransactionType.Purchase, AttemptType.Payment },
        { TransactionType.Transfer, AttemptType.Transfer },
        { TransactionType.Refund, AttemptType.Refund },
        { TransactionType.Reversal, AttemptType.Reversal },
        { TransactionType.Void, AttemptType.Void },
        { TransactionType.Inquiry, AttemptType.Inquiry },
        { TransactionType.BalanceInquiry, AttemptType.BalanceInquiry },
    };

    [Theory]
    [MemberData(nameof(PrimaryRequestTypes))]
    public void Primary_request_type_must_match_the_transaction_type(TransactionType type, AttemptType expected)
    {
        Assert.Equal(expected, Transaction.PrimaryAttemptTypeFor(type));

        AttemptType[] primaries =
        [
            AttemptType.Payment, AttemptType.Transfer, AttemptType.Refund, AttemptType.Reversal, AttemptType.Void,
            AttemptType.Inquiry, AttemptType.BalanceInquiry,
        ];
        foreach (var attemptType in primaries)
        {
            var t = Processing(NewOfType(type));
            var result = t.AuthorizeAttempt(attemptType, ProviderRefA, []);

            if (attemptType == expected)
            {
                Assert.True(result.IsSuccess, $"{type}/{attemptType}");
            }
            else
            {
                Assert.Equal(ErrorCodes.AttemptNotAllowed, result.Error.Code);
            }
        }
    }

    [Theory]
    [InlineData(TransactionType.Transfer, AttemptType.Transfer)]
    [InlineData(TransactionType.Void, AttemptType.Void)]
    [InlineData(TransactionType.Refund, AttemptType.Refund)]
    [InlineData(TransactionType.Purchase, AttemptType.Payment)]
    public void Financial_requests_are_never_resent_after_a_possible_send(TransactionType type, AttemptType attemptType)
    {
        var t = Processing(NewOfType(type));
        var sent = Attempt(t, 1, attemptType, ProviderRefA);
        sent.RecordOutcome(AttemptOutcome.Create(true, TransportStatus.ProtocolError).Value);

        Assert.Equal(ErrorCodes.FailoverNotAllowed, t.AuthorizeAttempt(attemptType, ProviderRefA, [sent]).Error.Code);

        var fresh = Processing(NewOfType(type));
        var notSent = Attempt(fresh, 1, attemptType, ProviderRefA);
        notSent.RecordOutcome(AttemptOutcome.Create(false, TransportStatus.NotSent).Value);
        Assert.True(fresh.AuthorizeAttempt(attemptType, ProviderRefA, [notSent]).IsSuccess);
    }

    [Fact]
    public void Protocol_error_before_send_does_not_prove_non_delivery()
    {
        // ADR-018: only NOT_SENT / CONNECTION_ERROR with requestSent=false prove the request never left RANSYS.
        var t = Processing(NewOfType(TransactionType.Transfer));
        var first = Attempt(t, 1, AttemptType.Transfer, ProviderRefA);
        first.RecordOutcome(AttemptOutcome.Create(false, TransportStatus.ProtocolError).Value);

        Assert.True(first.MayHaveReachedProvider);
        Assert.Equal(ErrorCodes.FailoverNotAllowed, t.AuthorizeAttempt(AttemptType.Transfer, ProviderRefA, [first]).Error.Code);
    }

    [Fact]
    public void Inquiries_are_not_financial_and_may_be_repeated_while_processing()
    {
        var t = Processing(NewInquiry());
        var first = Attempt(t, 1, AttemptType.Inquiry, ProviderRefA);
        first.RecordOutcome(AttemptOutcome.Create(true, TransportStatus.Timeout).Value);

        Assert.True(t.AuthorizeAttempt(AttemptType.Inquiry, ProviderRefA, [first]).IsSuccess);
    }

    [Fact]
    public void Attempt_needs_a_routing_decision()
    {
        var t = Reserved(NewPayment());

        Assert.Equal(ErrorCodes.AttemptNotAllowed, t.AuthorizeAttempt(AttemptType.Payment, ProviderRefA, []).Error.Code);
    }

    [Fact]
    public void Attempts_of_another_transaction_are_a_programming_error()
    {
        var t = Processing(NewPayment());

        Assert.Throws<ArgumentException>(() => t.AuthorizeAttempt(AttemptType.StatusCheck, ProviderRefA, [StartAttempt()]));
    }

    private static Transaction NewOfType(TransactionType type) => type switch
    {
        TransactionType.Refund or TransactionType.Reversal or TransactionType.Void => New(type, Rp(100_000m), NewTransactionId()),
        TransactionType.Inquiry or TransactionType.BalanceInquiry => New(type, Rp(0m)),
        _ => New(type, Rp(100_000m)),
    };

    private static TransactionAttempt Attempt(Transaction t, int number, AttemptType type, global::Ransys.Domain.Routing.ProviderReference provider) =>
        TransactionAttempt.Start(new AttemptId(Guid.CreateVersion7()), t.Id, number, type, provider, "c", "t", T0).Value;
}

public sealed class AttemptResolutionTests
{
    public static TheoryData<bool?, TransportStatus?, ProviderOutcome?, AttemptResolutionKind> Cases() => new()
    {
        { null, null, null, AttemptResolutionKind.InDoubt },                                            // no outcome recorded
        { false, TransportStatus.NotSent, null, AttemptResolutionKind.NotSent },
        { false, TransportStatus.ConnectionError, null, AttemptResolutionKind.NotSent },
        { true, TransportStatus.ConnectionError, null, AttemptResolutionKind.InDoubt },                 // reset after send
        { true, TransportStatus.Timeout, null, AttemptResolutionKind.InDoubt },                         // TIMEOUT != FAILED
        { false, TransportStatus.Timeout, null, AttemptResolutionKind.InDoubt },
        { true, TransportStatus.Sent, null, AttemptResolutionKind.InDoubt },
        { true, TransportStatus.Response, ProviderOutcome.Success, AttemptResolutionKind.Success },
        { true, TransportStatus.Response, ProviderOutcome.Failed, AttemptResolutionKind.Failed },
        { true, TransportStatus.Response, ProviderOutcome.Pending, AttemptResolutionKind.Pending },
        { true, TransportStatus.Response, ProviderOutcome.InDoubt, AttemptResolutionKind.InDoubt },
        { true, TransportStatus.Response, ProviderOutcome.NotSent, AttemptResolutionKind.InDoubt },     // contradictory
        { true, TransportStatus.Response, null, AttemptResolutionKind.InDoubt },                        // unmapped response
        { true, TransportStatus.Timeout, ProviderOutcome.Failed, AttemptResolutionKind.InDoubt },       // no response, no proof
        { true, TransportStatus.ProtocolError, null, AttemptResolutionKind.InDoubt },                   // ADR-018: unreadable reply
        { true, TransportStatus.ProtocolError, ProviderOutcome.Success, AttemptResolutionKind.InDoubt },
        { false, TransportStatus.ProtocolError, null, AttemptResolutionKind.InDoubt },                  // never proves not-sent
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify(bool? requestSent, TransportStatus? transport, ProviderOutcome? provider, AttemptResolutionKind expected)
    {
        var outcome = requestSent is { } sent ? AttemptOutcome.Create(sent, transport!.Value).Value : null;

        Assert.Equal(expected, AttemptResolution.Classify(outcome, provider));
    }
}
