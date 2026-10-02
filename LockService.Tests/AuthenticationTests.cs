using System.Security.Cryptography;
using System.Text;
using FocusLock.Security;
using LockService;
using LockService.Security;
using Xunit;

namespace LockService.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task ValidRequestSucceedsOnceAndReplayFails()
    {
        var f = new AuthFixture();
        Assert.Equal(AuthenticationFailure.None, await f.Check());
        Assert.Equal(AuthenticationFailure.Replay, await f.Check());
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("invalidKeyLength")]
    [InlineData("signature")]
    [InlineData("missingSignature")]
    [InlineData("missingTimestamp")]
    [InlineData("missingNonce")]
    [InlineData("past")]
    [InlineData("future")]
    [InlineData("body")]
    [InlineData("duration")]
    [InlineData("path")]
    [InlineData("method")]
    [InlineData("malformedSignature")]
    [InlineData("malformedNonce")]
    [InlineData("oversizedBody")]
    public async Task TamperingAndInvalidHeadersAreRejected(string variant)
    {
        var f = new AuthFixture();
        var method = "POST";
        var path = "/api/lock";
        var body = f.Body;
        string? timestamp = f.Timestamp;
        string? nonce = f.Nonce;
        string? signature = f.Signature;
        switch (variant)
        {
            case "secret": f.Secrets.Key = RandomNumberGenerator.GetBytes(32); break;
            case "invalidKeyLength": f.Secrets.Key = new byte[16]; break;
            case "signature": signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); break;
            case "missingSignature": signature = null; break;
            case "missingTimestamp": timestamp = null; break;
            case "missingNonce": nonce = null; break;
            case "past": timestamp = RequestSigning.Timestamp(f.Clock.UtcNow.AddSeconds(-61)); signature = f.Sign(timestamp); break;
            case "future": timestamp = RequestSigning.Timestamp(f.Clock.UtcNow.AddSeconds(61)); signature = f.Sign(timestamp); break;
            case "body": body = Encoding.UTF8.GetBytes("{ \"durationMinutes\":30 }"); break;
            case "duration": body = Encoding.UTF8.GetBytes("{\"durationMinutes\":720}"); break;
            case "path": path = "/api/unlock"; break;
            case "method": method = "GET"; break;
            case "malformedSignature": signature = new string('?',44); break;
            case "malformedNonce": nonce = new string('z',64); break;
            case "oversizedBody": body = new byte[4097]; break;
        }
        Assert.NotEqual(AuthenticationFailure.None,
            await f.Auth.AuthenticateAsync(method, path, body, timestamp, nonce, signature, default));
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(60)]
    public async Task ClockSkewBoundaryIsAccepted(int offset)
    {
        var f = new AuthFixture();
        var timestamp = RequestSigning.Timestamp(f.Clock.UtcNow.AddSeconds(offset));
        Assert.Equal(AuthenticationFailure.None, await f.Auth.AuthenticateAsync("POST", "/api/lock", f.Body, timestamp, f.Nonce, f.Sign(timestamp), default));
    }

    [Fact]
    public async Task FutureTimestampNonceIsRetainedForTheWholeAcceptanceWindow()
    {
        var f = new AuthFixture();
        var timestamp = RequestSigning.Timestamp(f.Clock.UtcNow.AddSeconds(60));
        var signature = f.Sign(timestamp);
        Assert.Equal(AuthenticationFailure.None, await f.Auth.AuthenticateAsync("POST", "/api/lock", f.Body, timestamp, f.Nonce, signature, default));
        f.Clock.UtcNow += TimeSpan.FromSeconds(61);
        await f.Nonces.ExpireAsync(default);
        Assert.Equal(AuthenticationFailure.Replay, await f.Auth.AuthenticateAsync("POST", "/api/lock", f.Body, timestamp, f.Nonce, signature, default));
    }

    [Fact]
    public async Task InvalidSignatureDoesNotConsumeNonce()
    {
        var f = new AuthFixture();
        Assert.Equal(AuthenticationFailure.Signature, await f.Auth.AuthenticateAsync("POST", "/api/lock", f.Body,
            f.Timestamp, f.Nonce, Convert.ToBase64String(new byte[32]), default));
        Assert.Equal(AuthenticationFailure.None, await f.Check());
    }

    [Fact]
    public async Task ConcurrentReplayHasExactlyOneWinner()
    {
        var f = new AuthFixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(f.Check)));
        Assert.Equal(1, results.Count(r => r == AuthenticationFailure.None));
        Assert.Equal(19, results.Count(r => r == AuthenticationFailure.Replay));
    }

    [Fact]
    public async Task CapacityIsBoundedAndExpiredEntriesAreRemoved()
    {
        var clock = new SecurityTestClock();
        var store = new NonceStore(clock, capacity: 2);
        var until = clock.UtcNow.ToUnixTimeSeconds() + 121;
        Assert.Equal(NonceResult.Accepted, await store.TryAcceptAsync("a", until, default));
        Assert.Equal(NonceResult.Accepted, await store.TryAcceptAsync("b", until, default));
        Assert.Equal(NonceResult.Full, await store.TryAcceptAsync("c", until, default));
        // Capacity pressure must not evict a live nonce and permit a replay.
        Assert.Equal(NonceResult.Replay, await store.TryAcceptAsync("a", until, default));
        clock.UtcNow += TimeSpan.FromSeconds(121);
        await store.ExpireAsync(default);
        Assert.Equal(NonceResult.Accepted, await store.TryAcceptAsync("c", until + 121, default));
    }

    [Fact]
    public async Task NonceAcceptanceSurvivesRestartAndCorruptionFailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), "FocusLockNonceTests", Guid.NewGuid().ToString("N"), "nonce-cache.json");
        try
        {
            var clock = new SecurityTestClock();
            var nonce = RequestSigning.NewNonce();
            var until = clock.UtcNow.ToUnixTimeSeconds() + 121;
            Assert.Equal(NonceResult.Accepted, await new NonceStore(clock, path).TryAcceptAsync(nonce, until, default));
            Assert.Equal(NonceResult.Replay, await new NonceStore(clock, path).TryAcceptAsync(nonce, until, default));
            Assert.DoesNotContain(nonce, await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, "corrupt");
            Assert.Equal(NonceResult.Unavailable, await new NonceStore(clock, path).TryAcceptAsync("new", until, default));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void CanonicalEncodingUsesUtf8LfAndNoTrailingNewline()
    {
        var canonical = Encoding.UTF8.GetString(RequestSigning.CanonicalBytes("get", "/api/status", "1700000000", new string('a',64), []));
        Assert.Equal("FocusLock-HMAC-SHA256-v1\nGET\n/api/status\n1700000000\n" + new string('a',64) +
            "\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", canonical);
    }

    private sealed class AuthFixture
    {
        public SecurityTestClock Clock { get; } = new();
        public TestSecretStore Secrets { get; } = new();
        public NonceStore Nonces { get; }
        public RequestAuthenticator Auth { get; }
        public byte[] Body { get; } = Encoding.UTF8.GetBytes("{\"durationMinutes\":30}");
        public string Nonce { get; } = RequestSigning.NewNonce();
        public string Timestamp { get; }
        public string Signature { get; }
        public AuthFixture()
        {
            Nonces = new(Clock);
            Auth = new(Clock, Secrets, Nonces);
            Timestamp = RequestSigning.Timestamp(Clock.UtcNow);
            Signature = Sign(Timestamp);
        }
        public string Sign(string timestamp) => Convert.ToBase64String(RequestSigning.Sign(Secrets.Key, "POST", "/api/lock", timestamp, Nonce, Body));
        public Task<AuthenticationFailure> Check() => Auth.AuthenticateAsync("POST", "/api/lock", Body, Timestamp, Nonce, Signature, default);
    }
}

internal sealed class SecurityTestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
}
internal sealed class TestSecretStore : ISecretStore
{
    public byte[] Key { get; set; } = RandomNumberGenerator.GetBytes(32);
    public Task<byte[]> ReadAsync(CancellationToken ct = default) => Task.FromResult(Key.ToArray());
}
