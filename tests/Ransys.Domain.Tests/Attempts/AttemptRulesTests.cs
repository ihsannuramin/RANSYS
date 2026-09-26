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
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify(bool? requestSent, TransportStatus? transport, ProviderOutcome? provider, AttemptResolutionKind expected)
    {
        var outcome = requestSent is { } sent ? AttemptOutcome.Create(sent, transport!.Value).Value : null;

        Assert.Equal(expected, AttemptResolution.Classify(outcome, provider));
    }
}
