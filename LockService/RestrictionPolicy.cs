namespace LockService;

// Extension points only. Daily allowance and schedules are not evaluated in this phase.
public enum RestrictionKind { ManualLock, DailyAllowance, ScheduledRestriction }
public sealed record RestrictionDecision(bool Restricted, DateTimeOffset? UntilUtc, RestrictionKind? Source);
public interface IRestrictionPolicy
{
    RestrictionDecision Evaluate(LockState manualState, DateTimeOffset utcNow);
}

public sealed class ManualLockPolicy : IRestrictionPolicy
{
    public RestrictionDecision Evaluate(LockState manualState, DateTimeOffset utcNow) =>
        manualState.Locked && manualState.LockUntilUtc > utcNow
            ? new(true, manualState.LockUntilUtc, RestrictionKind.ManualLock)
            : new(false, null, null);
}
