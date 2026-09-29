using System.Text.Json;

namespace LockService;

public sealed class JsonLockStateStore(ServiceSettings settings, ILogger<JsonLockStateStore> logger) : ILockStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path = Path.Combine(settings.StateDirectory, "state.json");

    public async Task<LockState> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out _) ||
                !root.TryGetProperty("locked", out _) ||
                !root.TryGetProperty("lockUntilUtc", out _))
                throw new JsonException("Missing state fields.");
            var state = root.Deserialize<LockState>(JsonOptions);
            if (state is null || state.SchemaVersion is not (1 or 2) ||
                state.Locked != state.LockUntilUtc.HasValue ||
                state.LockUntilUtc is { Offset: var offset } && offset != TimeSpan.Zero)
                throw new JsonException("Invalid state schema or UTC timestamp.");
            if (state.SchemaVersion == 1 && (state.TargetUsername is not null || state.TargetSid is not null ||
                state.AccountDisabledByFocusLock || state.DisablePending || state.SimulatedEnforcement))
                throw new JsonException("Legacy state cannot contain enforcement metadata.");
            if (state.SchemaVersion == 2 && (!state.Locked ||
                !AccountSafety.IsLocalUsername(state.TargetUsername) || state.TargetSid is null ||
                !AccountSafety.IsOrdinaryAccountSid(state.TargetSid) ||
                !root.TryGetProperty("accountDisabledByFocusLock", out _) ||
                !root.TryGetProperty("disablePending", out _) ||
                !root.TryGetProperty("simulatedEnforcement", out _) ||
                (state.AccountDisabledByFocusLock ? 1 : 0) + (state.DisablePending ? 1 : 0) +
                (state.SimulatedEnforcement ? 1 : 0) != 1))
                throw new JsonException("Invalid enforcement metadata.");
            return state;
        }
        catch (FileNotFoundException) { return LockState.Unlocked; }
        catch (DirectoryNotFoundException) { return LockState.Unlocked; }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Corrupt state at {Path}; no account actions are safe. Administrator recovery required for enforcement", path);
            return LockState.Unlocked with { IsCorrupt = true };
        }
    }

    public async Task SaveAsync(LockState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.StateDirectory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
