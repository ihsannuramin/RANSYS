using Ransys.Adapter.Contracts.V1;
using DomainCapabilities = Ransys.Domain.Routing.ProviderCapabilities;

namespace Ransys.Adapter.Tests;

public sealed class ProviderResultRulesTests
{
    /// <summary>The complete set of valid (outcome, finality, transport status, requestSent) combinations.</summary>
    private static readonly HashSet<(ProviderOutcome, ResultFinality, TransportStatus, bool)> Valid =
    [
        (ProviderOutcome.Success, ResultFinality.Definitive, TransportStatus.Response, true),
        (ProviderOutcome.Failed, ResultFinality.Definitive, TransportStatus.Response, true),
        (ProviderOutcome.Pending, ResultFinality.NonFinal, TransportStatus.Response, true),
        (ProviderOutcome.NotSent, ResultFinality.NotApplicable, TransportStatus.NotSent, false),
        (ProviderOutcome.NotSent, ResultFinality.NotApplicable, TransportStatus.ConnectionError, false),
        (ProviderOutcome.InDoubt, ResultFinality.Ambiguous, TransportStatus.Sent, true),
        (ProviderOutcome.InDoubt, ResultFinality.Ambiguous, TransportStatus.Response, true),
        (ProviderOutcome.InDoubt, ResultFinality.Ambiguous, TransportStatus.Timeout, true),
        (ProviderOutcome.InDoubt, ResultFinality.Ambiguous, TransportStatus.ConnectionError, true),
        (ProviderOutcome.InDoubt, ResultFinality.Ambiguous, TransportStatus.ProtocolError, true),
    ];

