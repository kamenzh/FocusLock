using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LockService.Security;

public enum NonceResult { Accepted, Replay, Full, Unavailable }
public interface INonceStore
{
    Task<NonceResult> TryAcceptAsync(string nonce, long expiresAtUnixSeconds, CancellationToken ct);
    Task ExpireAsync(CancellationToken ct);
}

// Persist accepted nonce digests before dispatching a command, including across service restarts.
public sealed class NonceStore(IClock clock, string? cachePath = null, int capacity = 4096) : INonceStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, long> entries = new(StringComparer.Ordinal);
    private bool loaded;
    private bool unavailable;

    public async Task<NonceResult> TryAcceptAsync(string nonce, long expiresAtUnixSeconds, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            if (unavailable) return NonceResult.Unavailable;
            Prune();
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(nonce)));
            if (entries.ContainsKey(digest)) return NonceResult.Replay;
            if (entries.Count >= capacity) return NonceResult.Full;
            entries.Add(digest, expiresAtUnixSeconds);
            await PersistAsync(ct);
            return unavailable ? NonceResult.Unavailable : NonceResult.Accepted;
        }
        finally { gate.Release(); }
    }

    public async Task ExpireAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            if (!unavailable && Prune()) await PersistAsync(ct);
        }
        finally { gate.Release(); }
    }

    private bool Prune()
    {
        var now = clock.UtcNow.ToUnixTimeSeconds();
        var expired = entries.Where(p => p.Value <= now).Select(p => p.Key).ToArray();
        foreach (var key in expired) entries.Remove(key);
        return expired.Length > 0;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (loaded) return;
        loaded = true;
        if (cachePath is null) return;
        try
        {
            await using var file = File.OpenRead(cachePath);
            if (file.Length > 1024 * 1024) throw new JsonException();
            var saved = await JsonSerializer.DeserializeAsync<Dictionary<string, long>>(file, cancellationToken: ct);
            if (saved is null || saved.Count > capacity || saved.Any(p => p.Key.Length != 64 ||
                p.Key.Any(c => !char.IsAsciiHexDigitLower(c)) || p.Value < 0)) throw new JsonException();
            entries = saved;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (OperationCanceledException) { loaded = false; throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { unavailable = true; }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (cachePath is null) return;
        var temp = cachePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, entries, cancellationToken: ct);
                await file.FlushAsync(ct);
                file.Flush(true);
            }
            File.Move(temp, cachePath, overwrite: true);
        }
        catch (OperationCanceledException) { unavailable = true; throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { unavailable = true; }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { unavailable = true; }
        }
    }
}

public sealed class NonceCleanupWorker(INonceStore nonces) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await nonces.ExpireAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
