namespace Shared;

public static class LockConfirmation
{
    public static string Create(LockStatus status, int minutes)
    {
        if (!status.EnforcementEnabled)
            return $"Start a timer for {minutes} minutes?\n\nAccount enforcement is disabled. No account or session changes will occur.";
        if (status.DryRun)
            return $"DryRun: simulate locking {status.TargetUsername} for {minutes} minutes?\n\nSafety checks will run, but no account will be disabled, enabled, or signed out.";
        return $"Lock {status.TargetUsername} for {minutes} minutes?\n\nThe Windows account will be disabled and any active session for that account will be signed out. Unsaved work in that account may be lost.";
    }
}
