// RANSYS API host (composition root).
// PLACEHOLDER – public endpoints are pending OpenAPI v1 (see main.md §20).
// Only a liveness endpoint is exposed in Phase 1.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "UP" }));

app.Run();
