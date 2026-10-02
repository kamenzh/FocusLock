namespace LockService.Security;

public sealed class AuthenticationSettings
{
    public string SecretFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusLock", "Secrets", "service.secret.dpapi");
}
