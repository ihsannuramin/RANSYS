using System.Text.Json;
using Contract = Ransys.Adapter.Contracts.V1;
using Wire = Ransys.Provider.V1;

namespace Ransys.Adapter.Sdk;

/// <summary>
/// The single bidirectional mapping between the Protobuf wire contract (<c>Ransys.Provider.V1</c>, generated from
/// <c>ransys_provider_adapter_v1.proto</c>) and the C# contract (<c>Ransys.Adapter.Contracts.V1</c>). Both gRPC
/// bindings use only this class, so the in-process and remote bindings carry identical semantics.
/// </summary>
/// <remarks>
/// Every method throws <see cref="ProtoMappingException"/> instead of guessing. Mapping choices where the two
/// contracts differ in shape:
/// <list type="bullet">
/// <item><b>Money</b>: <c>Money.value</c> is a decimal string (<see cref="FormatMoney"/>/<see cref="ParseMoney"/>);
/// malformed or inexact strings are rejected. The C# contract has no currency scale, so <c>currency_scale</c> is
/// written as 0 (unspecified) and ignored on read; the currency definition version identifies the scale.
/// An amount requires a currency code and definition version, and vice versa.</item>
/// <item><b>Product</b>: only <c>ProductReference.product_code</c> is carried (<c>ProductCode</c>);
/// <c>product_id</c>/<c>category</c> are written empty and ignored on read.</item>
/// <item><b>Customer / Source / Destination</b>: the C# dictionaries use the OpenAPI v1 field names
/// (<c>CustomerInput</c>: customerId, externalCustomerReference, accountNumber, phoneNumber, name, metadata;
/// <c>EndpointInput</c>: type, identifier, institutionCode, accountReference, metadata). Text fields must be JSON
/// strings, <c>metadata</c> a JSON object, <c>type</c> an <c>EndpointType</c> name. Unknown keys are rejected
/// (never dropped). An empty dictionary maps to an absent message and back.</item>
/// <item><b>External references</b>: dictionary key = <c>ExternalReference.type</c>, value = <c>value</c>;
/// <c>source</c>/<c>is_primary</c> are written empty/false and ignored on read. Duplicate types are rejected.</item>
/// <item><b>Struct</b> (metadata, data): JSON numbers must survive the double round trip (money belongs in strings).</item>
/// <item><b>Enums</b>: transaction types and health states use the proto names (e.g. <c>BALANCE_INQUIRY</c>,
/// <c>HEALTHY</c>). <c>TransportStatus.NotSent</c> ↔ <c>NOT_SENT_TRANSPORT</c>. <c>*_UNSPECIFIED</c> is rejected.</item>
/// <item><b>IDs</b>: GUIDs in "D" format; the empty GUID is rejected. Timestamps are UTC.</item>
/// </list>
/// </remarks>
public static partial class ProtoMapper
{
    private const string CustomerId = "customerId";
    private const string ExternalCustomerReference = "externalCustomerReference";
    private const string AccountNumber = "accountNumber";
    private const string PhoneNumber = "phoneNumber";
    private const string Name = "name";
    private const string MetadataKey = "metadata";
    private const string EndpointTypeKey = "type";
    private const string Identifier = "identifier";
    private const string InstitutionCode = "institutionCode";
    private const string AccountReference = "accountReference";

    private static readonly Dictionary<string, Wire.TransactionType> TransactionTypes = new(StringComparer.Ordinal)
    {
        ["INQUIRY"] = Wire.TransactionType.Inquiry,
        ["PAYMENT"] = Wire.TransactionType.Payment,
        ["PURCHASE"] = Wire.TransactionType.Purchase,
        ["TRANSFER"] = Wire.TransactionType.Transfer,
        ["REFUND"] = Wire.TransactionType.Refund,
        ["REVERSAL"] = Wire.TransactionType.Reversal,
        ["VOID"] = Wire.TransactionType.Void,
        ["ADVICE"] = Wire.TransactionType.Advice,
        ["BALANCE_INQUIRY"] = Wire.TransactionType.BalanceInquiry,
        ["STATUS_CHECK"] = Wire.TransactionType.StatusCheck,
    };

