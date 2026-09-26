namespace Ransys.Workers;

internal static class WorkerIdentity
{
    /// <summary>DDL v1.1 <c>outbox_events.locked_by varchar(128)</c>.</summary>
    private const int MaxLength = 128;

    /// <summary>Unique per process start: machine, process id and a random suffix.</summary>
    public static string Create()
    {
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return id.Length <= MaxLength ? id : id[^MaxLength..];
    }
}
