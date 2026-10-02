using System.Globalization;
using System.Security.Cryptography;
using FocusLock.Security;

namespace LockService.Security;

public enum AuthenticationFailure { None, Malformed, Timestamp, Signature, Replay, CacheFull, CacheUnavailable, SecretUnavailable }
public interface IRequestAuthenticator
{
    Task<AuthenticationFailure> AuthenticateAsync(string method, string path, byte[] body, string? timestamp, string? nonce, string? signature, CancellationToken ct);
}

public sealed class RequestAuthenticator(IClock clock, ISecretStore secrets, INonceStore nonces) : IRequestAuthenticator
{
    public async Task<AuthenticationFailure> AuthenticateAsync(string method, string path, byte[] body,
        string? timestamp, string? nonce, string? signature, CancellationToken ct)
    {
        if (timestamp is null || timestamp.Length > 19 ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            timestamp != seconds.ToString(CultureInfo.InvariantCulture) ||
            nonce is null || nonce.Length != 64 || nonce.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            signature is null || signature.Length != 44 || body.Length > RequestSigning.MaxBodyBytes)
            return AuthenticationFailure.Malformed;
        var now = clock.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs((decimal)seconds - now) > RequestSigning.ClockSkewSeconds) return AuthenticationFailure.Timestamp;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(signature); }
        catch (FormatException) { return AuthenticationFailure.Malformed; }
        if (supplied.Length != 32) return AuthenticationFailure.Malformed;
        byte[] key;
        try { key = await secrets.ReadAsync(ct); }
        catch (SecretUnavailableException) { return AuthenticationFailure.SecretUnavailable; }
        try
        {
            if (key.Length != 32) return AuthenticationFailure.SecretUnavailable;
            var expected = RequestSigning.Sign(key, method, path, timestamp, nonce, body);
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return AuthenticationFailure.Signature;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        // A future-dated request can remain valid for nearly 121 seconds. Retain until
        // its last acceptable Unix second has passed, not merely 60 seconds after receipt.
        return (await nonces.TryAcceptAsync(nonce, seconds + RequestSigning.ClockSkewSeconds + 1, ct)) switch
        {
            NonceResult.Accepted => AuthenticationFailure.None,
            NonceResult.Replay => AuthenticationFailure.Replay,
            NonceResult.Full => AuthenticationFailure.CacheFull,
            _ => AuthenticationFailure.CacheUnavailable
        };
    }
}
