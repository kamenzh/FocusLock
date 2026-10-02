using System.Text.Json;

namespace LockService.Security;

public enum SecurityEvent { LockAccepted, UnlockAccepted, AuthenticationRejected, ReplayRejected, AccountDisabled, AccountEnabled, TimerExpired }
public interface ISecurityAudit { void Record(SecurityEvent kind, string? reasonCode = null); }
public sealed class NullSecurityAudit : ISecurityAudit { public void Record(SecurityEvent kind, string? reasonCode = null) { } }

public sealed class FileSecurityAudit(IClock clock, ServiceSettings settings, ILogger<FileSecurityAudit> logger) : ISecurityAudit
{
    private readonly object gate = new();
    private readonly string directory = Path.Combine(settings.StateDirectory, "audit");

    public void Record(SecurityEvent kind, string? reasonCode = null)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "security-audit.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 5 * 1024 * 1024)
                    File.Move(path, Path.Combine(directory, "security-audit.previous.jsonl"), overwrite: true);
                var entry = new { timestampUtc = clock.UtcNow, @event = kind.ToString(), reasonCode };
                File.AppendAllText(path, JsonSerializer.Serialize(entry) + "\n");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Audit failure must never prevent an already-disabled account from being restored.
                logger.LogError("Security audit write failed ({ErrorType}); event {Event}. Check audit directory permissions", exception.GetType().Name, kind);
            }
        }
    }
}
