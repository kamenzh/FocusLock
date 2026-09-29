using LockService;
using Shared;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Service").Get<ServiceSettings>() ?? new();
var enforcement = builder.Configuration.GetSection("AccountEnforcement").Get<AccountEnforcementSettings>() ?? new();
var address = settings.Validate();
// Prevent configuration from adding non-loopback endpoints.
if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
    throw new InvalidOperationException("Configure only Service:ListenAddress and Service:Port.");
builder.WebHost.ConfigureKestrel(options =>
{
    // Disable endpoint configuration reloads as well as rejecting extra endpoints at startup.
    options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
    options.Listen(address, settings.Port);
});
builder.Services.AddWindowsService(options => options.ServiceName = "FocusLock");
if (!WindowsServiceHelpers.IsWindowsService())
{
    // Console development runs must not need permission to write the Windows Event Log.
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole();
}
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(enforcement);
builder.Services.AddSingleton<IAccountManager>(_ => OperatingSystem.IsWindows()
    ? new WindowsAccountManager(enforcement) : new UnsupportedAccountManager());
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ILockStateStore, JsonLockStateStore>();
builder.Services.AddSingleton<LockManager>();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
using var stateLease = new StateDirectoryLease(settings);
app.Logger.LogInformation("State directory: {Directory}; enforcement enabled: {Enabled}; DryRun: {DryRun}",
    settings.StateDirectory, enforcement.Enabled, enforcement.DryRun);
await app.Services.GetRequiredService<LockManager>().InitializeAsync();
app.MapGet("/api/health", () => Results.Ok(new ApiResult(true, "FocusLock service is running.")));
app.MapGet("/api/status", async (LockManager manager, CancellationToken ct) =>
    Results.Ok(await manager.GetStatusAsync(ct)));
app.MapPost("/api/lock", async (LockRequest request, LockManager manager, ILogger<LockManager> logger, CancellationToken ct) =>
{
    if (request.DurationMinutes is < 1 or > 720)
        return Results.BadRequest(new ApiResult(false, "Duration must be between 1 and 720 minutes."));
    try { return Results.Ok(await manager.LockAsync(request.DurationMinutes, ct)); }
    catch (EnforcementException exception)
    {
        logger.LogError(exception, "Account enforcement request refused or incomplete");
        return Results.Json(new ApiResult(false, exception.Message), statusCode: 409);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        logger.LogError(exception, "Unable to persist lock request");
        return Results.Json(new ApiResult(false, "Unable to save lock state. Check the service state-directory permissions."), statusCode: 503);
    }
});
await app.RunAsync();
