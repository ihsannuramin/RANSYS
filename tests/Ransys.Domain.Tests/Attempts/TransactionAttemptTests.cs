using Ransys.Domain.Attempts;
using Ransys.Domain.Common;
using static Ransys.Domain.Tests.TestData;

namespace Ransys.Domain.Tests.Attempts;

public sealed class TransactionAttemptTests
{
    [Fact]
    public void Attempt_without_recorded_outcome_may_have_reached_provider()
    {
        // ADR-005: crash window between "attempt created" and "outcome recorded" must be treated as possibly sent.
        var attempt = StartAttempt();

        Assert.False(attempt.IsOutcomeRecorded);
        Assert.False(attempt.ProvesRequestNotSent);
        Assert.True(attempt.MayHaveReachedProvider);
    }

    [Theory]
    [InlineData(TransportStatus.NotSent)]
    [InlineData(TransportStatus.ConnectionError)]
    public void Explicit_not_sent_outcome_proves_non_delivery(TransportStatus status)
    {
        var attempt = StartAttempt();

        attempt.RecordOutcome(AttemptOutcome.Create(requestSent: false, status).Value);

        Assert.True(attempt.ProvesRequestNotSent);
        Assert.False(attempt.MayHaveReachedProvider);
    }

    [Theory]
    [InlineData(true, TransportStatus.Timeout)]
    [InlineData(false, TransportStatus.Timeout)]
    [InlineData(true, TransportStatus.ConnectionError)]
    [InlineData(true, TransportStatus.Response)]
    [InlineData(true, TransportStatus.Sent)]
    public void Any_other_outcome_is_treated_as_possibly_sent(bool requestSent, TransportStatus status)
    {
        var attempt = StartAttempt();

        attempt.RecordOutcome(AttemptOutcome.Create(requestSent, status).Value);

        Assert.True(attempt.MayHaveReachedProvider);
    }

    [Theory]
    [InlineData(true, TransportStatus.NotSent)]
    [InlineData(false, TransportStatus.Sent)]
    [InlineData(false, TransportStatus.Response)]
    public void Inconsistent_transport_is_rejected(bool requestSent, TransportStatus status)
    {
        Assert.Equal(ErrorCodes.AttemptInconsistentTransport, AttemptOutcome.Create(requestSent, status).Error.Code);
    }

    [Fact]
    public void Sent_timestamp_requires_request_sent()
    {
        var result = AttemptOutcome.Create(false, TransportStatus.ConnectionError, providerSentAt: T0);

        Assert.Equal(ErrorCodes.AttemptInconsistentTransport, result.Error.Code);
    }

    [Fact]
    public void Outcome_is_recorded_once_identical_repeat_is_idempotent()
    {
        var attempt = StartAttempt();
        var timeout = AttemptOutcome.Create(true, TransportStatus.Timeout, providerSentAt: T0).Value;

        Assert.True(attempt.RecordOutcome(timeout).IsSuccess);
        Assert.True(attempt.RecordOutcome(timeout).IsSuccess);

        var conflicting = AttemptOutcome.Create(true, TransportStatus.Response, providerResponseCode: "00").Value;
        var result = attempt.RecordOutcome(conflicting);

        Assert.Equal(ErrorCodes.AttemptOutcomeAlreadyRecorded, result.Error.Code);
        Assert.Equal(TransportStatus.Timeout, attempt.Outcome!.TransportStatus);
    }

    [Fact]
    public void Outcome_field_rules()
    {
        Assert.Equal(ErrorCodes.InvalidFormat, AttemptOutcome.Create(true, TransportStatus.Response, ransysResponseCode: "00").Error.Code);
        Assert.Equal(ErrorCodes.OutOfRange, AttemptOutcome.Create(true, TransportStatus.Response, latency: TimeSpan.FromMilliseconds(-1)).Error.Code);
        Assert.Equal(
            ErrorCodes.OutOfRange,
            AttemptOutcome.Create(true, TransportStatus.Response, providerSentAt: T0, providerResponseAt: T0.AddMilliseconds(-1)).Error.Code);
        Assert.Equal(ErrorCodes.TooLong, AttemptOutcome.Create(true, TransportStatus.Response, providerRrn: new string('9', 65)).Error.Code);
    }

    [Fact]
    public void Long_provider_message_is_truncated_rather_than_losing_the_outcome()
    {
        var outcome = AttemptOutcome.Create(true, TransportStatus.Response, providerResponseMessage: new string('m', 800)).Value;

        Assert.Equal(AttemptOutcome.MaxProviderResponseMessageLength, outcome.ProviderResponseMessage!.Length);
    }

    [Fact]
    public void Start_validates_attempt_number_and_correlation()
    {
        var id = new AttemptId(Guid.CreateVersion7());
        var tx = NewTransactionId();

        Assert.Equal(ErrorCodes.OutOfRange, TransactionAttempt.Start(id, tx, 0, AttemptType.Payment, ProviderRefA, "c", "t", T0).Error.Code);
        Assert.Equal(ErrorCodes.Required, TransactionAttempt.Start(id, tx, 1, AttemptType.Payment, ProviderRefA, "", "t", T0).Error.Code);
        Assert.Equal(ErrorCodes.TooLong, TransactionAttempt.Start(id, tx, 1, AttemptType.Payment, ProviderRefA, "c", new string('t', 129), T0).Error.Code);
    }

    [Fact]
    public void Rehydrated_attempt_keeps_its_outcome()
    {
        var outcome = AttemptOutcome.Create(false, TransportStatus.NotSent).Value;

        var attempt = TransactionAttempt.Rehydrate(
            new AttemptId(Guid.CreateVersion7()), NewTransactionId(), 2, AttemptType.StatusCheck, ProviderRefB, "c", "t", T0, outcome).Value;

        Assert.True(attempt.ProvesRequestNotSent);
        Assert.Equal(2, attempt.AttemptNumber);
    }

    [Fact]
    public void Raw_message_references_are_bounded_and_not_blank()
    {
        Assert.Equal(ErrorCodes.TooLong, RawMessageReferences.Create(new string('r', 1001), null).Error.Code);
        Assert.Equal(ErrorCodes.Required, RawMessageReferences.Create(null, " ").Error.Code);
        Assert.Equal(
            "rawmsg://provider-a/2026/09/26/x/request",
            RawMessageReferences.Create("rawmsg://provider-a/2026/09/26/x/request", null).Value.RequestUri);
    }
}
