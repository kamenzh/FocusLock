using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace LockService;

/// <summary>Local SAM and local WTS only. All writes revalidate the configured account.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAccountManager(AccountEnforcementSettings settings) : IAccountManager
{
    private const uint AccountDisable = 0x2;
    private const uint NormalAccount = 0x200;

    public AccountDetails? GetTargetAccount()
    {
        if (!AccountSafety.IsLocalUsername(settings.TargetUsername))
            throw new EnforcementException("Configure an unqualified local TargetUsername first.");
        RejectDomainController();
        var raw = ReadUser();
        if (raw is null) return null;
        using var identity = WindowsIdentity.GetCurrent();
        var serviceSid = identity.User ?? throw new EnforcementException("Cannot determine the service identity SID.");
        var sid = new SecurityIdentifier(raw.Value.Sid);
        var localSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, settings.TargetUsername)
            .Translate(typeof(SecurityIdentifier));
        return new(raw.Value.Name, sid.Value, sid.Equals(localSid) && (raw.Value.Flags & NormalAccount) != 0,
            (raw.Value.Flags & AccountDisable) == 0, IsAdministrator(),
            !AccountSafety.IsOrdinaryAccountSid(sid.Value), sid.Equals(serviceSid));
    }

    public void DisableTarget(string expectedSid) => SetEnabled(expectedSid, enabled: false);
    public void EnableTarget(string expectedSid) => SetEnabled(expectedSid, enabled: true);

    private void SetEnabled(string expectedSid, bool enabled)
    {
        RequireRealEnforcement();
        var account = AccountSafety.Validate(settings, GetTargetAccount(), requireEnabled: !enabled, expectedSid);
        if (account.Enabled == enabled) return; // Idempotent enable.
        // Read flags again to preserve all unrelated flags, and check the SID again before the write.
        var raw = ReadUser() ?? throw new EnforcementException("Target disappeared before account update.");
        if (raw.Sid != expectedSid || (!enabled && (raw.Flags & AccountDisable) != 0))
            throw new EnforcementException("Target identity or enabled state changed before account update.");
        var flags = new Native.UserFlags { Flags = enabled ? raw.Flags & ~AccountDisable : raw.Flags | AccountDisable };
        Check(Native.NetUserSetInfo(null, settings.TargetUsername, 1008, ref flags, out _), "NetUserSetInfo");
    }

    public IReadOnlyList<AccountSession> GetInteractiveSessions()
    {
        if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw LastError("WTSEnumerateSessions");
        try
        {
            var sessions = new List<AccountSession>();
            var size = Marshal.SizeOf<Native.SessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var session = Marshal.PtrToStructure<Native.SessionInfo>(IntPtr.Add(buffer, index * size));
                if (session.SessionId <= 0) continue;
                var sid = SessionSid(session.SessionId);
                if (sid is not null) sessions.Add(new(session.SessionId, sid));
            }
            return sessions;
        }
        finally { Native.WTSFreeMemory(buffer); }
    }

    public void LogOffTargetSession(int sessionId, string expectedSid)
    {
        RequireRealEnforcement();
        var account = AccountSafety.Validate(settings, GetTargetAccount(), requireEnabled: false, expectedSid);
        if (account.Enabled) throw new EnforcementException("Refusing logoff: target is not disabled.");
        // Session IDs can be reused. Resolve identity again immediately before logoff.
        if (sessionId <= 0 || SessionSid(sessionId) != expectedSid)
            throw new EnforcementException("Refusing logoff: session no longer belongs to the configured target.");
        if (!Native.WTSLogoffSession(IntPtr.Zero, sessionId, false)) throw LastError("WTSLogoffSession");
    }

    private void RequireRealEnforcement()
    {
        if (!settings.Enabled || settings.DryRun)
            throw new EnforcementException("Account mutation is disabled by configuration.");
    }

    private (string Name, string Sid, uint Flags)? ReadUser()
    {
        var code = Native.NetUserGetInfo(null, settings.TargetUsername, 23, out var buffer);
        try
        {
            if (code == 2221) return null; // NERR_UserNotFound
            Check(code, "NetUserGetInfo");
            var user = Marshal.PtrToStructure<Native.UserInfo23>(buffer);
            return (Marshal.PtrToStringUni(user.Name) ?? "", new SecurityIdentifier(user.Sid).Value, user.Flags);
        }
        finally { if (buffer != IntPtr.Zero) Native.NetApiBufferFree(buffer); }
    }

    private bool IsAdministrator()
    {
        // Resolve the localized group name from the well-known SID, never English text.
        var admin = (NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            .Translate(typeof(NTAccount));
        var adminName = admin.Value[(admin.Value.IndexOf('\\') + 1)..];
        var code = Native.NetUserGetLocalGroups(null, settings.TargetUsername, 0, 1,
            out var buffer, uint.MaxValue, out var read, out var total);
        try
        {
            Check(code, "NetUserGetLocalGroups"); // Includes indirect membership; partial results are refused.
            if (read != total) throw new EnforcementException("Cannot establish complete administrator membership.");
            for (var index = 0; index < read; index++)
            {
                var group = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, checked((int)index * IntPtr.Size)));
                if (string.Equals(group, adminName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { if (buffer != IntPtr.Zero) Native.NetApiBufferFree(buffer); }
    }

    private static void RejectDomainController()
    {
        var code = Native.NetServerGetInfo(null, 101, out var buffer);
        try
        {
            Check(code, "NetServerGetInfo");
            var server = Marshal.PtrToStructure<Native.ServerInfo101>(buffer);
            if ((server.Type & 0x18) != 0)
                throw new EnforcementException("Domain controllers are unsupported; a local Windows SAM account is required.");
        }
        finally { if (buffer != IntPtr.Zero) Native.NetApiBufferFree(buffer); }
    }

    private static string? SessionSid(int sessionId)
    {
        var username = QuerySessionString(sessionId, 5); // WTSUserName
        if (string.IsNullOrEmpty(username)) return null;
        var domain = QuerySessionString(sessionId, 7); // WTSDomainName
        if (!string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return null;
        return ((SecurityIdentifier)new NTAccount(domain, username).Translate(typeof(SecurityIdentifier))).Value;
    }

    private static string QuerySessionString(int sessionId, int infoClass)
    {
        if (!Native.WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            throw LastError("WTSQuerySessionInformation");
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { Native.WTSFreeMemory(buffer); }
    }

    private static void Check(uint code, string operation)
    {
        if (code != 0) throw new Win32Exception((int)code, $"{operation} failed (Windows error {code}).");
    }

    private static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastWin32Error(), $"{operation} failed.");

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct UserInfo23 { public IntPtr Name, FullName, Comment; public uint Flags; public IntPtr Sid; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct UserFlags { public uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ServerInfo101
        {
            public uint Platform; public IntPtr Name; public uint Major, Minor, Type; public IntPtr Comment;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SessionInfo { public int SessionId; public IntPtr StationName; public int State; }

        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint NetUserGetInfo(string? server, string username, uint level, out IntPtr buffer);
        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint NetUserSetInfo(string? server, string username, uint level, ref UserFlags buffer, out uint error);
        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint NetUserGetLocalGroups(string? server, string username, uint level, uint flags,
            out IntPtr buffer, uint preferredLength, out uint read, out uint total);
        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint NetServerGetInfo(string? server, uint level, out IntPtr buffer);
        [DllImport("Netapi32.dll")]
        internal static extern uint NetApiBufferFree(IntPtr buffer);
        [DllImport("Wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr buffer, out int count);
        [DllImport("Wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("Wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSLogoffSession(IntPtr server, int session, [MarshalAs(UnmanagedType.Bool)] bool wait);
        [DllImport("Wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr buffer);
    }
}
