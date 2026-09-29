using System.Text.Json;
using LockService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LockService.Tests;

public sealed class EnforcementStateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FocusLockEnforcementTests", Guid.NewGuid().ToString("N"));
    private JsonLockStateStore Store() => new(new ServiceSettings { StateDirectory = directory }, NullLogger<JsonLockStateStore>.Instance);
    private static LockState OwnedState => new(2, true, new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero))
    {
        TargetUsername = "FocusLockTest", TargetSid = "S-1-5-21-1-2-3-1001", AccountDisabledByFocusLock = true
    };

    [Theory]
    [InlineData("owned")]
    [InlineData("pending")]
    [InlineData("simulated")]
    public async Task EnforcementMetadataSurvivesRealJsonRoundTripAndOverwrite(string kind)
    {
        var state = kind switch
        {
            "pending" => OwnedState with { AccountDisabledByFocusLock = false, DisablePending = true },
            "simulated" => OwnedState with { AccountDisabledByFocusLock = false, SimulatedEnforcement = true },
            _ => OwnedState
        };
        await Store().SaveAsync(LockState.Unlocked, default);
        await Store().SaveAsync(state, default);
        Assert.Equal(state, await Store().LoadAsync(default));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("missingMetadata")]
    [InlineData("twoStages")]
    [InlineData("noStage")]
    [InlineData("legacyOwnership")]
    [InlineData("builtinSid")]
    [InlineData("unlockedOwned")]
    [InlineData("wrongOffset")]
    public async Task InconsistentEnforcementStateIsCorrupt(string kind)
    {
        var state = kind switch
        {
            "missingMetadata" => new LockState(2, true, OwnedState.LockUntilUtc),
            "twoStages" => OwnedState with { DisablePending = true },
            "noStage" => OwnedState with { AccountDisabledByFocusLock = false },
            "legacyOwnership" => OwnedState with { SchemaVersion = 1 },
            "builtinSid" => OwnedState with { TargetSid = "S-1-5-18" },
            "unlockedOwned" => OwnedState with { Locked = false, LockUntilUtc = null },
            _ => OwnedState with { LockUntilUtc = OwnedState.LockUntilUtc!.Value.ToOffset(TimeSpan.FromHours(2)) }
        };
        await Store().SaveAsync(state, default);
        var loaded = await Store().LoadAsync(default);
        Assert.True(loaded.IsCorrupt);
        Assert.False(loaded.AccountDisabledByFocusLock);
        Assert.False(loaded.Locked);
    }

    [Fact]
    public async Task CorruptInputIsNotRewrittenOnLoad()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(path, "corrupt");
        Assert.True((await Store().LoadAsync(default)).IsCorrupt);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RecoveryMarkerSurvivesTimerOnlyState()
    {
        var state = LockState.Unlocked with { RecoveryRequired = true };
        await Store().SaveAsync(state, default);
        Assert.Equal(state, await Store().LoadAsync(default));
    }

    [Fact]
    public void HttpRequestCannotSpecifyUsername()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Shared.LockRequest>(
            "{\"durationMinutes\":1,\"username\":\"SomebodyElse\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void ConcurrentServiceOrRecoveryCannotAcquireSameStateDirectory()
    {
        var settings = new ServiceSettings { StateDirectory = directory };
        using (var lease = new StateDirectoryLease(settings))
            Assert.Throws<IOException>(() => new StateDirectoryLease(settings));
        using var next = new StateDirectoryLease(settings);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WindowsImplementationRejectsMutationsWhenDisabledOrDryRun(bool enabled, bool dryRun)
    {
        if (!OperatingSystem.IsWindows()) return;
        IAccountManager accounts = new WindowsAccountManager(new() { Enabled = enabled, DryRun = dryRun, TargetUsername = "FocusLockTest" });
        Assert.Throws<EnforcementException>(() => accounts.DisableTarget("S-1-5-21-1-2-3-1001"));
        Assert.Throws<EnforcementException>(() => accounts.EnableTarget("S-1-5-21-1-2-3-1001"));
        Assert.Throws<EnforcementException>(() => accounts.LogOffTargetSession(1, "S-1-5-21-1-2-3-1001"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
