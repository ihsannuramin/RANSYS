// RANSYS background worker host.

using Microsoft.Extensions.Options;
using Npgsql;
using Ransys.Application;
using Ransys.Application.Outbox;
using Ransys.Infrastructure;
using Ransys.Persistence.PostgreSql;
using Ransys.Persistence.PostgreSql.Idempotency;
using Ransys.Persistence.PostgreSql.Outbox;
using Ransys.TransactionCore.Idempotency;
using Ransys.Workers;

var builder = Host.CreateApplicationBuilder(args);

// Fail closed: workers never run without the Transaction Database.
var connectionString = builder.Configuration.GetConnectionString("TransactionDb")
    ?? throw new InvalidOperationException("Connection string 'TransactionDb' is required.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IDatabaseSessionFactory, PostgresSessionFactory>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();

// Idempotency expiry sweep (ADR-009).
builder.Services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
builder.Services.AddSingleton<IdempotencyService>();
builder.Services.Configure<IdempotencyExpiryOptions>(builder.Configuration.GetSection(IdempotencyExpiryOptions.Section));
builder.Services.AddHostedService<IdempotencyExpiryWorker>();

// Transactional outbox delivery (brokerless; PLACEHOLDER log publisher until a consumer exists).
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.Section));
builder.Services.AddSingleton<IOutboxStore, PostgresOutboxStore>();
builder.Services.AddSingleton<IOutboxPublisher, LoggingOutboxPublisher>();
builder.Services.AddSingleton(sp => new OutboxProcessor(
    sp.GetRequiredService<IDatabaseSessionFactory>(),
    sp.GetRequiredService<IOutboxStore>(),
    sp.GetRequiredService<IOutboxPublisher>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<IOptions<OutboxOptions>>().Value,
    WorkerIdentity.Create()));
builder.Services.AddHostedService<OutboxWorker>();

var host = builder.Build();
host.Run();
