// RANSYS background worker host.
// Hosted services (outbox worker, idempotency expiry sweep) are registered in later milestones.

var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();
host.Run();
