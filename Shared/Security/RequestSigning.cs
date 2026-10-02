using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FocusLock.Security;

// Protocol utilities, not DTOs. Keys are passed by the caller and never retained here.
public static class RequestSigning
{
    public const string TimestampHeader = "X-FocusLock-Timestamp";
    public const string NonceHeader = "X-FocusLock-Nonce";
    public const string SignatureHeader = "X-FocusLock-Signature";
    public const int ClockSkewSeconds = 60;
    public const int MaxBodyBytes = 4096;

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string Timestamp(DateTimeOffset now) => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    public static byte[] CanonicalBytes(string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        if (new[] { method, path, timestamp, nonce }.Any(s => s.Contains('\n') || s.Contains('\r')))
            throw new ArgumentException("Canonical fields cannot contain newlines.");
        return Encoding.UTF8.GetBytes(string.Join('\n', "FocusLock-HMAC-SHA256-v1", method.ToUpperInvariant(),
            path, timestamp, nonce, Convert.ToHexStringLower(SHA256.HashData(body))));
    }

    public static byte[] Sign(ReadOnlySpan<byte> key, string method, string path, string timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        if (key.Length != 32) throw new CryptographicException("FocusLock requires a 256-bit key.");
        return HMACSHA256.HashData(key, CanonicalBytes(method, path, timestamp, nonce, body));
    }
}
