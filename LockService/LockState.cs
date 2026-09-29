namespace LockService;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public sealed record LockState(int SchemaVersion, bool Locked, DateTimeOffset? LockUntilUtc)
{
    public static LockState Unlocked => new(1, false, null);
    public string? TargetUsername { get; init; }
    public string? TargetSid { get; init; }
    public bool AccountDisabledByFocusLock { get; init; }
    public bool DisablePending { get; init; }
    public bool SimulatedEnforcement { get; init; }
    public bool RecoveryRequired { get; init; }
    // Load-time signal only. A corrupt file is not itself rewritten by loading it.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCorrupt { get; init; }
}

public interface ILockStateStore
{
    Task<LockState> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(LockState state, CancellationToken cancellationToken);
}
