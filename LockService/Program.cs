using LockService;
using LockService.Security;
using FocusLock.Security;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Service").Get<ServiceSettings>() ?? new();
var enforcement = builder.Configuration.GetSection("AccountEnforcement").Get<AccountEnforcementSettings>() ?? new();
var authentication = builder.Configuration.GetSection("Authentication").Get<AuthenticationSettings>() ?? new();
var address = settings.Validate();
if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
    throw new InvalidOperationException("Configure only Service:ListenAddress and Service:Port.");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
    options.Listen(address, settings.Port);
});
builder.Services.AddWindowsService(options => options.ServiceName = "FocusLock");
if (!WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole();
}
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(enforcement);
builder.Services.AddSingleton<IAccountManager>(_ => OperatingSystem.IsWindows()
    ? new WindowsAccountManager(enforcement) : new UnsupportedAccountManager());
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ILockStateStore, JsonLockStateStore>();
builder.Services.AddSingleton<IRestrictionPolicy, ManualLockPolicy>();
builder.Services.AddSingleton<ISecurityAudit, FileSecurityAudit>();
builder.Services.AddSingleton<ISecretStore>(new DpapiSecretStore(authentication.SecretFile));
builder.Services.AddSingleton<INonceStore>(services => new NonceStore(services.GetRequiredService<IClock>(),
    Path.Combine(settings.StateDirectory, "nonce-cache.json")));
builder.Services.AddSingleton<IRequestAuthenticator, RequestAuthenticator>();
builder.Services.AddSingleton<LockManager>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<NonceCleanupWorker>();

var app = builder.Build();
using var stateLease = new StateDirectoryLease(settings);
app.Logger.LogInformation("State directory: {Directory}; enforcement enabled: {Enabled}; DryRun: {DryRun}",
    settings.StateDirectory, enforcement.Enabled, enforcement.DryRun);
// Expiration/recovery still runs if the authentication key is unavailable. API requests fail closed.
await app.Services.GetRequiredService<LockManager>().InitializeAsync();
app.UseRouting();
app.UseMiddleware<AuthenticationMiddleware>();
app.MapFocusLockApi();
await app.RunAsync();