    private static readonly Dictionary<string, Wire.EndpointType> EndpointTypes = new(StringComparer.Ordinal)
    {
        ["MERCHANT"] = Wire.EndpointType.Merchant,
        ["CUSTOMER"] = Wire.EndpointType.Customer,
        ["BANK_ACCOUNT"] = Wire.EndpointType.BankAccount,
        ["WALLET"] = Wire.EndpointType.Wallet,
        ["BILLER"] = Wire.EndpointType.Biller,
        ["PROVIDER"] = Wire.EndpointType.Provider,
        ["VIRTUAL_ACCOUNT"] = Wire.EndpointType.VirtualAccount,
        ["MOBILE_NUMBER"] = Wire.EndpointType.MobileNumber,
        ["CUSTOM"] = Wire.EndpointType.Custom,
    };

    private static readonly Dictionary<string, Wire.HealthState> HealthStates = new(StringComparer.Ordinal)
    {
        ["HEALTHY"] = Wire.HealthState.Healthy,
        ["DEGRADED"] = Wire.HealthState.Degraded,
        ["UNHEALTHY"] = Wire.HealthState.Unhealthy,
    };

    // ---- identity / capabilities / health / balance ---------------------------------------------------------

    public static Wire.ProviderIdentity ToProto(Contract.ProviderIdentity identity)
    {
        Required(identity, "provider");
        return new Wire.ProviderIdentity
        {
            ProviderId = FormatGuid(identity.ProviderId, "provider.providerId"),
            ProviderCode = RequiredText(identity.ProviderCode, "provider.providerCode"),
            AdapterService = identity.AdapterService ?? string.Empty,
        };
    }

    public static Contract.ProviderIdentity FromProto(Wire.ProviderIdentity? identity)
    {
        var value = Required(identity, "provider");
        return new Contract.ProviderIdentity(
            ParseGuid(value.ProviderId, "provider.providerId"),
            RequiredText(value.ProviderCode, "provider.providerCode"),
            value.AdapterService);
    }

    public static Wire.GetCapabilitiesResponse ToProto(Contract.ProviderCapabilities capabilities)
    {
        Required(capabilities, "capabilities");
        var response = new Wire.GetCapabilitiesResponse { ContractVersion = capabilities.ContractVersion ?? string.Empty };
        response.CapabilityCodes.AddRange(Required(capabilities.CapabilityCodes, "capabilities.capabilityCodes"));
        return response;
    }

    public static Contract.ProviderCapabilities FromProto(Wire.GetCapabilitiesResponse? response)
    {
        var value = Required(response, "capabilities");
        return new Contract.ProviderCapabilities(new HashSet<string>(value.CapabilityCodes, StringComparer.Ordinal), value.ContractVersion);
    }

    public static Wire.HealthCheckResponse ToProto(Contract.ProviderHealthResult health)
    {
        Required(health, "health");
        if (health.State is null || !HealthStates.TryGetValue(health.State, out var state))
        {
            throw new ProtoMappingException("health.state must be HEALTHY, DEGRADED or UNHEALTHY.");
        }

        var response = new Wire.HealthCheckResponse { State = state, ObservedAt = ToTimestamp(health.ObservedAt) };
        if (health.LatencyMs is { } latency)
        {
            response.LatencyMs = latency;
        }

        if (health.DiagnosticCode is not null)
        {
            response.DiagnosticCode = health.DiagnosticCode;
        }

        if (health.DiagnosticMessage is not null)
        {
            response.DiagnosticMessage = health.DiagnosticMessage;
        }

        return response;
    }

    public static Contract.ProviderHealthResult FromProto(Wire.HealthCheckResponse? response)
    {
        var value = Required(response, "health");
        var state = HealthStates.FirstOrDefault(pair => pair.Value == value.State).Key
            ?? throw new ProtoMappingException("health.state is unspecified or unknown.");

        return new Contract.ProviderHealthResult(
            state,
            value.HasLatencyMs ? value.LatencyMs : null,
            value.HasDiagnosticCode ? value.DiagnosticCode : null,
            value.HasDiagnosticMessage ? value.DiagnosticMessage : null,
            FromTimestamp(value.ObservedAt, "health.observedAt"));
    }

    public static Wire.GetProviderBalanceRequest ToProto(Contract.ProviderIdentity identity, string? balanceAccountReference)
    {
        var request = new Wire.GetProviderBalanceRequest { Provider = ToProto(identity) };
        if (balanceAccountReference is not null)
        {
            request.BalanceAccountReference = balanceAccountReference;
        }

        return request;
    }

