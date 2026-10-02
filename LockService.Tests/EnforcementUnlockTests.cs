using LockService;
using Xunit;

namespace LockService.Tests;

public sealed partial class EnforcementTests
{
    [Fact]
    public async Task EarlyUnlockEnablesOnlyVerifiedOwnedTargetAndIsIdempotent()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(30);
        var status = await f.Manager.UnlockAsync();
        Assert.False(status.Locked);
        Assert.True(f.Accounts.Target!.Enabled);
        Assert.Equal(LockState.Unlocked, f.Store.State);
        await f.Manager.UnlockAsync();
        Assert.Equal(1, f.Accounts.Mutations.Count(s => s == "enable:" + Fixture.TargetSid));
        Assert.DoesNotContain(f.Accounts.Mutations, s => s.Contains(Fixture.OtherSid));
    }

    [Fact]
    public async Task UnlockAlreadyUnlockedNeverInspectsOrEnablesAnAccount()
    {
        var f = new Fixture();
        f.Accounts.Target = f.Accounts.Target! with { Enabled = false };
        Assert.False((await f.Manager.UnlockAsync()).Locked);
        Assert.Empty(f.Accounts.Mutations);
        Assert.Equal(0, f.Accounts.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlockNeverEnablesAnUnownedAccount(bool simulated)
    {
        var f = new Fixture();
        f.Store.State = simulated
            ? new LockState(2, true, f.Clock.Now.AddMinutes(10)) { TargetUsername = "FocusLockTest", TargetSid = Fixture.TargetSid, SimulatedEnforcement = true }
            : new LockState(1, true, f.Clock.Now.AddMinutes(10));
        f.Accounts.Target = f.Accounts.Target! with { Enabled = false };
        await f.Manager.InitializeAsync();
        Assert.False((await f.Manager.UnlockAsync()).Locked);
        Assert.Empty(f.Accounts.Mutations);
        Assert.False(f.Accounts.Target.Enabled);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("sid")]
    [InlineData("pending")]
    [InlineData("corrupt")]
    public async Task UnlockRejectsUncertainOrConflictingState(string kind)
    {
        var f = new Fixture();
        await f.Manager.LockAsync(30);
        f.Accounts.Mutations.Clear();
        f.Store.State = kind switch
        {
            "name" => f.Store.State with { TargetUsername = "Unrelated" },
            "sid" => f.Store.State with { TargetSid = Fixture.OtherSid },
            "pending" => f.Store.State with { AccountDisabledByFocusLock = false, DisablePending = true },
            _ => LockState.Unlocked with { IsCorrupt = true }
        };
        var restarted = f.NewManager();
        await restarted.InitializeAsync();
        await Assert.ThrowsAsync<EnforcementException>(() => restarted.UnlockAsync());
        Assert.Empty(f.Accounts.Mutations);
    }

    [Fact]
    public async Task UnlockFailureRetainsOwnershipAndRequiresRecovery()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(30);
        f.Accounts.FailEnable = true;
        await Assert.ThrowsAsync<EnforcementException>(() => f.Manager.UnlockAsync());
        Assert.True(f.Store.State.AccountDisabledByFocusLock);
        Assert.True((await f.Manager.GetStatusAsync()).RecoveryRequired);
    }

    [Fact]
    public async Task UnlockRacingExpirationEnablesOnlyOnceAndClearsState()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(1);
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await Task.WhenAll(Task.Run(() => f.Manager.UnlockAsync()), Task.Run(() => f.Manager.ExpireAsync()));
        Assert.True(f.Accounts.Target!.Enabled);
        Assert.Equal(LockState.Unlocked, f.Store.State);
        Assert.Equal(1, f.Accounts.Mutations.Count(s => s == "enable:" + Fixture.TargetSid));
    }

    [Fact]
    public async Task LockRacingExpirationCannotLoseOwnershipOrTouchAnotherAccount()
    {
        var f = new Fixture();
        // Keep an expired timer in memory until the racing expiration operation runs.
        f.Store.State = new LockState(1, true, f.Clock.Now.AddMinutes(1));
        await f.Manager.InitializeAsync();
        f.Clock.Now += TimeSpan.FromMinutes(1);
        await Task.WhenAll(Task.Run(() => f.Manager.LockAsync(1)), Task.Run(() => f.Manager.ExpireAsync()));
        Assert.True((await f.Manager.GetStatusAsync()).Locked);
        Assert.True(f.Store.State.AccountDisabledByFocusLock);
        Assert.False(f.Accounts.Target!.Enabled);
        Assert.DoesNotContain(f.Accounts.Mutations, s => s.Contains(Fixture.OtherSid));
        await f.Manager.UnlockAsync();
        Assert.True(f.Accounts.Target.Enabled);
    }

    [Fact]
    public async Task FuturePolicyCanPreventEarlyEnableWithoutImplementingUsageOrSchedules()
    {
        var f = new Fixture();
        await f.Manager.LockAsync(30);
        f.Accounts.Mutations.Clear();
        var manager = new LockManager(f.Clock, f.Store, f.Settings, f.Accounts, policy: new OtherRestrictionPolicy());
        await manager.InitializeAsync();
        f.Accounts.Mutations.Clear();
        await Assert.ThrowsAsync<EnforcementException>(() => manager.UnlockAsync());
        Assert.Empty(f.Accounts.Mutations);
        Assert.True(f.Store.State.AccountDisabledByFocusLock);
        Assert.False(f.Accounts.Target!.Enabled);
    }

    private sealed class OtherRestrictionPolicy : IRestrictionPolicy
    {
        public RestrictionDecision Evaluate(LockState manualState, DateTimeOffset utcNow) => new(true, null, RestrictionKind.ScheduledRestriction);
    }
}
