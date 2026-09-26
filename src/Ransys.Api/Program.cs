// RANSYS API host (composition root): OpenAPI v1 merchant endpoints (M12e).

using Microsoft.AspNetCore.Diagnostics;
using Ransys.Api.Composition;
using Ransys.Api.Contracts.V1;
using Ransys.Api.Endpoints;
using Ransys.Api.Errors;
using Ransys.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// Fail closed: the API never runs without the Transaction Database (set locally as a user secret, UserSecretsId
// ransys-api-dev; never commit it).
var connectionString = builder.Configuration.GetConnectionString("TransactionDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'TransactionDb' is required.");
}

builder.Services.ConfigureHttpJsonOptions(options => ApiJson.Apply(options.SerializerOptions));
builder.Services.AddRansysTransactionProcessing(connectionString, builder.Configuration);
builder.Services.AddRansysRequestAuthentication(builder.Configuration, builder.Environment);
var rateLimiting = builder.Services.AddRansysRateLimiting(builder.Configuration);

var app = builder.Build();

var auth = app.Services.GetRequiredService<ApiAuthOptions>();
if (auth.AllowDevelopmentAuthentication)
{
    app.Logger.LogWarning(
        "DEVELOPMENT request authentication is enabled (environment {Environment}); signature verification skipped: {Skip}. Never use in production (ADR-022).",
        app.Environment.EnvironmentName, auth.SkipSignatureVerification);
}

// Unexpected exceptions never leak details. Nothing unsafe was sent (Transaction Core commits before any provider call
// and records outcomes itself), so the client may retry with the same clientReference.
app.UseExceptionHandler(errors => errors.Run(context =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogError(context.Features.Get<IExceptionHandlerFeature>()?.Error, "Unhandled exception for {Path}.", context.Request.Path.Value);
    return ApiError.DependencyUnavailable().WriteAsync(context);
}));

app.UseMiddleware<RequestAuthenticationMiddleware>();
if (rateLimiting)
{
    app.UseRateLimiter();
}

app.MapGet("/health", () => Results.Ok(new { status = "UP" }));
app.MapTransactionEndpoints();

app.Run();

/// <summary>Entry point; public so tests can host the API with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
