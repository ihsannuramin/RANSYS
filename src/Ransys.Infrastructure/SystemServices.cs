using Ransys.Application;

namespace Ransys.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Time-ordered UUIDv7 identifiers generated in the application (ERD v1.1 §5).</summary>
public sealed class UuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}
