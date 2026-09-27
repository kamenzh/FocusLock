namespace Shared;

public sealed record LockRequest(int DurationMinutes);
public sealed record LockStatus(bool Locked, DateTimeOffset? LockUntilUtc, long RemainingSeconds, string MachineName);
public sealed record ApiResult(bool Success, string Message);
