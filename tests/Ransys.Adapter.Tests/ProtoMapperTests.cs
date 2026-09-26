using System.Text.Json;
using Google.Protobuf;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using Ransys.Adapter.Contracts.V1;
using Ransys.Adapter.Sdk;
using Wire = Ransys.Provider.V1;

namespace Ransys.Adapter.Tests;

public sealed class ProtoMapperTests
{
    [Fact]
    public void Full_request_round_trips_through_the_wire_format()
    {
        var request = TestData.Request();

        var bytes = ProtoMapper.ToProto(request).ToByteArray();
        var back = ProtoMapper.FromProto(Wire.ProviderTransactionRequest.Parser.ParseFrom(bytes));

        Assert.Equal(TestData.Canonical(request), TestData.Canonical(back));
    }

    [Fact]
    public void Minimal_request_round_trips_and_leaves_optional_messages_absent()
    {
        var request = TestData.MinimalRequest();

        var wire = ProtoMapper.ToProto(request);
        var back = ProtoMapper.FromProto(wire);

        Assert.Null(wire.Amount);
        Assert.Null(wire.Customer);
        Assert.Null(wire.Source);
        Assert.Null(wire.Destination);
        Assert.False(wire.HasOriginalTransactionId);
        Assert.Equal(TestData.Canonical(request), TestData.Canonical(back));
    }

