namespace Shared;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LockRequest(int DurationMinutes);
public sealed record LockStatus(bool Locked, DateTimeOffset? LockUntilUtc, long RemainingSeconds, string MachineName)
{
    public bool EnforcementEnabled { get; init; }
    public bool DryRun { get; init; } = true;
    public bool TargetAccountConfigured { get; init; }
    public string? TargetUsername { get; init; }
    public bool RecoveryRequired { get; init; }
    public string? EnforcementMessage { get; init; }
}
public sealed record ApiResult(bool Success, string Message);
