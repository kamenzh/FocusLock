using System.ComponentModel;
using LockService;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shared;
using Xunit;

namespace LockService.Tests;

public sealed class EnforcementTests
{
    [Fact]
    public async Task DefaultsKeepPhaseOneAndNeverReadOrModifyAccounts()
    {
        var f = new Fixture();
        f.Settings.Enabled = false;
        f.Accounts.Target = null;
        await f.Manager.InitializeAsync();
        Assert.True((await f.Manager.LockAsync(1)).Locked);
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await f.Manager.ExpireAsync();
        Assert.Equal(0, f.Accounts.Reads);
        Assert.Empty(f.Accounts.Mutations);
        Assert.False(new AccountEnforcementSettings().Enabled);
        Assert.True(new AccountEnforcementSettings().DryRun);
        Assert.Empty(new AccountEnforcementSettings().TargetUsername);
    }

    [Fact]
    public async Task StandardTargetPersistsIntentThenVerifiesDisableBeforeLogoff()
    {
        var f = new Fixture();
        f.Accounts.BeforeDisable = () =>
        {
            Assert.True(f.Store.State.DisablePending);
            Assert.False(f.Store.State.AccountDisabledByFocusLock);
            Assert.Equal(Fixture.TargetSid, f.Store.State.TargetSid);
            Assert.Equal("FocusLockTest", f.Store.State.TargetUsername);
        };
        f.Accounts.BeforeLogoff = () => Assert.True(f.Store.State.AccountDisabledByFocusLock);
        var status = await f.Manager.LockAsync(30);
        Assert.True(status.Locked);
        Assert.False(f.Accounts.Target!.Enabled);
        Assert.True(f.Store.State.AccountDisabledByFocusLock);
        Assert.False(f.Store.State.DisablePending);
        Assert.Equal(new[] { "disable:" + Fixture.TargetSid, "logoff:3" }, f.Accounts.Mutations);
        Assert.Contains(f.Log.Messages, s => s.Contains("validation passed"));
        Assert.Contains(f.Log.Messages, s => s.Contains("Verified target"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("admin")]
    [InlineData("recovery")]
    [InlineData("service")]
    [InlineData("disabled")]
    [InlineData("domain")]
    [InlineData("builtin")]
    [InlineData("system")]
    [InlineData("localservice")]
    [InlineData("networkservice")]
    [InlineData("wrongname")]
    [InlineData("emptyconfig")]
    [InlineData("qualifiedconfig")]
    public async Task UnsafeTargetsAreRefusedBeforeIntentOrMutation(string scenario)
    {
        var f = new Fixture();
        switch (scenario)
        {
            case "missing": f.Accounts.Target = null; break;
            case "admin": f.Accounts.Target = f.Accounts.Target! with { IsAdministrator = true }; break;
            case "recovery": f.Settings.RecoveryAdminUsername = "focuslocktest"; break;
            case "service": f.Accounts.Target = f.Accounts.Target! with { IsServiceIdentity = true }; break;
            case "disabled": f.Accounts.Target = f.Accounts.Target! with { Enabled = false }; break;
            case "domain": f.Accounts.Target = f.Accounts.Target! with { IsLocal = false }; break;
            case "builtin": f.Accounts.Target = f.Accounts.Target! with { Sid = "S-1-5-21-1-2-3-500" }; break;
            case "system": f.Accounts.Target = f.Accounts.Target! with { Sid = "S-1-5-18" }; break;
            case "localservice": f.Accounts.Target = f.Accounts.Target! with { Sid = "S-1-5-19" }; break;
            case "networkservice": f.Accounts.Target = f.Accounts.Target! with { Sid = "S-1-5-20" }; break;
            case "wrongname": f.Accounts.Target = f.Accounts.Target! with { Username = "SomebodyElse" }; break;
            case "emptyconfig": f.Settings.TargetUsername = ""; break;
            case "qualifiedconfig": f.Settings.TargetUsername = "DOMAIN\\FocusLockTest"; break;
        }
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        Assert.Empty(f.Accounts.Mutations);
        Assert.Empty(f.Store.Saves);
        Assert.False((await f.Manager.GetStatusAsync()).Locked);
        Assert.Contains(f.Log.Messages, s => s.Contains("validation failed"));
    }

    [Fact]
    public async Task DryRunValidatesLogsTargetSessionsAndNeverMutatesEvenOnExpiration()
    {
        var f = new Fixture();
        f.Settings.DryRun = true;
        await f.Manager.LockAsync(1);
        Assert.True(f.Store.State.SimulatedEnforcement);
        Assert.False(f.Store.State.AccountDisabledByFocusLock);
        Assert.Contains(f.Log.Messages, s => s.Contains("WOULD disable"));
        Assert.Contains(f.Log.Messages, s => s.Contains("session 3"));
        Assert.DoesNotContain(f.Log.Messages, s => s.Contains("session 9"));
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await f.Manager.ExpireAsync();
        Assert.Empty(f.Accounts.Mutations);
        Assert.Equal(LockState.Unlocked, f.Store.State);
    }

    [Fact]
    public async Task DryRunStillRejectsAdministrator()
    {
        var f = new Fixture();
        f.Settings.DryRun = true;
        f.Accounts.Target = f.Accounts.Target! with { IsAdministrator = true };
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        Assert.Empty(f.Accounts.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrIneffectiveDisableRollsBackAndNeverLogsOff(bool noOp)
    {
        var f = new Fixture();
        f.Accounts.FailDisable = !noOp;
        f.Accounts.NoOpDisable = noOp;
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        Assert.True(f.Accounts.Target!.Enabled);
        Assert.Equal(LockState.Unlocked, f.Store.State);
        Assert.DoesNotContain(f.Accounts.Mutations, s => s.StartsWith("logoff"));
        Assert.False((await f.Manager.GetStatusAsync()).Locked);
    }

    [Fact]
    public async Task ErrorAfterDisableRetainsPendingIntentAndRequiresRecoveryWithoutLogoffOrEnable()
    {
        var f = new Fixture();
        f.Accounts.FailAfterDisable = true;
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        Assert.True(f.Store.State.DisablePending);
        Assert.False(f.Store.State.AccountDisabledByFocusLock);
        f.Clock.Now += TimeSpan.FromMinutes(2);
        await f.Manager.ExpireAsync();
        Assert.True((await f.Manager.GetStatusAsync()).RecoveryRequired);
        Assert.Equal(new[] { "disable:" + Fixture.TargetSid }, f.Accounts.Mutations);
    }

    [Fact]
    public async Task FailureToPersistIntentNeverDisables()
    {
        var f = new Fixture();
        f.Store.FailSaveNumber = 1;
        await Assert.ThrowsAsync<IOException>(() => f.Manager.LockAsync(1));
        Assert.Empty(f.Accounts.Mutations);
    }

    [Fact]
    public async Task FailureToPersistSuccessNeverLogsOffAndRetainsIntentForRecovery()
    {
        var f = new Fixture();
        f.Store.FailSaveNumber = 2;
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        Assert.True(f.Store.State.DisablePending);
        Assert.False(f.Store.State.AccountDisabledByFocusLock);
        Assert.True((await f.Manager.GetStatusAsync()).RecoveryRequired);
        Assert.Equal(new[] { "disable:" + Fixture.TargetSid }, f.Accounts.Mutations);
    }

    [Fact]
    public async Task ExpirationEnablesOnlyOwnedTargetAndClearsStateAfterVerification()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(1);
        f.Accounts.BeforeEnable = () => Assert.True(f.Store.State.AccountDisabledByFocusLock);
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await f.Manager.ExpireAsync();
        await f.Manager.ExpireAsync();
        Assert.True(f.Accounts.Target!.Enabled);
        Assert.Equal(1, f.Accounts.Mutations.Count(s => s == "enable:" + Fixture.TargetSid));
        Assert.DoesNotContain(f.Accounts.Mutations, s => s.Contains(Fixture.OtherSid));
        Assert.Equal(LockState.Unlocked, f.Store.State);
    }

    [Fact]
    public async Task ExpiredRestartEnablesOwnedTarget()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(1);
        f.Clock.Now += TimeSpan.FromMinutes(2);
        var restarted = f.NewManager();
        await restarted.InitializeAsync();
        Assert.True(f.Accounts.Target!.Enabled);
        Assert.False((await restarted.GetStatusAsync()).Locked);
        Assert.Equal(LockState.Unlocked, f.Store.State);
    }

    [Fact]
    public async Task ActiveRestartKeepsDeadlineAndDoesNotDisableAgainIfAlreadyDisabled()
    {
        var f = new Fixture();
        var original = await f.Manager.LockAsync(30);
        f.Clock.Now += TimeSpan.FromMinutes(5);
        var restarted = f.NewManager();
        await restarted.InitializeAsync();
        var status = await restarted.GetStatusAsync();
        Assert.Equal(original.LockUntilUtc, status.LockUntilUtc);
        Assert.Equal(25 * 60, status.RemainingSeconds);
        Assert.Equal(1, f.Accounts.Mutations.Count(s => s.StartsWith("disable")));
    }

    [Fact]
    public async Task ActiveRestartRevalidatesAndDisablesSameOwnedAccountIfItWasReenabled()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(30);
        f.Accounts.Target = f.Accounts.Target! with { Enabled = true };
        await f.NewManager().InitializeAsync();
        Assert.False(f.Accounts.Target.Enabled);
        Assert.Equal(2, f.Accounts.Mutations.Count(s => s == "disable:" + Fixture.TargetSid));
    }

    [Theory]
    [InlineData("name", false)]
    [InlineData("sid", false)]
    [InlineData("name", true)]
    [InlineData("sid", true)]
    [InlineData("admin", true)]
    public async Task ConflictingPersistedIdentityOrChangedSafetyNeverMutates(string conflict, bool expired)
    {
        var f = new Fixture();
        await f.Manager.LockAsync(1);
        f.Accounts.Mutations.Clear();
        if (conflict == "name") f.Store.State = f.Store.State with { TargetUsername = "UnrelatedUser" };
        if (conflict == "sid") f.Store.State = f.Store.State with { TargetSid = Fixture.OtherSid };
        if (conflict == "admin") f.Accounts.Target = f.Accounts.Target! with { IsAdministrator = true };
        if (expired) f.Clock.Now += TimeSpan.FromMinutes(2);
        var restarted = f.NewManager();
        await restarted.InitializeAsync();
        await restarted.ExpireAsync();
        await Assert.ThrowsAsync<EnforcementException>(() => restarted.LockAsync(1));
        Assert.True((await restarted.GetStatusAsync()).RecoveryRequired);
        Assert.Empty(f.Accounts.Mutations);
        Assert.True(f.Store.State.Locked);
    }

    [Fact]
    public async Task CorruptStateNeverModifiesAccountsAndBlocksEnforcement()
    {
        var f = new Fixture();
        f.Store.State = LockState.Unlocked with { IsCorrupt = true };
        await f.Manager.InitializeAsync();
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        await f.Manager.ExpireAsync();
        Assert.Empty(f.Accounts.Mutations);
        Assert.Empty(f.Store.Saves);
        Assert.Equal(0, f.Accounts.Reads);
        Assert.True((await f.Manager.GetStatusAsync()).RecoveryRequired);
    }

    [Fact]
    public async Task TimerOnlyReplacementOfCorruptFileRetainsRecoveryMarkerForFutureEnforcement()
    {
        var f = new Fixture();
        f.Settings.Enabled = false;
        f.Store.State = LockState.Unlocked with { IsCorrupt = true };
        await f.Manager.InitializeAsync();
        await f.Manager.LockAsync(1);
        Assert.True(f.Store.State.RecoveryRequired);
        f.Settings.Enabled = true;
        var restarted = f.NewManager();
        await restarted.InitializeAsync();
        await Assert.ThrowsAsync<EnforcementException>(() => restarted.LockAsync(1));
        Assert.Empty(f.Accounts.Mutations);
    }

    [Fact]
    public async Task PendingDisableAfterCrashIsNeverAssumedOwnedOrAutomaticallyEnabled()
    {
        var f = new Fixture();
        f.Store.State = new LockState(2, true, f.Clock.Now.AddMinutes(-1))
        { TargetUsername = "FocusLockTest", TargetSid = Fixture.TargetSid, DisablePending = true };
        f.Accounts.Target = f.Accounts.Target! with { Enabled = false };
        await f.Manager.InitializeAsync();
        await f.Manager.ExpireAsync();
        Assert.True((await f.Manager.GetStatusAsync()).RecoveryRequired);
        Assert.Empty(f.Accounts.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAndDryRunTimersNeverEnableAccountsTheyDidNotDisable(bool simulated)
    {
        var f = new Fixture();
        f.Store.State = simulated
            ? new LockState(2, true, f.Clock.Now.AddMinutes(-1))
                { TargetUsername = "FocusLockTest", TargetSid = Fixture.TargetSid, SimulatedEnforcement = true }
            : new LockState(1, true, f.Clock.Now.AddMinutes(-1));
        f.Accounts.Target = f.Accounts.Target! with { Enabled = false };
        await f.Manager.InitializeAsync();
        Assert.Empty(f.Accounts.Mutations);
        Assert.False(f.Accounts.Target.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledModeOrDryRunDoesNotRestoreAnExistingRealLock(bool dryRun)
    {
        var f = new Fixture();
        await f.Manager.LockAsync(1);
        f.Accounts.Mutations.Clear();
        f.Clock.Now += TimeSpan.FromMinutes(2);
        f.Settings.Enabled = dryRun;
        f.Settings.DryRun = dryRun;
        var restarted = f.NewManager();
        await restarted.InitializeAsync();
        Assert.True((await restarted.GetStatusAsync()).RecoveryRequired);
        Assert.Empty(f.Accounts.Mutations);
        Assert.True(f.Store.State.AccountDisabledByFocusLock);
    }

    [Fact]
    public async Task TurningOffDryRunCannotRetroactivelyEnforceSimulatedTimer()
    {
        var f = new Fixture();
        f.Settings.DryRun = true;
        await f.Manager.LockAsync(1);
        f.Settings.DryRun = false;
        await f.NewManager().InitializeAsync();
        Assert.Empty(f.Accounts.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnableFailureOrFailedVerificationKeepsOwnershipForRecovery(bool noOp)
    {
        var f = new Fixture();
        await f.Manager.LockAsync(1);
        f.Accounts.FailEnable = !noOp;
        f.Accounts.NoOpEnable = noOp;
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await f.Manager.ExpireAsync();
        Assert.True(f.Store.State.AccountDisabledByFocusLock);
        Assert.True((await f.Manager.GetStatusAsync()).RecoveryRequired);
    }

    [Fact]
    public async Task AlreadyReenabledTargetIsIdempotentAtExpiration()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(1);
        f.Accounts.Target = f.Accounts.Target! with { Enabled = true };
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await f.Manager.ExpireAsync();
        Assert.DoesNotContain(f.Accounts.Mutations, s => s.StartsWith("enable"));
        Assert.Equal(LockState.Unlocked, f.Store.State);
    }

    [Fact]
    public async Task LogoffFailureKeepsTimerSoExpirationStillEnablesTarget()
    {
        var f = new Fixture();
        f.Accounts.FailLogoff = true;
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        Assert.True(f.Store.State.AccountDisabledByFocusLock);
        var restarted = f.NewManager();
        await restarted.InitializeAsync();
        f.Clock.Now += TimeSpan.FromMinutes(2);
        await restarted.ExpireAsync();
        Assert.True(f.Accounts.Target!.Enabled);
        Assert.Equal(LockState.Unlocked, f.Store.State);
    }

    [Fact]
    public async Task NewRequestCannotOverwriteAnOwnedLock()
    {
        var f = new Fixture();
        var status = await f.Manager.LockAsync(30);
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.LockAsync(1));
        Assert.Equal(status.LockUntilUtc, f.Store.State.LockUntilUtc);
    }

    [Fact]
    public void ConfirmationExplicitlyWarnsAboutTargetSignoutAndUnsavedWork()
    {
        var status = new LockStatus(false, null, 0, "Test") { EnforcementEnabled = true, DryRun = false, TargetUsername = "FocusLockTest" };
        var prompt = LockConfirmation.Create(status, 15);
        Assert.Contains("Lock FocusLockTest for 15 minutes?", prompt);
        Assert.Contains("disabled", prompt);
        Assert.Contains("signed out", prompt);
        Assert.Contains("Unsaved work", prompt);
        Assert.Contains("no account will be disabled", LockConfirmation.Create(status with { DryRun = true }, 15));
    }

    private sealed class Fixture
    {
        public const string TargetSid = "S-1-5-21-1-2-3-1001";
        public const string OtherSid = "S-1-5-21-1-2-3-1002";
        public TestClock Clock { get; } = new();
        public MemoryStore Store { get; } = new();
        public FakeAccounts Accounts { get; } = new();
        public CaptureLogger Log { get; } = new();
        public AccountEnforcementSettings Settings { get; } = new() { Enabled = true, DryRun = false, TargetUsername = "FocusLockTest", RecoveryAdminUsername = "RecoveryAdmin" };
        public LockManager Manager { get; }
        public Fixture() => Manager = NewManager();
        public LockManager NewManager() => new(Clock, Store, Settings, Accounts, Log);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class MemoryStore : ILockStateStore
    {
        public LockState State = LockState.Unlocked;
        public List<LockState> Saves { get; } = [];
        public int FailSaveNumber;
        public Task<LockState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task SaveAsync(LockState state, CancellationToken cancellationToken)
        {
            if (Saves.Count + 1 == FailSaveNumber) throw new IOException("Simulated disk failure");
            Saves.Add(state);
            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccounts : IAccountManager
    {
        public AccountDetails? Target = new("FocusLockTest", Fixture.TargetSid, true, true, false, false, false);
        public List<string> Mutations { get; } = [];
        public int Reads;
        public bool FailDisable, NoOpDisable, FailAfterDisable, FailEnable, NoOpEnable, FailLogoff;
        public Action? BeforeDisable, BeforeEnable, BeforeLogoff;
        public AccountDetails? GetTargetAccount() { Reads++; return Target; }
        public IReadOnlyList<AccountSession> GetInteractiveSessions() =>
            [new(0, Fixture.TargetSid), new(3, Fixture.TargetSid), new(9, Fixture.OtherSid)];
        public void DisableTarget(string expectedSid)
        {
            Assert.Equal(Target!.Sid, expectedSid);
            BeforeDisable?.Invoke();
            if (FailDisable) throw new Win32Exception(5);
            Mutations.Add("disable:" + expectedSid);
            if (!NoOpDisable) Target = Target with { Enabled = false };
            if (FailAfterDisable) throw new Win32Exception(5);
        }
        public void EnableTarget(string expectedSid)
        {
            Assert.Equal(Target!.Sid, expectedSid);
            BeforeEnable?.Invoke();
            if (FailEnable) throw new Win32Exception(5);
            Mutations.Add("enable:" + expectedSid);
            if (!NoOpEnable) Target = Target with { Enabled = true };
        }
        public void LogOffTargetSession(int sessionId, string expectedSid)
        {
            Assert.Equal(Fixture.TargetSid, expectedSid);
            Assert.Equal(3, sessionId);
            Assert.False(Target!.Enabled);
            BeforeLogoff?.Invoke();
            if (FailLogoff) throw new Win32Exception(5);
            Mutations.Add("logoff:" + sessionId);
        }
    }

    private sealed class CaptureLogger : ILogger<LockManager>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
