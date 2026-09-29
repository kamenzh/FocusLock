namespace LockService;

public sealed record AccountDetails(string Username, string Sid, bool IsLocal, bool Enabled,
    bool IsAdministrator, bool IsBuiltIn, bool IsServiceIdentity);

public sealed record AccountSession(int SessionId, string AccountSid);

// Implementations are bound to configuration. Mutation methods never accept a username.
public interface IAccountManager
{
    AccountDetails? GetTargetAccount();
    IReadOnlyList<AccountSession> GetInteractiveSessions();
    void DisableTarget(string expectedSid);
    void EnableTarget(string expectedSid);
    void LogOffTargetSession(int sessionId, string expectedSid);
}

public sealed class EnforcementException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class UnsupportedAccountManager : IAccountManager
{
    private static EnforcementException Error() => new("Account enforcement is supported only on Windows.");
    public AccountDetails? GetTargetAccount() => throw Error();
    public IReadOnlyList<AccountSession> GetInteractiveSessions() => throw Error();
    public void DisableTarget(string expectedSid) => throw Error();
    public void EnableTarget(string expectedSid) => throw Error();
    public void LogOffTargetSession(int sessionId, string expectedSid) => throw Error();
}

public static class AccountSafety
{
    public static bool IsLocalUsername(string? name) => !string.IsNullOrWhiteSpace(name)
        && name == name.Trim() && name.Length <= 20 && !name.EndsWith('.')
        && !name.Any(c => char.IsControl(c) || "\\/\"[]:|<>+=;,?*@".Contains(c));

    public static AccountDetails Validate(AccountEnforcementSettings settings, AccountDetails? account,
        bool requireEnabled, string? expectedSid = null)
    {
        if (!IsLocalUsername(settings.TargetUsername))
            throw new EnforcementException("Configure a single unqualified local TargetUsername first.");
        if (!string.IsNullOrEmpty(settings.RecoveryAdminUsername) && !IsLocalUsername(settings.RecoveryAdminUsername))
            throw new EnforcementException("RecoveryAdminUsername must be an unqualified local username.");
        if (account is null) throw new EnforcementException("Configured target account does not exist.");
        if (!string.Equals(account.Username, settings.TargetUsername, StringComparison.OrdinalIgnoreCase) || !account.IsLocal)
            throw new EnforcementException("Target must be the configured local Windows account.");
        if (account.IsAdministrator) throw new EnforcementException("Administrator accounts cannot be targeted.");
        if (string.Equals(account.Username, settings.RecoveryAdminUsername, StringComparison.OrdinalIgnoreCase))
            throw new EnforcementException("The recovery administrator cannot be targeted.");
        if (account.IsServiceIdentity) throw new EnforcementException("The service identity cannot be targeted.");
        if (account.IsBuiltIn || !IsOrdinaryAccountSid(account.Sid))
            throw new EnforcementException("Built-in and system accounts cannot be targeted.");
        if (expectedSid is not null && !string.Equals(account.Sid, expectedSid, StringComparison.Ordinal))
            throw new EnforcementException("Persisted target SID differs from the configured account. Administrator recovery required.");
        if (requireEnabled && !account.Enabled)
            throw new EnforcementException("Target is already disabled. FocusLock will not take ownership of it.");
        return account;
    }

    public static bool IsOrdinaryAccountSid(string sid)
    {
        var parts = sid.Split('-');
        return parts.Length == 8 && sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
            && parts.Skip(4).All(p => uint.TryParse(p, out _))
            && uint.TryParse(parts[^1], out var rid) && rid >= 1000;
    }
}
