namespace LockService;

// Shared with the administrator recovery script. Prevent two writers/enforcers for one state directory.
public sealed class StateDirectoryLease : IDisposable
{
    private readonly FileStream lease;

    public StateDirectoryLease(ServiceSettings settings)
    {
        Directory.CreateDirectory(settings.StateDirectory);
        lease = new FileStream(Path.Combine(settings.StateDirectory, "service.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => lease.Dispose();
}
