// RANSYS background worker host.

using Npgsql;
using Ransys.Application;
using Ransys.Infrastructure;
using Ransys.Persistence.PostgreSql.Idempotency;
using Ransys.TransactionCore.Idempotency;
using Ransys.Workers;

var builder = Host.CreateApplicationBuilder(args);

// Fail closed: workers never run without the Transaction Database.
var connectionString = builder.Configuration.GetConnectionString("TransactionDb")
    ?? throw new InvalidOperationException("Connection string 'TransactionDb' is required.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();
builder.Services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
builder.Services.AddSingleton<IdempotencyService>();

builder.Services.Configure<IdempotencyExpiryOptions>(builder.Configuration.GetSection(IdempotencyExpiryOptions.Section));
builder.Services.AddHostedService<IdempotencyExpiryWorker>();

var host = builder.Build();
host.Run();
