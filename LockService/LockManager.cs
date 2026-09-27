using Shared;

namespace LockService;

// Serialize disk and memory changes across HTTP requests and expiration.
public sealed class LockManager(IClock clock, ILockStateStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private LockState state = LockState.Unlocked;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            state = await store.LoadAsync(cancellationToken);
            await ExpireCoreAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<LockStatus> LockAsync(int minutes, CancellationToken cancellationToken = default)
    {
        if (minutes is < 1 or > 720)
            throw new ArgumentOutOfRangeException(nameof(minutes), "Duration must be between 1 and 720 minutes.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var next = new LockState(1, true, clock.UtcNow.AddMinutes(minutes));
            await store.SaveAsync(next, cancellationToken);
            state = next;
            return Status();
        }
        finally { gate.Release(); }
    }

    public async Task<LockStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return Status(); }
        finally { gate.Release(); }
    }

    public async Task ExpireAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await ExpireCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task ExpireCoreAsync(CancellationToken cancellationToken)
    {
        if (state.Locked && state.LockUntilUtc <= clock.UtcNow)
        {
            await store.SaveAsync(LockState.Unlocked, cancellationToken);
            state = LockState.Unlocked;
        }
    }

    private LockStatus Status()
    {
        var remaining = state.LockUntilUtc is { } until
            ? Math.Max(0, (long)Math.Ceiling((until - clock.UtcNow).TotalSeconds)) : 0;
        return new(remaining > 0, remaining > 0 ? state.LockUntilUtc : null, remaining, Environment.MachineName);
    }
}
