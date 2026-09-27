namespace LockService;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public sealed record LockState(int SchemaVersion, bool Locked, DateTimeOffset? LockUntilUtc)
{
    public static LockState Unlocked => new(1, false, null);
}

public interface ILockStateStore
{
    Task<LockState> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(LockState state, CancellationToken cancellationToken);
}
