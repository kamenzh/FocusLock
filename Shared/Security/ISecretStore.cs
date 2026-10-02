using System.Security.Cryptography;
using System.Text;

namespace FocusLock.Security;

public interface ISecretStore
{
    // Returns a fresh caller-owned buffer; caller must zero it after use.
    Task<byte[]> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class SecretUnavailableException() : Exception("The protected FocusLock secret is missing or unreadable. Run the secret setup script under this Windows identity.");

public sealed class DpapiSecretStore(string path) : ISecretStore
{
    public static byte[] Entropy => Encoding.UTF8.GetBytes("FocusLock.SharedSecret.v1");

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new SecretUnavailableException();
            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (!Path.IsPathFullyQualified(expanded)) throw new SecretUnavailableException();
            await using var stream = File.OpenRead(expanded);
            if (stream.Length is < 1 or > 16384) throw new SecretUnavailableException();
            var protectedBytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(protectedBytes, cancellationToken);
            var key = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new SecretUnavailableException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Never include cryptographic material or provider diagnostics in client-visible errors.
            throw new SecretUnavailableException();
        }
    }
}