    public static TheoryData<ProviderOutcome, ResultFinality, TransportStatus, bool> AllCombinations()
    {
        var data = new TheoryData<ProviderOutcome, ResultFinality, TransportStatus, bool>();
        foreach (var outcome in Enum.GetValues<ProviderOutcome>())
        {
            foreach (var finality in Enum.GetValues<ResultFinality>())
            {
                foreach (var status in Enum.GetValues<TransportStatus>())
                {
                    data.Add(outcome, finality, status, true);
                    data.Add(outcome, finality, status, false);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Every_combination_is_classified_and_normalization_is_conservative(
        ProviderOutcome outcome,
        ResultFinality finality,
        TransportStatus status,
        bool requestSent)
    {
        var result = Build(outcome, finality, status, requestSent);
        var expectedValid = Valid.Contains((outcome, finality, status, requestSent));

        Assert.Equal(expectedValid, ProviderResultRules.IsValid(result));
        Assert.Equal(expectedValid, ProviderResultRules.Validate(result).IsSuccess);
        Assert.Equal(expectedValid, ProviderResultRules.Explain(result) is null);

        var normalized = ProviderResultRules.Normalize(result);
        Assert.True(ProviderResultRules.IsValid(normalized));
        if (expectedValid)
        {
            Assert.Same(result, normalized);
        }
        else
        {
            Assert.Equal(ProviderResultRules.InvalidResultCode, ProviderResultRules.Validate(result).Error.Code);
            Assert.Equal(ProviderOutcome.InDoubt, normalized.Outcome);
            Assert.Equal(ResultFinality.Ambiguous, normalized.Finality);
            Assert.True(normalized.Transport.RequestSent);
            Assert.NotEqual(TransportStatus.NotSent, normalized.Transport.Status);
            Assert.Equal(ProviderResultCodes.InDoubt, normalized.RansysResponseCode);
            Assert.Null(normalized.RetryHint);
            Assert.Equal(result.ProviderResponseCode, normalized.ProviderResponseCode);
            Assert.Same(result.References, normalized.References);
        }
    }

    [Fact]
    public void Success_without_request_sent_is_normalized_to_in_doubt()
    {
        var result = Build(ProviderOutcome.Success, ResultFinality.Definitive, TransportStatus.Response, requestSent: false);

        var normalized = ProviderResultRules.Normalize(result);

        Assert.Equal(ProviderOutcome.InDoubt, normalized.Outcome);
        Assert.True(normalized.Transport.RequestSent);
        Assert.Equal(TransportStatus.Response, normalized.Transport.Status);
    }

    [Fact]
    public void Undefined_enum_values_and_missing_transport_are_invalid_and_normalize_to_in_doubt()
    {
        var undefined = Build((ProviderOutcome)99, ResultFinality.Definitive, TransportStatus.Response, true);
        var badStatus = Build(ProviderOutcome.NotSent, ResultFinality.NotApplicable, (TransportStatus)42, false);
        var noTransport = Build(ProviderOutcome.NotSent, ResultFinality.NotApplicable, TransportStatus.NotSent, false) with { Transport = null! };

        foreach (var result in new[] { undefined, badStatus, noTransport })
        {
            Assert.False(ProviderResultRules.IsValid(result));
            var normalized = ProviderResultRules.Normalize(result);
            Assert.True(ProviderResultRules.IsValid(normalized));
            Assert.Equal(ProviderOutcome.InDoubt, normalized.Outcome);
            Assert.Equal(result == undefined ? TransportStatus.Response : TransportStatus.Sent, normalized.Transport.Status);
            Assert.Equal(ProviderResultRules.NormalizedErrorCode, normalized.Transport.Error!.Code);
        }
    }

    [Fact]
    public void Capability_unsupported_is_not_sent_and_valid()
    {
        var result = ProviderResults.CapabilityUnsupported(ProviderCapabilityCodes.Void, TestData.References(), TestData.Now);

        Assert.True(ProviderResultRules.IsValid(result));
        Assert.Equal(ProviderOutcome.NotSent, result.Outcome);
        Assert.Equal(ResultFinality.NotApplicable, result.Finality);
        Assert.Equal(TransportStatus.NotSent, result.Transport.Status);
        Assert.False(result.Transport.RequestSent);
        Assert.Equal(ProviderErrorCategories.CapabilityUnsupported, result.Transport.Error!.Category);
        Assert.Equal(ProviderResultCodes.CapabilityUnsupported, result.RansysResponseCode);
    }

    [Fact]
    public void Result_factories_refuse_statuses_that_contradict_their_outcome()
    {
        var error = new ProviderError(ProviderErrorCategories.Unknown, "X", "x", false, null);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProviderResults.NotSent(TransportStatus.ProtocolError, error, "1001", TestData.References(), TestData.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProviderResults.NotSent(TransportStatus.Timeout, error, "1001", TestData.References(), TestData.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProviderResults.InDoubt(TransportStatus.NotSent, error, TestData.References(), TestData.Now));
        Assert.True(ProviderResultRules.IsValid(ProviderResults.InDoubt(TransportStatus.Timeout, error, TestData.References(), TestData.Now)));
        Assert.True(ProviderResultRules.IsValid(ProviderResults.NotSent(TransportStatus.ConnectionError, error, "0091", TestData.References(), TestData.Now)));
    }

    [Fact]
    public void Capability_codes_match_the_routing_catalog()
    {
        Assert.Equal(DomainCapabilities.All, ProviderCapabilityCodes.All);
        Assert.Equal(13, ProviderCapabilityCodes.All.Count);
        Assert.Equal("VOID", ProviderCapabilityCodes.Void);
        Assert.Equal("TRANSFER", ProviderCapabilityCodes.Transfer);
    }

    [Fact]
    public void Error_categories_match_the_contract_document()
    {
        Assert.Equal(
            ["CONNECTION", "TIMEOUT", "PROTOCOL", "AUTHENTICATION", "MAPPING", "PROVIDER_DECLINE", "CAPABILITY_UNSUPPORTED", "BACKPRESSURE", "UNKNOWN"],
            ProviderErrorCategories.All);
    }

    private static ProviderResult Build(ProviderOutcome outcome, ResultFinality finality, TransportStatus status, bool requestSent) =>
        TestData.Result(ProviderOutcome.Success) with
        {
            Outcome = outcome,
            Finality = finality,
            Transport = new ProviderTransportResult(status, requestSent, null, TimeSpan.FromMilliseconds(5), null),
        };
}
