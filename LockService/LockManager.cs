using System.ComponentModel;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Shared;

namespace LockService;

// One gate orders persistence, OS changes, HTTP requests, and expiration.
public sealed class LockManager
{
    private readonly IClock clock;
    private readonly ILockStateStore store;
    private readonly AccountEnforcementSettings settings;
    private readonly IAccountManager? accounts;
    private readonly ILogger<LockManager> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private LockState state = LockState.Unlocked;
    private bool recoveryRequired;
    private string? enforcementMessage;

    public LockManager(IClock clock, ILockStateStore store, AccountEnforcementSettings? settings = null,
        IAccountManager? accounts = null, ILogger<LockManager>? logger = null)
    {
        this.clock = clock;
        this.store = store;
        this.settings = settings ?? new();
        this.accounts = accounts;
        this.logger = logger ?? NullLogger<LockManager>.Instance;
    }

    private bool RealEnforcement => settings.Enabled && !settings.DryRun;
    private bool HasAccountObligation => state.AccountDisabledByFocusLock || state.DisablePending;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            state = await store.LoadAsync(cancellationToken);
            if (state.IsCorrupt || state.RecoveryRequired)
            {
                RequireRecovery("State is corrupt or marked for recovery; account operations are blocked.");
                return;
            }
            if (state.SchemaVersion == 2)
            {
                try
                {
                    var target = ValidatePersistedTarget();
                    if (state.DisablePending)
                    {
                        RequireRecovery("A disable attempt was interrupted before success was recorded. Ownership is uncertain.");
                        return;
                    }
                    if (HasAccountObligation && !RealEnforcement)
                    {
                        RequireRecovery("An enforced lock exists, but enforcement is disabled or DryRun is on. No account changes were made.");
                        return;
                    }
                    if (state.LockUntilUtc > clock.UtcNow && state.AccountDisabledByFocusLock)
                    {
                        if (target.Enabled)
                        {
                            await SaveAsync(state with { DisablePending = true, AccountDisabledByFocusLock = false });
                            await DisableAndRecordAsync();
                        }
                        try { await LogOffTargetSessionsAsync(); }
                        catch (EnforcementException) { /* Keep the expiration recovery path active. */ }
                    }
                    else if (state.LockUntilUtc > clock.UtcNow && state.SimulatedEnforcement)
                    {
                        // A dry-run timer can never become a real lock just because configuration changed.
                        enforcementMessage = "Restored a simulated lock; no account changes will be made for this timer.";
                        if (settings.Enabled && settings.DryRun) LogDryRun(target);
                    }
                }
                catch (Exception exception) when (IsAccountFailure(exception))
                {
                    RequireRecovery("Startup enforcement could not be safely completed.", exception);
                    return;
                }
            }
            await ExpireCoreAsync();
        }
        finally { gate.Release(); }
    }

    public async Task<LockStatus> LockAsync(int minutes, CancellationToken cancellationToken = default)
    {
        if (minutes is < 1 or > 720)
            throw new ArgumentOutOfRangeException(nameof(minutes), "Duration must be between 1 and 720 minutes.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.Enabled && recoveryRequired)
                throw new EnforcementException("Administrator recovery is required before another enforced lock.");
            if (HasAccountObligation)
                throw new EnforcementException("An enforced lock already exists. Wait for expiration or use administrator recovery.");
            if (!settings.Enabled)
            {
                // Preserve Phase 1 simulation, including corrupt-file replacement, but retain a durable
                // recovery marker so later enabling enforcement cannot hide a corrupt-state incident.
                await SaveAsync(new LockState(1, true, clock.UtcNow.AddMinutes(minutes))
                    { RecoveryRequired = recoveryRequired });
                return Status();
            }

            var target = ValidateTarget(requireEnabled: true);
            if (settings.DryRun) LogDryRun(target); // Validate session access before accepting the simulation.
            var next = new LockState(2, true, clock.UtcNow.AddMinutes(minutes))
            {
                TargetUsername = target.Username,
                TargetSid = target.Sid,
                DisablePending = !settings.DryRun,
                SimulatedEnforcement = settings.DryRun
            };
            // Once intent is durable, finish the transaction even if the HTTP client disconnects.
            await SaveAsync(next);
            if (RealEnforcement)
            {
                await DisableAndRecordAsync();
                await LogOffTargetSessionsAsync();
            }
            return Status();
        }
        catch (Exception exception) when (IsAccountFailure(exception))
        {
            logger.LogError(exception, "Lock enforcement failed for configured target {Target}", settings.TargetUsername);
            if (exception is EnforcementException) throw;
            throw new EnforcementException("Account validation or enforcement failed. Check the service log.", exception);
        }
        finally { gate.Release(); }
    }

    private async Task DisableAndRecordAsync()
    {
        try
        {
            var target = ValidatePersistedTarget(requireEnabled: true);
            logger.LogInformation("Disabling configured target {Target}", target.Username);
            accounts!.DisableTarget(target.Sid);
            if (ValidatePersistedTarget().Enabled)
                throw new EnforcementException("Disable verification failed: target is still enabled.");
            logger.LogInformation("Verified target {Target} is disabled", target.Username);
        }
        catch (Exception exception) when (IsAccountFailure(exception))
        {
            // Never infer ownership merely because the account is disabled after an error.
            // Roll back only when it is positively verified to still be enabled.
            try
            {
                if (ValidatePersistedTarget().Enabled)
                {
                    await SaveAsync(LockState.Unlocked);
                    logger.LogWarning("Failed disable rolled back; target remains enabled. No sessions were logged off");
                }
                else RequireRecovery("Disable outcome is uncertain; no sessions were logged off.", exception);
            }
            catch (Exception rollbackError) when (IsAccountFailure(rollbackError) || rollbackError is IOException)
            {
                RequireRecovery("Could not safely roll back the disable intent.", rollbackError);
            }
            throw new EnforcementException("Account disable failed. No sessions were logged off. Check status and service logs for recovery instructions.", exception);
        }

        try
        {
            await SaveAsync(state with { AccountDisabledByFocusLock = true, DisablePending = false });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RequireRecovery("Account was disabled, but success could not be persisted. Intent was retained; administrator recovery is required.", exception);
            throw new EnforcementException("Account disabled but state update failed. Administrator recovery required; no sessions were logged off.", exception);
        }
    }

    private Task LogOffTargetSessionsAsync()
    {
        try
        {
            var target = ValidatePersistedTarget();
            if (target.Enabled || !state.AccountDisabledByFocusLock || !RealEnforcement)
                throw new EnforcementException("Refusing session logoff without a verified enforced disable.");
            foreach (var session in accounts!.GetInteractiveSessions().Where(s => s.SessionId > 0 && s.AccountSid == target.Sid))
            {
                accounts.LogOffTargetSession(session.SessionId, target.Sid);
                logger.LogInformation("Requested logout of target {Target} interactive session {Session}", target.Username, session.SessionId);
            }
        }
        catch (Exception exception) when (IsAccountFailure(exception))
        {
            // Keep the recorded ownership and deadline so expiration can still restore access.
            enforcementMessage = "Target disabled, but session logout was incomplete. The expiration timer remains active.";
            logger.LogError(exception, "{Message}", enforcementMessage);
            throw new EnforcementException(enforcementMessage, exception);
        }
        return Task.CompletedTask;
    }

    private void LogDryRun(AccountDetails target)
    {
        logger.LogInformation("DryRun: WOULD disable {Target}, verify disabled, and enable it at expiration; no account changes will occur", target.Username);
        foreach (var session in accounts!.GetInteractiveSessions().Where(s => s.SessionId > 0 && s.AccountSid == target.Sid))
            logger.LogInformation("DryRun: WOULD log off {Target} interactive session {Session}", target.Username, session.SessionId);
        enforcementMessage = "DryRun: validation passed. No account or session changes are performed.";
    }

    private AccountDetails ValidateTarget(bool requireEnabled, string? expectedSid = null)
    {
        try
        {
            if (accounts is null) throw new EnforcementException("Windows account manager is unavailable.");
            var target = AccountSafety.Validate(settings, accounts.GetTargetAccount(), requireEnabled, expectedSid);
            logger.LogInformation("Target safety validation passed for {Target}", target.Username);
            return target;
        }
        catch (Exception exception) when (IsAccountFailure(exception))
        {
            logger.LogError(exception, "Target safety validation failed for {Target}", settings.TargetUsername);
            throw;
        }
    }

    private AccountDetails ValidatePersistedTarget(bool requireEnabled = false)
    {
        if (!string.Equals(state.TargetUsername, settings.TargetUsername, StringComparison.OrdinalIgnoreCase) || state.TargetSid is null)
            throw new EnforcementException("Persisted target username conflicts with configuration. Administrator recovery required.");
        return ValidateTarget(requireEnabled, state.TargetSid);
    }

    public async Task<LockStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return Status(); }
        finally { gate.Release(); }
    }

    public async Task ExpireAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { await ExpireCoreAsync(); }
        finally { gate.Release(); }
    }

    private async Task ExpireCoreAsync()
    {
        if (!state.Locked || state.LockUntilUtc > clock.UtcNow || (recoveryRequired && state.SchemaVersion == 2)) return;
        if (state.SchemaVersion == 2)
        {
            try
            {
                var target = ValidatePersistedTarget();
                if (state.DisablePending) throw new EnforcementException("Interrupted disable requires administrator recovery.");
                if (state.AccountDisabledByFocusLock)
                {
                    if (!RealEnforcement) throw new EnforcementException("Recovery requires real enforcement to be enabled or the administrator recovery script.");
                    if (!target.Enabled)
                    {
                        logger.LogInformation("Enabling configured target {Target} at expiration", target.Username);
                        accounts!.EnableTarget(target.Sid);
                    }
                    if (!ValidatePersistedTarget().Enabled) throw new EnforcementException("Enable verification failed.");
                    logger.LogInformation("Verified target {Target} is enabled", target.Username);
                }
                else if (state.SimulatedEnforcement)
                    logger.LogInformation("DryRun timer expired; no enable operation was performed for {Target}", target.Username);
            }
            catch (Exception exception) when (IsAccountFailure(exception))
            {
                RequireRecovery("Expiration could not safely restore the target. State retained for administrator recovery.", exception);
                return;
            }
        }
        await SaveAsync(LockState.Unlocked with { RecoveryRequired = recoveryRequired });
        if (!recoveryRequired) enforcementMessage = null;
        logger.LogInformation("Lock state cleared after expiration");
    }

    private async Task SaveAsync(LockState next)
    {
        await store.SaveAsync(next, CancellationToken.None);
        state = next;
    }

    private void RequireRecovery(string message, Exception? exception = null)
    {
        recoveryRequired = true;
        enforcementMessage = message + " Stop the service and use scripts/Recover-TargetAccount.ps1 from an administrator session.";
        logger.LogError(exception, "RECOVERY REQUIRED: {Message}", enforcementMessage);
    }

    private static bool IsAccountFailure(Exception exception) => exception is EnforcementException or Win32Exception
        or UnauthorizedAccessException or IdentityNotMappedException or PlatformNotSupportedException;

    private LockStatus Status()
    {
        var remaining = state.LockUntilUtc is { } until
            ? Math.Max(0, (long)Math.Ceiling((until - clock.UtcNow).TotalSeconds)) : 0;
        return new(remaining > 0, remaining > 0 ? state.LockUntilUtc : null, remaining, Environment.MachineName)
        {
            EnforcementEnabled = settings.Enabled,
            DryRun = settings.DryRun,
            TargetAccountConfigured = AccountSafety.IsLocalUsername(settings.TargetUsername),
            TargetUsername = AccountSafety.IsLocalUsername(settings.TargetUsername) ? settings.TargetUsername : null,
            RecoveryRequired = recoveryRequired,
            EnforcementMessage = enforcementMessage
        };
    }
}
