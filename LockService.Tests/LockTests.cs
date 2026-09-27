using LockService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LockService.Tests;

public sealed class LockTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FocusLockTests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock clock = new();
    private JsonLockStateStore Store() => new(new ServiceSettings { StateDirectory = directory }, NullLogger<JsonLockStateStore>.Instance);
    private LockManager Manager() => new(clock, Store());

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(720)]
    public async Task CreatesAndPersistsLockIncludingBoundaries(int minutes)
    {
        var manager = Manager();
        await manager.InitializeAsync();
        var status = await manager.LockAsync(minutes);
        Assert.True(status.Locked);
        Assert.Equal(clock.UtcNow.AddMinutes(minutes), status.LockUntilUtc);
        Assert.Equal(minutes * 60L, status.RemainingSeconds);
        Assert.Equal(Environment.MachineName, status.MachineName);
        var saved = await Store().LoadAsync(default);
        Assert.Equal(new LockState(1, true, status.LockUntilUtc), saved);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(721)]
    [InlineData(int.MaxValue)]
    public async Task RejectsInvalidDurationWithoutChangingState(int minutes)
    {
        var manager = Manager();
        var original = await manager.LockAsync(30);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.LockAsync(minutes));
        Assert.Equal(original, await manager.GetStatusAsync());
    }

    [Fact]
    public async Task ExpirationClearsPersistedStateAtExactDeadline()
    {
        var manager = Manager();
        await manager.LockAsync(1);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await manager.ExpireAsync();
        var status = await manager.GetStatusAsync();
        Assert.False(status.Locked);
        Assert.Null(status.LockUntilUtc);
        Assert.Equal(0, status.RemainingSeconds);
        Assert.Equal(LockState.Unlocked, await Store().LoadAsync(default));
    }

    [Fact]
    public async Task RestartPreservesAbsoluteDeadline()
    {
        var original = await Manager().LockAsync(30);
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        var restarted = Manager();
        await restarted.InitializeAsync();
        var status = await restarted.GetStatusAsync();
        Assert.True(status.Locked);
        Assert.Equal(original.LockUntilUtc, status.LockUntilUtc);
        Assert.Equal(25 * 60, status.RemainingSeconds);
    }

    [Fact]
    public async Task StartupClearsAlreadyExpiredState()
    {
        await Manager().LockAsync(1);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        var restarted = Manager();
        await restarted.InitializeAsync();
        Assert.False((await restarted.GetStatusAsync()).Locked);
        Assert.Equal(LockState.Unlocked, await Store().LoadAsync(default));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"locked\":false,\"lockUntilUtc\":null}")]
    [InlineData("{\"schemaVersion\":1,\"locked\":true,\"lockUntilUtc\":null}")]
    [InlineData("{\"schemaVersion\":1,\"locked\":true,\"lockUntilUtc\":\"invalid\"}")]
    public async Task CorruptStateStartsUnlockedAndCanBeReplaced(string json)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), json);
        var manager = Manager();
        await manager.InitializeAsync();
        Assert.False((await manager.GetStatusAsync()).Locked);
        await manager.LockAsync(15);
        Assert.True((await Store().LoadAsync(default)).Locked);
    }

    [Fact]
    public async Task MissingStateStartsUnlocked()
    {
        var manager = Manager();
        await manager.InitializeAsync();
        Assert.False((await manager.GetStatusAsync()).Locked);
    }

    [Fact]
    public async Task FailedSaveDoesNotAcceptLock()
    {
        var manager = new LockManager(clock, new FailingStore());
        await Assert.ThrowsAsync<IOException>(() => manager.LockAsync(30));
        Assert.False((await manager.GetStatusAsync()).Locked);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("::")]
    public void RejectsNonLoopbackListeners(string address) =>
        Assert.Throws<InvalidOperationException>(() => new ServiceSettings { ListenAddress = address }.Validate());

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FailingStore : ILockStateStore
    {
        public Task<LockState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(LockState.Unlocked);
        public Task SaveAsync(LockState state, CancellationToken cancellationToken) => throw new IOException("Test disk failure");
    }
}
