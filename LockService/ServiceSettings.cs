using System.Net;

namespace LockService;

public sealed class ServiceSettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 42831;
    public string StateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FocusLock");

    public IPAddress Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out var address) || !address.Equals(IPAddress.Loopback))
            throw new InvalidOperationException("Phase 3 Service:ListenAddress must be 127.0.0.1.");
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("Service:Port must be between 1 and 65535.");
        StateDirectory = Environment.ExpandEnvironmentVariables(StateDirectory);
        if (!Path.IsPathFullyQualified(StateDirectory))
            throw new InvalidOperationException("Service:StateDirectory must be an absolute path.");
        return address;
    }
}
