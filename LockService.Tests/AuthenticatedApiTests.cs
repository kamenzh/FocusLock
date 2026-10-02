using System.Net;
using System.Net.Http.Json;
using System.Text;
using FocusLock.Client;
using FocusLock.Security;
using LockService;
using LockService.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;
using Xunit;

namespace LockService.Tests;

public sealed class AuthenticatedApiTests
{
    [Fact]
    public async Task AuthenticatedClientCanLockPollAndUnlockConfiguredFakeAccount()
    {
        await using var f = await ApiFixture.StartAsync();
        Assert.False((await f.Client.GetStatusAsync()).Locked);
        Assert.True((await f.Client.LockAsync(30)).Locked);
        Assert.False(f.Accounts.Enabled);
        Assert.True((await f.Client.GetStatusAsync()).Locked);
        Assert.False((await f.Client.UnlockAsync()).Locked);
        Assert.True(f.Accounts.Enabled);
        Assert.False((await f.Client.UnlockAsync()).Locked);
        Assert.Equal(1, f.Accounts.EnableCalls);
        Assert.Contains(SecurityEvent.LockAccepted, f.Audit.Events);
        Assert.Contains(SecurityEvent.UnlockAccepted, f.Audit.Events);
        Assert.Contains(SecurityEvent.AccountDisabled, f.Audit.Events);
        Assert.Contains(SecurityEvent.AccountEnabled, f.Audit.Events);
    }

    [Theory]
    [InlineData("GET", "/api/status")]
    [InlineData("POST", "/api/lock")]
    [InlineData("POST", "/api/unlock")]
    public async Task MissingAuthenticationNeverReachesStateOrAccounts(string method, string path)
    {
        await using var f = await ApiFixture.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await f.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(new ApiResult(false, "Authentication failed."), await response.Content.ReadFromJsonAsync<ApiResult>());
        Assert.Equal(0, f.Accounts.Mutations);
        Assert.False(f.Store.State.Locked);
    }

    [Fact]
    public async Task ReplayProducesSameGenericErrorAndDoesNotRepeatCommand()
    {
        await using var f = await ApiFixture.StartAsync();
        var nonce = RequestSigning.NewNonce();
        var body = Encoding.UTF8.GetBytes("{\"durationMinutes\":30}");
        using var first = f.Signed("POST", "/api/lock", body, nonce);
        using var second = f.Signed("POST", "/api/lock", body, nonce);
        using var accepted = await f.Http.SendAsync(first);
        using var replayed = await f.Http.SendAsync(second);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(new ApiResult(false, "Authentication failed."), await replayed.Content.ReadFromJsonAsync<ApiResult>());
        Assert.Contains(SecurityEvent.ReplayRejected, f.Audit.Events);
        Assert.Equal(1, f.Accounts.DisableCalls);
    }

    [Fact]
    public async Task HealthIsPublicAndContainsOnlyMinimalHealth()
    {
        await using var f = await ApiFixture.StartAsync();
        using var response = await f.Http.GetAsync("/api/health");
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/unlock")]
    [InlineData("/api/unlock/")]
    [InlineData("/API/UNLOCK")]
    [InlineData("/api/status")]
    public async Task EmptyBodyEndpointsRejectAccountSelectorsEvenWithValidAuthentication(string path)
    {
        await using var f = await ApiFixture.StartAsync();
        using var request = f.Signed(path.EndsWith("status") ? "GET" : "POST", path, Encoding.UTF8.GetBytes("{\"username\":\"Other\"}"));
        using var response = await f.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, f.Accounts.Mutations);
    }

    [Fact]
    public async Task LockRejectsUsernameAndQueryStringsAreNeverUnsignedInputs()
    {
        await using var f = await ApiFixture.StartAsync();
        using var extra = f.Signed("POST", "/api/lock", Encoding.UTF8.GetBytes("{\"durationMinutes\":1,\"username\":\"Other\"}"));
        using var extraResponse = await f.Http.SendAsync(extra);
        Assert.Equal(HttpStatusCode.BadRequest, extraResponse.StatusCode);
        using var query = f.Signed("GET", "/api/status?username=Other", []);
        using var queryResponse = await f.Http.SendAsync(query);
        Assert.Equal(HttpStatusCode.Unauthorized, queryResponse.StatusCode);
        Assert.Equal(0, f.Accounts.Mutations);
    }

    [Fact]
    public async Task ClientDistinguishesAuthenticationFromNormalApiErrors()
    {
        await using var f = await ApiFixture.StartAsync();
        var wrongClient = new FocusLockApiClient(f.Http, new TestSecretStore(), () => f.Clock.UtcNow);
        var authError = await Assert.ThrowsAsync<FocusLockApiException>(() => wrongClient.GetStatusAsync());
        Assert.Equal(ApiFailureKind.Authentication, authError.Kind);
        Assert.Equal("Authentication failed.", authError.Message);
        var apiError = await Assert.ThrowsAsync<FocusLockApiException>(() => f.Client.LockAsync(0));
        Assert.Equal(ApiFailureKind.Api, apiError.Kind);
        Assert.Equal(HttpStatusCode.BadRequest, apiError.StatusCode);
        Assert.Equal(0, f.Accounts.Mutations);
    }

