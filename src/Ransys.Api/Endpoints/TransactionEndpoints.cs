using System.Text.Json;
using Ransys.Api.Contracts.V1;
using Ransys.Api.Errors;
using Ransys.Api.Mapping;
using Ransys.Api.Security;
using Ransys.Api.Validation;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.TransactionCore.Processing;

namespace Ransys.Api.Endpoints;

/// <summary>
/// OpenAPI v1 merchant endpoints (minimal APIs under <c>/api/v1</c>). Each POST: authenticated client (middleware) →
/// <c>Idempotency-Key</c> header → strict JSON → <see cref="RequestValidator"/> → command →
/// <see cref="TransactionProcessingService"/> → <see cref="ResponseMapper"/>. HTTP 200 means the request entered normal
/// processing, whatever the business outcome; errors follow ADR-021.
/// </summary>
public static class TransactionEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var v1 = routes.MapGroup("/api/v1");

        v1.MapPost("/inquiries", (HttpContext context, TransactionProcessingService service) =>
            Process<InquiryRequest, InquiryCommand>(context, RequestValidator.ToCommand, service.InquireAsync));
        v1.MapPost("/payments", (HttpContext context, TransactionProcessingService service) =>
            Process<PaymentRequest, PaymentCommand>(context, RequestValidator.ToCommand, service.PayAsync));
        v1.MapPost("/transfers", (HttpContext context, TransactionProcessingService service) =>
            Process<TransferRequest, TransferCommand>(context, RequestValidator.ToCommand, service.TransferAsync));
        v1.MapPost("/refunds", (HttpContext context, TransactionProcessingService service) =>
            Process<RefundRequest, RefundCommand>(context, RequestValidator.ToCommand, service.RefundAsync));
        v1.MapPost("/reversals", (HttpContext context, TransactionProcessingService service) =>
            Process<ReversalRequest, ReversalCommand>(context, RequestValidator.ToCommand, service.ReverseAsync));
        v1.MapPost("/voids", (HttpContext context, TransactionProcessingService service) =>
            Process<VoidRequest, VoidCommand>(context, RequestValidator.ToCommand, service.VoidAsync));
        v1.MapGet("/transactions/{ransysTransactionId}", GetTransaction);
        return routes;
    }

    private static async Task<IResult> GetTransaction(HttpContext context, string ransysTransactionId, TransactionProcessingService service)
    {
        var client = RequestAuthenticationMiddleware.GetClient(context);
        var id = RequestValidator.TransactionId(ransysTransactionId);
        if (!id.IsValid)
        {
            return id.Error!.ToResult(context);
        }

        if (id.Value == Guid.Empty)
        {
            return ApiError.NotFound().ToResult(context);
        }

        try
        {
            var view = await service.GetTransactionAsync(client.ChannelId, new TransactionId(id.Value), context.RequestAborted);
            return view is null
                ? ApiError.NotFound().ToResult(context)
                : Results.Json(ResponseMapper.ToResponse(view), ApiJson.Options);
        }
        catch (FinancialDependencyUnavailableException)
        {
            return ApiError.DependencyUnavailable().ToResult(context);
        }
    }

    private static async Task<IResult> Process<TRequest, TCommand>(
        HttpContext context,
        Func<TRequest?, ClientContext, string?, Validated<TCommand>> validate,
        Func<TCommand, CancellationToken, Task<Result<TransactionProcessingResult>>> execute)
        where TRequest : class
    {
        var client = RequestAuthenticationMiddleware.GetClient(context);

        var idempotencyKey = RequestValidator.IdempotencyKey(Single(context.Request.Headers[IdempotencyKeyHeader], out var repeated));
        if (repeated)
        {
            return ApiError.Validation("Idempotency-Key must be sent at most once.", IdempotencyKeyHeader).ToResult(context);
        }

        if (!idempotencyKey.IsValid)
        {
            return idempotencyKey.Error!.ToResult(context);
        }

        if (!context.Request.HasJsonContentType())
        {
            return ApiError.Validation("Content-Type must be application/json.", null).ToResult(context);
        }

        var body = Deserialize<TRequest>(RequestAuthenticationMiddleware.GetBody(context));
        if (!body.IsValid)
        {
            return body.Error!.ToResult(context);
        }

        var command = validate(body.Value, client, idempotencyKey.Value);
        if (!command.IsValid)
        {
            return command.Error!.ToResult(context);
        }

        Result<TransactionProcessingResult> result;
        try
        {
            result = await execute(command.Value, context.RequestAborted);
        }
        catch (FinancialDependencyUnavailableException)
        {
            return ApiError.DependencyUnavailable().ToResult(context);
        }

        return result.IsSuccess
            ? Results.Json(ResponseMapper.ToResponse(result.Value), ApiJson.Options)
            : ApiErrorMapper.Map(result.Error).ToResult(context);
    }

    private static Validated<T?> Deserialize<T>(ReadOnlyMemory<byte> body)
        where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body.Span, ApiJson.Options);
            return value is null
                ? new Validated<T?>(null, ApiError.Validation("Request body must be a JSON object.", null))
                : new Validated<T?>(value, null);
        }
        catch (JsonException ex)
        {
            // The exception text may name internal types, so only the JSON path is reported.
            return new Validated<T?>(null, ApiError.Validation("Malformed JSON, wrong value type, or a property not defined by the schema.", FieldOf(ex.Path)));
        }
    }

    private static string? FieldOf(string? path) => path switch
    {
        null or "$" => null,
        _ when path.StartsWith("$.", StringComparison.Ordinal) => path[2..],
        _ => path,
    };

    private static string? Single(Microsoft.Extensions.Primitives.StringValues values, out bool repeated)
    {
        repeated = values.Count > 1;
        return values.Count == 1 ? values[0] : null;
    }
}