    public static Wire.GetProviderBalanceResponse ToProto(Contract.ProviderBalanceResult balance)
    {
        Required(balance, "balance");
        return new Wire.GetProviderBalanceResponse
        {
            Balance = ToMoney(balance.Balance, balance.CurrencyCode, balance.CurrencyDefinitionVersion, "balance"),
            ObservedAt = ToTimestamp(balance.ObservedAt),
            Source = balance.Source ?? string.Empty,
        };
    }

    public static Contract.ProviderBalanceResult FromProto(Wire.GetProviderBalanceResponse? response)
    {
        var value = Required(response, "balance");
        var (amount, currency, version) = FromMoney(value.Balance, "balance");
        return new Contract.ProviderBalanceResult(
            amount,
            currency,
            version,
            FromTimestamp(value.ObservedAt, "balance.observedAt"),
            value.Source);
    }

    // ---- transaction request --------------------------------------------------------------------------------

    public static Wire.ProviderTransactionRequest ToProto(Contract.ProviderTransactionRequest request)
    {
        Required(request, "request");
        if (request.TransactionType is null || !TransactionTypes.TryGetValue(request.TransactionType, out var type))
        {
            throw new ProtoMappingException($"request.transactionType '{request.TransactionType}' is not a contract transaction type.");
        }

        var wire = new Wire.ProviderTransactionRequest
        {
            RansysTransactionId = FormatGuid(request.RansysTransactionId, "request.ransysTransactionId"),
            AttemptId = FormatGuid(request.AttemptId, "request.attemptId"),
            TransactionType = type,
            Product = new Wire.ProductReference { ProductCode = RequiredText(request.ProductCode, "request.productCode") },
            Customer = ToCustomer(request.Customer),
            Source = ToEndpoint(request.Source, "request.source"),
            Destination = ToEndpoint(request.Destination, "request.destination"),
            References = ToProto(request.References),
            ExecutionContext = ToProto(request.ExecutionContext),
            Correlation = ToProto(request.Correlation),
            Metadata = ToStruct(request.Metadata, "request.metadata"),
        };

        if (request.OriginalTransactionId is { } original)
        {
            wire.OriginalTransactionId = FormatGuid(original, "request.originalTransactionId");
        }

        if (request.Amount is { } amount)
        {
            wire.Amount = ToMoney(
                amount,
                request.CurrencyCode,
                request.CurrencyDefinitionVersion ?? throw new ProtoMappingException("request.currencyDefinitionVersion is required with an amount."),
                "request.amount");
        }
        else if (request.CurrencyCode is not null || request.CurrencyDefinitionVersion is not null)
        {
            throw new ProtoMappingException("request currency fields require an amount.");
        }

        return wire;
    }

    public static Contract.ProviderTransactionRequest FromProto(Wire.ProviderTransactionRequest? request)
    {
        var value = Required(request, "request");
        var type = TransactionTypes.FirstOrDefault(pair => pair.Value == value.TransactionType).Key
            ?? throw new ProtoMappingException("request.transactionType is unspecified or unknown.");

        decimal? amount = null;
        string? currency = null;
        int? version = null;
        if (value.Amount is not null)
        {
            (amount, currency, version) = FromMoney(value.Amount, "request.amount");
        }

        return new Contract.ProviderTransactionRequest(
            ParseGuid(value.RansysTransactionId, "request.ransysTransactionId"),
            ParseOptionalGuid(value.HasOriginalTransactionId, value.OriginalTransactionId, "request.originalTransactionId"),
            ParseGuid(value.AttemptId, "request.attemptId"),
            type,
            RequiredText(Required(value.Product, "request.product").ProductCode, "request.product.productCode"),
            amount,
            currency,
            version,
            FromCustomer(value.Customer),
            FromEndpoint(value.Source, "request.source"),
            FromEndpoint(value.Destination, "request.destination"),
            FromProto(value.References),
            FromProto(value.ExecutionContext),
            FromProto(value.Correlation),
            FromStruct(value.Metadata, "request.metadata"));
    }

