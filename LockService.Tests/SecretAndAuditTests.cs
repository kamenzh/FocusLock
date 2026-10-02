using System.Security.Cryptography;
using System.Text.Json;
using FocusLock.Security;
using LockService;
using LockService.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LockService.Tests;

public sealed class SecretAndAuditTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FocusLockSecurityTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingSecretFailsClosed()
    {
        await Assert.ThrowsAsync<SecretUnavailableException>(() => new DpapiSecretStore(Path.Combine(directory, "missing.secret.dpapi")).ReadAsync());
    }

    [Fact]
    public async Task DpapiRoundTripAndCorruptBlobHandling()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.secret.dpapi");
        var key = RandomNumberGenerator.GetBytes(32);
        var protectedBytes = ProtectedData.Protect(key, DpapiSecretStore.Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(path, protectedBytes);
        var loaded = await new DpapiSecretStore(path).ReadAsync();
        Assert.Equal(key, loaded);
        Assert.NotEqual(key, protectedBytes);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(loaded);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await Assert.ThrowsAsync<SecretUnavailableException>(() => new DpapiSecretStore(path).ReadAsync());
    }

    [Fact]
    public void AuditIsStructuredUsesInjectedClockAndContainsOnlyApprovedFields()
    {
        var clock = new SecurityTestClock();
        var log = new FileSecurityAudit(clock, new ServiceSettings { StateDirectory = directory }, NullLogger<FileSecurityAudit>.Instance);
        log.Record(SecurityEvent.AuthenticationRejected, "Signature");
        log.Record(SecurityEvent.ReplayRejected);
        var lines = File.ReadAllLines(Path.Combine(directory, "audit", "security-audit.jsonl"));
        Assert.Equal(2, lines.Length);
        using var entry = JsonDocument.Parse(lines[0]);
        Assert.Equal(clock.UtcNow, entry.RootElement.GetProperty("timestampUtc").GetDateTimeOffset());
        Assert.Equal("AuthenticationRejected", entry.RootElement.GetProperty("event").GetString());
        Assert.Equal(new[] { "timestampUtc", "event", "reasonCode" }, entry.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("X-FocusLock", string.Join("", lines));
    }

    [Fact]
    public void AuditRotatesAndKeepsOnePreviousFile()
    {
        var folder = Path.Combine(directory, "audit");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "security-audit.jsonl");
        File.WriteAllBytes(path, new byte[5 * 1024 * 1024]);
        var log = new FileSecurityAudit(new SecurityTestClock(), new ServiceSettings { StateDirectory = directory }, NullLogger<FileSecurityAudit>.Instance);
        log.Record(SecurityEvent.UnlockAccepted);
        Assert.True(File.Exists(Path.Combine(folder, "security-audit.previous.jsonl")));
        Assert.Single(File.ReadAllLines(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
