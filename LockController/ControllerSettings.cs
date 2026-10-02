using System.IO;
using System.Text.Json;

namespace LockController;

public sealed class ControllerSettings
{
    public string TargetHostname { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 42831;
    public string SecretFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusLock", "Secrets", "controller.secret.dpapi");

    public Uri GetBaseAddress()
    {
        if (Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(TargetHostname) ||
            Uri.CheckHostName(TargetHostname) == UriHostNameType.Unknown)
            throw new InvalidOperationException("Invalid TargetHostname or Port in appsettings.json.");
        if (TargetHostname != "127.0.0.1")
            throw new InvalidOperationException("Phase 3 TargetHostname must be 127.0.0.1; LAN transport is not enabled.");
        return new UriBuilder(Uri.UriSchemeHttp, TargetHostname, Port).Uri;
    }

    public static async Task<ControllerSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ControllerSettings>(stream, cancellationToken: cancellationToken)
            ?? throw new JsonException("Controller settings cannot be null.");
    }
}