    public static Wire.TransactionReferences ToProto(Contract.ProviderTransactionReferences references)
    {
        Required(references, "references");
        var wire = new Wire.TransactionReferences { ClientReference = references.ClientReference ?? string.Empty };
        if (references.MerchantReference is not null)
        {
            wire.MerchantReference = references.MerchantReference;
        }

        if (references.Stan is not null)
        {
            wire.Stan = references.Stan;
        }

        if (references.Rrn is not null)
        {
            wire.Rrn = references.Rrn;
        }

        if (references.ProviderReference is not null)
        {
            wire.ProviderReference = references.ProviderReference;
        }

        if (references.ProviderStan is not null)
        {
            wire.ProviderStan = references.ProviderStan;
        }

        if (references.ProviderRrn is not null)
        {
            wire.ProviderRrn = references.ProviderRrn;
        }

        foreach (var (type, value) in references.ExternalReferences ?? new Dictionary<string, string>())
        {
            wire.ExternalReferences.Add(new Wire.ExternalReference { Type = type, Value = value ?? string.Empty });
        }

        return wire;
    }

    public static Contract.ProviderTransactionReferences FromProto(Wire.TransactionReferences? references)
    {
        var value = Required(references, "references");
        var external = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in value.ExternalReferences)
        {
            if (!external.TryAdd(RequiredText(reference.Type, "references.externalReferences.type"), reference.Value))
            {
                throw new ProtoMappingException($"references.externalReferences has a duplicate type '{reference.Type}'.");
            }
        }