    [Fact]
    public async Task ClientDistinguishesUnreachableService()
    {
        using var http = new HttpClient(new UnreachableHandler()) { BaseAddress = new Uri("http://127.0.0.1") };
        var client = new FocusLockApiClient(http, new TestSecretStore());
        var error = await Assert.ThrowsAsync<FocusLockApiException>(() => client.GetStatusAsync());
        Assert.Equal(ApiFailureKind.Unreachable, error.Kind);
    }

    [Fact]
    public async Task MissingServiceSecretFailsClosedButHealthRemainsAvailable()
    {
        await using var f = await ApiFixture.StartAsync(unavailableSecret: true);
        using var request = f.Signed("GET", "/api/status", []);
        using var response = await f.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var health = await f.Http.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, f.Accounts.Mutations);
    }

    [Fact]
    public async Task ExpirationRecordsAuditAndAuthenticatedUnlockRemainsIdempotent()
    {
        await using var f = await ApiFixture.StartAsync();
        await f.Client.LockAsync(1);
        f.Clock.UtcNow += TimeSpan.FromMinutes(1);
        await f.App.Services.GetRequiredService<LockManager>().ExpireAsync();
        Assert.Contains(SecurityEvent.TimerExpired, f.Audit.Events);
        Assert.False((await f.Client.UnlockAsync()).Locked);
        Assert.Equal(1, f.Accounts.EnableCalls);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("Test offline");
    }

    private sealed class ApiFixture : IAsyncDisposable
    {
        public SecurityTestClock Clock { get; } = new();
        public TestSecretStore Secrets { get; } = new();
        public FakeAccounts Accounts { get; } = new();
        public MemoryStore Store { get; } = new();
        public CaptureAudit Audit { get; } = new();
        public WebApplication App { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public FocusLockApiClient Client { get; private set; } = null!;

        public static async Task<ApiFixture> StartAsync(bool unavailableSecret = false)
        {
            var f = new ApiFixture();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IClock>(f.Clock);
            builder.Services.AddSingleton<ISecretStore>(unavailableSecret ? new UnavailableSecret() : f.Secrets);
            builder.Services.AddSingleton<INonceStore>(new NonceStore(f.Clock));
            builder.Services.AddSingleton<IRequestAuthenticator, RequestAuthenticator>();
            builder.Services.AddSingleton<ISecurityAudit>(f.Audit);
            builder.Services.AddSingleton(new LockManager(f.Clock, f.Store, new AccountEnforcementSettings
            { Enabled = true, DryRun = false, TargetUsername = "FocusLockTest" }, f.Accounts, audit: f.Audit));
            f.App = builder.Build();
            f.App.UseRouting();
            f.App.UseMiddleware<AuthenticationMiddleware>();
            f.App.MapFocusLockApi();
            await f.App.StartAsync();
            f.Http = f.App.GetTestClient();
            f.Client = new(f.Http, f.Secrets, () => f.Clock.UtcNow);
            return f;
        }

        public HttpRequestMessage Signed(string method, string path, byte[] body, string? nonce = null)
        {
            nonce ??= RequestSigning.NewNonce();
            var timestamp = RequestSigning.Timestamp(Clock.UtcNow);
            var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add(RequestSigning.TimestampHeader, timestamp);
            request.Headers.Add(RequestSigning.NonceHeader, nonce);
            request.Headers.Add(RequestSigning.SignatureHeader, Convert.ToBase64String(RequestSigning.Sign(Secrets.Key, method, path, timestamp, nonce, body)));
            return request;
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); }
    }

    private sealed class UnavailableSecret : ISecretStore
    {
        public Task<byte[]> ReadAsync(CancellationToken ct = default) => throw new SecretUnavailableException();
    }
    private sealed class CaptureAudit : ISecurityAudit
    {
        public List<SecurityEvent> Events { get; } = [];
        public void Record(SecurityEvent kind, string? reasonCode = null) => Events.Add(kind);
    }
    private sealed class MemoryStore : ILockStateStore
    {
        public LockState State = LockState.Unlocked;
        public Task<LockState> LoadAsync(CancellationToken ct) => Task.FromResult(State);
        public Task SaveAsync(LockState state, CancellationToken ct) { State = state; return Task.CompletedTask; }
    }
    private sealed class FakeAccounts : IAccountManager
    {
        private const string Sid = "S-1-5-21-1-2-3-1001";
        public bool Enabled = true;
        public int EnableCalls, DisableCalls;
        public int Mutations => EnableCalls + DisableCalls;
        public AccountDetails GetTargetAccount() => new("FocusLockTest", Sid, true, Enabled, false, false, false);
        public IReadOnlyList<AccountSession> GetInteractiveSessions() => [];
        public void DisableTarget(string expectedSid) { Assert.Equal(Sid, expectedSid); Enabled = false; DisableCalls++; }
        public void EnableTarget(string expectedSid) { Assert.Equal(Sid, expectedSid); Enabled = true; EnableCalls++; }
        public void LogOffTargetSession(int sessionId, string expectedSid) => throw new Exception("No test sessions exist");
    }
}