    [Fact]
    public void Money_keeps_full_decimal_precision_and_scale()
    {
        var wire = ProtoMapper.ToProto(TestData.Request(amount: 123456789.12345678m));

        Assert.Equal("123456789.12345678", wire.Amount.Value);
        Assert.Equal("IDR", wire.Amount.Currency);
        Assert.Equal(1, wire.Amount.CurrencyDefinitionVersion);
        Assert.Equal(123456789.12345678m, ProtoMapper.FromProto(wire).Amount);
        Assert.Equal("100.00", ProtoMapper.FormatMoney(100.00m));
        Assert.Equal("100.00", ProtoMapper.FormatMoney(ProtoMapper.ParseMoney("100.00")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("1,000.00")]
    [InlineData("1.000,00")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("+1")]
    [InlineData("NaN")]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("79228162514264337593543950336")]
    public void Malformed_or_inexact_money_strings_are_rejected(string value)
    {
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ParseMoney(value));

        var wire = ProtoMapper.ToProto(TestData.Request());
        wire.Amount.Value = value;
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(wire));
    }

    [Fact]
    public void Full_and_minimal_results_round_trip_for_every_outcome()
    {
        foreach (var outcome in Enum.GetValues<ProviderOutcome>())
        {
            foreach (var full in new[] { true, false })
            {
                var result = TestData.Result(outcome, full);

                var bytes = ProtoMapper.ToProto(result).ToByteArray();
                var back = ProtoMapper.FromProto(Wire.ProviderResult.Parser.ParseFrom(bytes));

                Assert.Equal(TestData.Canonical(result), TestData.Canonical(back));
            }
        }
    }

    [Theory]
    [InlineData(TransportStatus.NotSent, Wire.TransportStatus.NotSentTransport)]
    [InlineData(TransportStatus.Sent, Wire.TransportStatus.Sent)]
    [InlineData(TransportStatus.Response, Wire.TransportStatus.Response)]
    [InlineData(TransportStatus.Timeout, Wire.TransportStatus.Timeout)]
    [InlineData(TransportStatus.ConnectionError, Wire.TransportStatus.ConnectionError)]
    [InlineData(TransportStatus.ProtocolError, Wire.TransportStatus.ProtocolError)]
    public void Transport_status_maps_both_ways(TransportStatus contract, Wire.TransportStatus wire)
    {
        Assert.Equal(wire, ProtoMapper.ToProto(contract));
        Assert.Equal(contract, ProtoMapper.FromProto(wire));
    }

    [Fact]
    public void Unspecified_wire_enums_are_rejected()
    {
        var valid = ProtoMapper.ToProto(TestData.Result(ProviderOutcome.Success));

        var outcome = valid.Clone();
        outcome.Outcome = Wire.ProviderOutcome.Unspecified;
        var finality = valid.Clone();
        finality.Finality = Wire.ResultFinality.Unspecified;
        var transport = valid.Clone();
        transport.Transport.Status = Wire.TransportStatus.Unspecified;
        var request = ProtoMapper.ToProto(TestData.Request());
        request.TransactionType = Wire.TransactionType.Unspecified;

        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(outcome));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(finality));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(transport));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(request));
    }

    [Theory]
    [InlineData("INQUIRY", Wire.TransactionType.Inquiry)]
    [InlineData("PAYMENT", Wire.TransactionType.Payment)]
    [InlineData("PURCHASE", Wire.TransactionType.Purchase)]
    [InlineData("TRANSFER", Wire.TransactionType.Transfer)]
    [InlineData("REFUND", Wire.TransactionType.Refund)]
    [InlineData("REVERSAL", Wire.TransactionType.Reversal)]
    [InlineData("VOID", Wire.TransactionType.Void)]
    [InlineData("ADVICE", Wire.TransactionType.Advice)]
    [InlineData("BALANCE_INQUIRY", Wire.TransactionType.BalanceInquiry)]
    [InlineData("STATUS_CHECK", Wire.TransactionType.StatusCheck)]
    public void Transaction_types_map_one_to_one(string contract, Wire.TransactionType wire)
    {
        var mapped = ProtoMapper.ToProto(TestData.Request(contract));

        Assert.Equal(wire, mapped.TransactionType);
        Assert.Equal(contract, ProtoMapper.FromProto(mapped).TransactionType);
    }

    [Theory]
    [InlineData("payment")]
    [InlineData("SALE")]
    [InlineData("")]
    public void Unknown_transaction_type_is_rejected(string type)
    {
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(TestData.Request(type)));
    }

    [Fact]
    public void Unknown_customer_or_endpoint_keys_are_rejected_not_dropped()
    {
        var customer = TestData.Request() with { Customer = TestData.Json("""{"customerId":"C-1","pin":"1234"}""") };
        var destination = TestData.Request() with { Destination = TestData.Json("""{"type":"WALLET","identifier":"W","extra":"x"}""") };
        var badType = TestData.Request() with { Destination = TestData.Json("""{"type":"PLANET","identifier":"W"}""") };
        var noIdentifier = TestData.Request() with { Source = TestData.Json("""{"type":"WALLET"}""") };
        var nonString = TestData.Request() with { Customer = TestData.Json("""{"customerId":42}""") };

        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(customer));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(destination));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(badType));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(noIdentifier));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(nonString));
    }

    [Theory]
    [InlineData("123456789.12345678123")]
    [InlineData("12345678901234567890")]
    [InlineData("0.1000000000000000000001")]
    public void Json_numbers_that_do_not_survive_the_double_round_trip_are_rejected(string number)
    {
        var request = TestData.Request() with { Metadata = TestData.Json($$"""{"amount":{{number}}}""") };

        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(request));
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("2")]
    [InlineData("-17.25")]
    [InlineData("1.50")]
    [InlineData("1e3")]
    public void Json_numbers_that_survive_the_double_round_trip_are_accepted(string number)
    {
        var request = TestData.Request() with { Metadata = TestData.Json($$"""{"n":{{number}}}""") };

        var back = ProtoMapper.FromProto(ProtoMapper.ToProto(request));

        Assert.Equal(decimal.Parse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture), back.Metadata["n"].GetDecimal());
    }

    [Fact]
    public void Malformed_guids_and_missing_required_fields_are_rejected()
    {
        var badGuid = ProtoMapper.ToProto(TestData.Request());
        badGuid.AttemptId = "not-a-guid";
        var emptyGuid = ProtoMapper.ToProto(TestData.Request());
        emptyGuid.RansysTransactionId = Guid.Empty.ToString();
        var noContext = ProtoMapper.ToProto(TestData.Request());
        noContext.ExecutionContext = null;
        var noReceivedAt = ProtoMapper.ToProto(TestData.Result(ProviderOutcome.Success));
        noReceivedAt.ReceivedAt = null;
        var noTransport = ProtoMapper.ToProto(TestData.Result(ProviderOutcome.Success));
        noTransport.Transport = null;

        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(badGuid));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(emptyGuid));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(noContext));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(noReceivedAt));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(noTransport));
    }

    [Fact]
    public void Amount_and_currency_must_come_together()
    {
        var noCurrency = TestData.Request() with { CurrencyCode = null };
        var currencyWithoutAmount = TestData.Request(amount: null) with { CurrencyCode = "IDR" };

        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(noCurrency));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(currencyWithoutAmount));
    }

    [Fact]
    public void Callback_and_ack_round_trip()
    {
        var callback = TestData.Callback();
        var ack = new ProviderCallbackAck(true, "ACK-1");

        var callbackBack = ProtoMapper.FromProto(Wire.ProviderCallback.Parser.ParseFrom(ProtoMapper.ToProto(callback).ToByteArray()));
        var ackBack = ProtoMapper.FromProto(ProtoMapper.ToProto(ack));

        Assert.Equal(TestData.Canonical(callback), TestData.Canonical(callbackBack));
        Assert.Equal(ack, ackBack);
        Assert.Equal(new ProviderCallbackAck(false, null), ProtoMapper.FromProto(ProtoMapper.ToProto(new ProviderCallbackAck(false, null))));
    }

    [Theory]
    [InlineData("HEALTHY")]
    [InlineData("DEGRADED")]
    [InlineData("UNHEALTHY")]
    public void Health_round_trips(string state)
    {
        var health = new ProviderHealthResult(state, 25, "D1", "slow", TestData.Now);

        Assert.Equal(health, ProtoMapper.FromProto(ProtoMapper.ToProto(health)));
        Assert.Equal(health with { LatencyMs = null, DiagnosticCode = null, DiagnosticMessage = null },
            ProtoMapper.FromProto(ProtoMapper.ToProto(health with { LatencyMs = null, DiagnosticCode = null, DiagnosticMessage = null })));
    }

    [Fact]
    public void Unknown_health_state_is_rejected()
    {
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.ToProto(new ProviderHealthResult("OK", null, null, null, TestData.Now)));
        Assert.Throws<ProtoMappingException>(() => ProtoMapper.FromProto(new Wire.HealthCheckResponse { ObservedAt = Timestamp.FromDateTimeOffset(TestData.Now) }));
    }

    [Fact]
    public void Balance_round_trips_with_exact_decimal()
    {
        var balance = new ProviderBalanceResult(123456789.12345678m, "IDR", 1, TestData.Now, "PROVIDER_API");

        var back = ProtoMapper.FromProto(ProtoMapper.ToProto(balance));

        Assert.Equal(balance, back);
        Assert.Equal("123456789.12345678", ProtoMapper.ToProto(balance).Balance.Value);
    }

    [Fact]
    public void Capabilities_and_identity_round_trip()
    {
        var capabilities = new ProviderCapabilities(new HashSet<string>(ProviderCapabilityCodes.All), "1.0");

        var back = ProtoMapper.FromProto(ProtoMapper.ToProto(capabilities));

        Assert.True(back.CapabilityCodes.SetEquals(ProviderCapabilityCodes.All));
        Assert.Equal("1.0", back.ContractVersion);
        Assert.Equal(TestData.Identity, ProtoMapper.FromProto(ProtoMapper.ToProto(TestData.Identity)));
    }

    [Fact]
    public void Struct_data_keeps_strings_nested_objects_arrays_and_nulls()
    {
        var result = TestData.Result(ProviderOutcome.Success) with
        {
            Data = TestData.Json("""{"s":"x","o":{"a":[1,"b",null,{"c":false}]},"z":null}"""),
        };

        var back = ProtoMapper.FromProto(ProtoMapper.ToProto(result));

        Assert.Equal(JsonValueKind.Null, back.Data["z"].ValueKind);
        Assert.Equal(TestData.Canonical(result), TestData.Canonical(back));
    }
}