        return new Contract.ProviderTransactionReferences(
            value.ClientReference,
            value.HasMerchantReference ? value.MerchantReference : null,
            value.HasStan ? value.Stan : null,
            value.HasRrn ? value.Rrn : null,
            value.HasProviderReference ? value.ProviderReference : null,
            value.HasProviderStan ? value.ProviderStan : null,
            value.HasProviderRrn ? value.ProviderRrn : null,
            external);
    }

    public static Wire.CorrelationContext ToProto(Contract.CorrelationContext correlation)
    {
        Required(correlation, "correlation");
        return new Wire.CorrelationContext
        {
            RansysTransactionId = FormatGuid(correlation.RansysTransactionId, "correlation.ransysTransactionId"),
            CorrelationId = correlation.CorrelationId ?? string.Empty,
            TraceId = correlation.TraceId ?? string.Empty,
        };
    }

    public static Contract.CorrelationContext FromProto(Wire.CorrelationContext? correlation)
    {
        var value = Required(correlation, "correlation");
        return new Contract.CorrelationContext(
            ParseGuid(value.RansysTransactionId, "correlation.ransysTransactionId"),
            value.CorrelationId,
            value.TraceId);
    }

    public static Wire.ProviderExecutionContext ToProto(Contract.ProviderExecutionContext context)
    {
        Required(context, "executionContext");
        var timeout = Required(context.TimeoutPolicy, "executionContext.timeoutPolicy");
        var concurrency = Required(context.ConcurrencyPolicy, "executionContext.concurrencyPolicy");

        var wire = new Wire.ProviderExecutionContext
        {
            Provider = ToProto(context.Provider),
            TimeoutPolicy = new Wire.TimeoutPolicy
            {
                ConnectTimeout = ToDuration(timeout.ConnectTimeout),
                ReadTimeout = ToDuration(timeout.ReadTimeout),
                MaxRetry = timeout.MaxRetry,
                RetryDelay = ToDuration(timeout.RetryDelay),
                RetryBackoff = timeout.RetryBackoff ?? string.Empty,
                StatusCheckAfterTimeout = timeout.StatusCheckAfterTimeout,
                ReversalAfterTimeout = timeout.ReversalAfterTimeout,
            },
            ConcurrencyPolicy = new Wire.ConcurrencyPolicy(),
            PolicyVersion = context.PolicyVersion,
        };

        if (concurrency.MaxConcurrentRequests is { } maxConcurrent)
        {
            wire.ConcurrencyPolicy.MaxConcurrentRequests = maxConcurrent;
        }

        if (concurrency.MaxQueueDepth is { } maxQueue)
        {
            wire.ConcurrencyPolicy.MaxQueueDepth = maxQueue;
        }

        if (concurrency.RateLimitPerSecond is { } rateLimit)
        {
            wire.ConcurrencyPolicy.RateLimitPerSecond = rateLimit;
        }

        if (context.EndpointProfileId is { } endpointProfile)
        {
            wire.EndpointProfileId = FormatGuid(endpointProfile, "executionContext.endpointProfileId");
        }

        if (context.ProductMappingVersion is { } mappingVersion)
        {
            wire.ProductMappingVersion = mappingVersion;
        }

        if (context.AuthenticationProfileReference is not null)
        {
            wire.AuthenticationProfileReference = context.AuthenticationProfileReference;
        }

        wire.Capabilities.AddRange(Required(context.Capabilities, "executionContext.capabilities"));
        return wire;
    }

    public static Contract.ProviderExecutionContext FromProto(Wire.ProviderExecutionContext? context)
    {
        var value = Required(context, "executionContext");
        var timeout = Required(value.TimeoutPolicy, "executionContext.timeoutPolicy");
        var concurrency = Required(value.ConcurrencyPolicy, "executionContext.concurrencyPolicy");

        return new Contract.ProviderExecutionContext(
            FromProto(value.Provider),
            ParseOptionalGuid(value.HasEndpointProfileId, value.EndpointProfileId, "executionContext.endpointProfileId"),
            value.HasProductMappingVersion ? value.ProductMappingVersion : null,
            new Contract.ProviderTimeoutPolicy(
                FromDuration(timeout.ConnectTimeout, "timeoutPolicy.connectTimeout"),
                FromDuration(timeout.ReadTimeout, "timeoutPolicy.readTimeout"),
                timeout.MaxRetry,
                FromDuration(timeout.RetryDelay, "timeoutPolicy.retryDelay"),
                timeout.RetryBackoff,
                timeout.StatusCheckAfterTimeout,
                timeout.ReversalAfterTimeout),
            new Contract.ProviderConcurrencyPolicy(
                concurrency.HasMaxConcurrentRequests ? concurrency.MaxConcurrentRequests : null,
                concurrency.HasMaxQueueDepth ? concurrency.MaxQueueDepth : null,
                concurrency.HasRateLimitPerSecond ? concurrency.RateLimitPerSecond : null),
            new HashSet<string>(value.Capabilities, StringComparer.Ordinal),
            value.HasAuthenticationProfileReference ? value.AuthenticationProfileReference : null,
            value.PolicyVersion);
    }

    // ---- result ---------------------------------------------------------------------------------------------

    public static Wire.ProviderResult ToProto(Contract.ProviderResult result)
    {
        Required(result, "result");
        var wire = new Wire.ProviderResult
        {
            Outcome = ToProto(result.Outcome),
            Finality = ToProto(result.Finality),
            Transport = ToProto(Required(result.Transport, "result.transport")),
            RansysResponseCode = result.RansysResponseCode ?? string.Empty,
            References = ToProto(result.References),
            Data = ToStruct(result.Data, "result.data"),
            ReceivedAt = ToTimestamp(result.ReceivedAt),
        };

        if (result.ProviderResponseCode is not null)
        {
            wire.ProviderResponseCode = result.ProviderResponseCode;
        }

        if (result.ProviderResponseMessage is not null)
        {
            wire.ProviderResponseMessage = result.ProviderResponseMessage;
        }

        if (result.RetryHint is { } hint)
        {
            wire.RetryHint = new Wire.ProviderRetryHint
            {
                SafeToRetryTransport = hint.SafeToRetryTransport,
                SuggestedDelay = ToOptionalDuration(hint.SuggestedDelay),
            };
        }

        if (result.RawRequestReference is not null)
        {
            wire.RawRequestReference = result.RawRequestReference;
        }

        if (result.RawResponseReference is not null)
        {
            wire.RawResponseReference = result.RawResponseReference;
        }

        return wire;
    }

    public static Contract.ProviderResult FromProto(Wire.ProviderResult? result)
    {
        var value = Required(result, "result");
        return new Contract.ProviderResult(
            FromProto(value.Outcome),
            FromProto(value.Finality),
            FromProto(value.Transport),
            value.RansysResponseCode,
            value.HasProviderResponseCode ? value.ProviderResponseCode : null,
            value.HasProviderResponseMessage ? value.ProviderResponseMessage : null,
            FromProto(value.References),
            FromStruct(value.Data, "result.data"),
            value.RetryHint is null
                ? null
                : new Contract.ProviderRetryHint(
                    value.RetryHint.SafeToRetryTransport,
                    FromOptionalDuration(value.RetryHint.SuggestedDelay, "result.retryHint.suggestedDelay")),
            FromTimestamp(value.ReceivedAt, "result.receivedAt"),
            value.HasRawRequestReference ? value.RawRequestReference : null,
            value.HasRawResponseReference ? value.RawResponseReference : null);
    }

    public static Wire.ProviderTransportResult ToProto(Contract.ProviderTransportResult transport)
    {
        Required(transport, "transport");
        var wire = new Wire.ProviderTransportResult
        {
            Status = ToProto(transport.Status),
            RequestSent = transport.RequestSent,
            ConnectDuration = ToOptionalDuration(transport.ConnectDuration),
            RoundTripDuration = ToOptionalDuration(transport.RoundTripDuration),
        };

        if (transport.Error is { } error)
        {
            wire.Error = new Wire.ProviderError
            {
                Category = error.Category ?? string.Empty,
                Code = error.Code ?? string.Empty,
                Message = error.Message ?? string.Empty,
                RetryableTransportError = error.RetryableTransportError,
            };

            if (error.RawCode is not null)
            {
                wire.Error.RawCode = error.RawCode;
            }
        }

        return wire;
    }

    public static Contract.ProviderTransportResult FromProto(Wire.ProviderTransportResult? transport)
    {
        var value = Required(transport, "transport");
        return new Contract.ProviderTransportResult(
            FromProto(value.Status),
            value.RequestSent,
            FromOptionalDuration(value.ConnectDuration, "transport.connectDuration"),
            FromOptionalDuration(value.RoundTripDuration, "transport.roundTripDuration"),
            value.Error is null
                ? null
                : new Contract.ProviderError(
                    value.Error.Category,
                    value.Error.Code,
                    value.Error.Message,
                    value.Error.RetryableTransportError,
                    value.Error.HasRawCode ? value.Error.RawCode : null));
    }

    // ---- callback -------------------------------------------------------------------------------------------

    public static Wire.ProviderCallback ToProto(Contract.ProviderCallback callback)
    {
        Required(callback, "callback");
        var wire = new Wire.ProviderCallback
        {
            ProviderId = FormatGuid(callback.ProviderId, "callback.providerId"),
            OriginalRansysTransactionId = FormatGuid(callback.OriginalRansysTransactionId, "callback.originalRansysTransactionId"),
            Result = ToProto(callback.Result),
            Correlation = ToProto(callback.Correlation),
            ReceivedAt = ToTimestamp(callback.ReceivedAt),
        };

        if (callback.CallbackId is not null)
        {
            wire.CallbackId = callback.CallbackId;
        }

        if (callback.ProviderReference is not null)
        {
            wire.ProviderReference = callback.ProviderReference;
        }

        return wire;
    }

    public static Contract.ProviderCallback FromProto(Wire.ProviderCallback? callback)
    {
        var value = Required(callback, "callback");
        return new Contract.ProviderCallback(
            ParseGuid(value.ProviderId, "callback.providerId"),
            value.HasCallbackId ? value.CallbackId : null,
            ParseGuid(value.OriginalRansysTransactionId, "callback.originalRansysTransactionId"),
            value.HasProviderReference ? value.ProviderReference : null,
            FromProto(value.Result),
            FromProto(value.Correlation),
            FromTimestamp(value.ReceivedAt, "callback.receivedAt"));
    }

    public static Wire.ProviderCallbackAck ToProto(Contract.ProviderCallbackAck ack)
    {
        Required(ack, "ack");
        var wire = new Wire.ProviderCallbackAck { Accepted = ack.Accepted };
        if (ack.AcknowledgementCode is not null)
        {
            wire.AcknowledgementCode = ack.AcknowledgementCode;
        }

        return wire;
    }

    public static Contract.ProviderCallbackAck FromProto(Wire.ProviderCallbackAck? ack)
    {
        var value = Required(ack, "ack");
        return new Contract.ProviderCallbackAck(value.Accepted, value.HasAcknowledgementCode ? value.AcknowledgementCode : null);
    }

    // ---- enums ----------------------------------------------------------------------------------------------

    public static Wire.ProviderOutcome ToProto(Contract.ProviderOutcome outcome) => outcome switch
    {
        Contract.ProviderOutcome.Success => Wire.ProviderOutcome.Success,
        Contract.ProviderOutcome.Failed => Wire.ProviderOutcome.Failed,
        Contract.ProviderOutcome.Pending => Wire.ProviderOutcome.Pending,
        Contract.ProviderOutcome.InDoubt => Wire.ProviderOutcome.InDoubt,
        Contract.ProviderOutcome.NotSent => Wire.ProviderOutcome.NotSent,
        _ => throw new ProtoMappingException($"Outcome {outcome} is not defined."),
    };

    public static Contract.ProviderOutcome FromProto(Wire.ProviderOutcome outcome) => outcome switch
    {
        Wire.ProviderOutcome.Success => Contract.ProviderOutcome.Success,
        Wire.ProviderOutcome.Failed => Contract.ProviderOutcome.Failed,
        Wire.ProviderOutcome.Pending => Contract.ProviderOutcome.Pending,
        Wire.ProviderOutcome.InDoubt => Contract.ProviderOutcome.InDoubt,
        Wire.ProviderOutcome.NotSent => Contract.ProviderOutcome.NotSent,
        _ => throw new ProtoMappingException($"result.outcome {outcome} is unspecified or unknown."),
    };

    public static Wire.ResultFinality ToProto(Contract.ResultFinality finality) => finality switch
    {
        Contract.ResultFinality.Definitive => Wire.ResultFinality.Definitive,
        Contract.ResultFinality.NonFinal => Wire.ResultFinality.NonFinal,
        Contract.ResultFinality.Ambiguous => Wire.ResultFinality.Ambiguous,
        Contract.ResultFinality.NotApplicable => Wire.ResultFinality.NotApplicable,
        _ => throw new ProtoMappingException($"Finality {finality} is not defined."),
    };

    public static Contract.ResultFinality FromProto(Wire.ResultFinality finality) => finality switch
    {
        Wire.ResultFinality.Definitive => Contract.ResultFinality.Definitive,
        Wire.ResultFinality.NonFinal => Contract.ResultFinality.NonFinal,
        Wire.ResultFinality.Ambiguous => Contract.ResultFinality.Ambiguous,
        Wire.ResultFinality.NotApplicable => Contract.ResultFinality.NotApplicable,
        _ => throw new ProtoMappingException($"result.finality {finality} is unspecified or unknown."),
    };

    public static Wire.TransportStatus ToProto(Contract.TransportStatus status) => status switch
    {
        Contract.TransportStatus.NotSent => Wire.TransportStatus.NotSentTransport,
        Contract.TransportStatus.Sent => Wire.TransportStatus.Sent,
        Contract.TransportStatus.Response => Wire.TransportStatus.Response,
        Contract.TransportStatus.Timeout => Wire.TransportStatus.Timeout,
        Contract.TransportStatus.ConnectionError => Wire.TransportStatus.ConnectionError,
        Contract.TransportStatus.ProtocolError => Wire.TransportStatus.ProtocolError,
        _ => throw new ProtoMappingException($"Transport status {status} is not defined."),
    };

    public static Contract.TransportStatus FromProto(Wire.TransportStatus status) => status switch
    {
        Wire.TransportStatus.NotSentTransport => Contract.TransportStatus.NotSent,
        Wire.TransportStatus.Sent => Contract.TransportStatus.Sent,
        Wire.TransportStatus.Response => Contract.TransportStatus.Response,
        Wire.TransportStatus.Timeout => Contract.TransportStatus.Timeout,
        Wire.TransportStatus.ConnectionError => Contract.TransportStatus.ConnectionError,
        Wire.TransportStatus.ProtocolError => Contract.TransportStatus.ProtocolError,
        _ => throw new ProtoMappingException($"transport.status {status} is unspecified or unknown."),
    };

    // ---- typed proto messages <-> C# dictionaries -----------------------------------------------------------

    private static Wire.Money ToMoney(decimal amount, string? currency, int version, string field) => new()
    {
        Value = FormatMoney(amount),
        Currency = RequiredText(currency, $"{field}.currency"),
        CurrencyScale = 0,
        CurrencyDefinitionVersion = version > 0
            ? version
            : throw new ProtoMappingException($"{field}.currencyDefinitionVersion must be positive."),
    };

    private static (decimal Amount, string Currency, int Version) FromMoney(Wire.Money? money, string field)
    {
        var value = Required(money, field);
        var amount = ParseMoney(value.Value, $"{field}.value");
        var currency = RequiredText(value.Currency, $"{field}.currency");
        if (value.CurrencyDefinitionVersion <= 0)
        {
            throw new ProtoMappingException($"{field}.currencyDefinitionVersion must be positive.");
        }

        return (amount, currency, value.CurrencyDefinitionVersion);
    }

    private static Wire.Customer? ToCustomer(IReadOnlyDictionary<string, JsonElement>? customer)
    {
        if (customer is null || customer.Count == 0)
        {
            return null;
        }

        var wire = new Wire.Customer();
        foreach (var (key, element) in customer)
        {
            var field = $"request.customer.{key}";
            switch (key)
            {
                case CustomerId:
                    wire.CustomerId = JsonText(element, field);
                    break;
                case ExternalCustomerReference:
                    wire.ExternalCustomerReference = JsonText(element, field);
                    break;
                case AccountNumber:
                    wire.AccountNumber = JsonText(element, field);
                    break;
                case PhoneNumber:
                    wire.PhoneNumber = JsonText(element, field);
                    break;
                case Name:
                    wire.Name = JsonText(element, field);
                    break;
                case MetadataKey:
                    wire.Metadata = ObjectToStruct(element, field);
                    break;
                default:
                    throw new ProtoMappingException($"{field} is not a customer field of the contract.");
            }
        }

        return wire;
    }

    private static IReadOnlyDictionary<string, JsonElement> FromCustomer(Wire.Customer? customer)
    {
        if (customer is null)
        {
            return EmptyJson;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (customer.HasCustomerId)
        {
            result.Add(CustomerId, JsonString(customer.CustomerId));
        }

        if (customer.HasExternalCustomerReference)
        {
            result.Add(ExternalCustomerReference, JsonString(customer.ExternalCustomerReference));
        }

        if (customer.HasAccountNumber)
        {
            result.Add(AccountNumber, JsonString(customer.AccountNumber));
        }

        if (customer.HasPhoneNumber)
        {
            result.Add(PhoneNumber, JsonString(customer.PhoneNumber));
        }

        if (customer.HasName)
        {
            result.Add(Name, JsonString(customer.Name));
        }

        if (customer.Metadata is not null)
        {
            result.Add(MetadataKey, StructToObject(customer.Metadata, "request.customer.metadata"));
        }

        return result;
    }

    private static Wire.TransactionEndpoint? ToEndpoint(IReadOnlyDictionary<string, JsonElement>? endpoint, string field)
    {
        if (endpoint is null || endpoint.Count == 0)
        {
            return null;
        }

        var wire = new Wire.TransactionEndpoint();
        foreach (var (key, element) in endpoint)
        {
            var name = $"{field}.{key}";
            switch (key)
            {
                case EndpointTypeKey:
                    wire.Type = EndpointTypes.TryGetValue(JsonText(element, name), out var type)
                        ? type
                        : throw new ProtoMappingException($"{name} is not an endpoint type of the contract.");
                    break;
                case Identifier:
                    wire.Identifier = JsonText(element, name);
                    break;
                case InstitutionCode:
                    wire.InstitutionCode = JsonText(element, name);
                    break;
                case AccountReference:
                    wire.AccountReference = JsonText(element, name);
                    break;
                case MetadataKey:
                    wire.Metadata = ObjectToStruct(element, name);
                    break;
                default:
                    throw new ProtoMappingException($"{name} is not an endpoint field of the contract.");
            }
        }

        if (wire.Type == Wire.EndpointType.Unspecified)
        {
            throw new ProtoMappingException($"{field}.type is required.");
        }

        RequiredText(wire.Identifier, $"{field}.identifier");
        return wire;
    }

    private static IReadOnlyDictionary<string, JsonElement> FromEndpoint(Wire.TransactionEndpoint? endpoint, string field)
    {
        if (endpoint is null)
        {
            return EmptyJson;
        }

        var type = EndpointTypes.FirstOrDefault(pair => pair.Value == endpoint.Type).Key
            ?? throw new ProtoMappingException($"{field}.type is unspecified or unknown.");

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [EndpointTypeKey] = JsonString(type),
            [Identifier] = JsonString(RequiredText(endpoint.Identifier, $"{field}.identifier")),
        };

        if (endpoint.HasInstitutionCode)
        {
            result.Add(InstitutionCode, JsonString(endpoint.InstitutionCode));
        }

        if (endpoint.HasAccountReference)
        {
            result.Add(AccountReference, JsonString(endpoint.AccountReference));
        }

        if (endpoint.Metadata is not null)
        {
            result.Add(MetadataKey, StructToObject(endpoint.Metadata, $"{field}.metadata"));
        }

        return result;
    }
}
