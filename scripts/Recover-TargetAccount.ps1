#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ConfigurationPath = (Join-Path $PSScriptRoot '..\LockService\appsettings.json'),
    [ValidateSet('Development', 'Production')][string]$Environment = 'Development',
    [string]$StateDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Recovery reads data only. It never evaluates configuration as PowerShell code.
$configFile = (Resolve-Path -LiteralPath $ConfigurationPath).Path
$config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
$target = [string]$config.AccountEnforcement.TargetUsername
$recovery = [string]$config.AccountEnforcement.RecoveryAdminUsername
$directory = [string]$config.Service.StateDirectory
$overrideFile = Join-Path (Split-Path $configFile -Parent) "appsettings.$Environment.json"
if (Test-Path -LiteralPath $overrideFile) {
    $overrides = Get-Content -LiteralPath $overrideFile -Raw | ConvertFrom-Json
    if ($overrides.PSObject.Properties['AccountEnforcement']) {
        if ($overrides.AccountEnforcement.PSObject.Properties['TargetUsername']) { $target = [string]$overrides.AccountEnforcement.TargetUsername }
        if ($overrides.AccountEnforcement.PSObject.Properties['RecoveryAdminUsername']) { $recovery = [string]$overrides.AccountEnforcement.RecoveryAdminUsername }
    }
    if ($overrides.PSObject.Properties['Service'] -and $overrides.Service.PSObject.Properties['StateDirectory']) {
        $directory = [string]$overrides.Service.StateDirectory
    }
}
# Match standard service environment overrides. Command-line state overrides must be supplied here too.
if (Test-Path Env:AccountEnforcement__TargetUsername) { $target = $env:AccountEnforcement__TargetUsername }
if (Test-Path Env:AccountEnforcement__RecoveryAdminUsername) { $recovery = $env:AccountEnforcement__RecoveryAdminUsername }
if (Test-Path Env:Service__StateDirectory) { $directory = $env:Service__StateDirectory }
if ($StateDirectory) { $directory = $StateDirectory }
$directory = [Environment]::ExpandEnvironmentVariables($directory)
if (-not [IO.Path]::IsPathRooted($directory)) { throw 'StateDirectory must be an absolute path.' }
$directory = [IO.Path]::GetFullPath($directory)
if ([string]::IsNullOrWhiteSpace($target) -or $target -ne $target.Trim() -or $target.Length -gt 20 -or
    $target.EndsWith('.') -or $target -match '[\\/"\[\]:|<>+=;,?*@\x00-\x1f]') {
    throw 'Configure a single unqualified local TargetUsername before recovery.'
}
if ($target -eq $recovery) { throw 'Refusing to operate on the configured recovery administrator.' }

# Include indirect local group membership and resolve the localized administrators name by SID.
if (-not ('FocusLockRecoveryGroups' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class FocusLockRecoveryGroups {
    [DllImport("Netapi32.dll", CharSet=CharSet.Unicode)]
    static extern uint NetUserGetLocalGroups(string server, string user, uint level, uint flags,
        out IntPtr buffer, uint length, out uint read, out uint total);
    [DllImport("Netapi32.dll")] static extern uint NetApiBufferFree(IntPtr buffer);
    public static bool IsAdministrator(string user) {
        var admin = ((NTAccount)new SecurityIdentifier("S-1-5-32-544").Translate(typeof(NTAccount))).Value;
        admin = admin.Substring(admin.IndexOf('\\') + 1);
        IntPtr buffer;
        uint read, total;
        uint code = NetUserGetLocalGroups(null, user, 0, 1, out buffer, uint.MaxValue, out read, out total);
        try {
            if (code != 0) throw new Win32Exception((int)code);
            if (read != total) throw new InvalidOperationException("Incomplete group membership results.");
            for (int i = 0; i < read; i++) {
                string group = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                if (string.Equals(group, admin, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        } finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
    }
}
"@
}

function Get-ValidatedTarget {
    $account = Get-LocalUser -Name $target -ErrorAction Stop
    if ($account.PrincipalSource -ne 'Local') { throw 'Only a local SAM account can be recovered.' }
    $sid = $account.SID.Value
    if ($sid -notmatch '^S-1-5-21-\d+-\d+-\d+-(\d+)$' -or [uint32]$Matches[1] -lt 1000) {
        throw 'Built-in and system accounts cannot be recovered by this script.'
    }
    if ($sid -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Refusing to modify the current administrator identity.' }
    if ([FocusLockRecoveryGroups]::IsAdministrator($target)) { throw 'Refusing to operate on an administrator account.' }
    return $account
}

$initial = Get-ValidatedTarget
Write-Host "Configuration: $configFile ($Environment)"
Write-Host "Configured local target: $target"
Write-Host "State directory: $directory"
Write-Host 'Only this configured Standard account will be enabled. No sessions will be signed out.'
if (-not $PSCmdlet.ShouldProcess($target, 'Stop FocusLock service, enable configured target, and archive active state')) { return }

foreach ($serviceName in @('FocusLock', 'LockService')) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping installed service $serviceName"
        Stop-Service -Name $serviceName -ErrorAction Stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

New-Item -ItemType Directory -Path $directory -Force | Out-Null
$lease = $null
try {
    try {
        $lease = [IO.File]::Open((Join-Path $directory 'service.lock'), [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        throw 'State directory is in use. Stop the development LockService console with Ctrl+C, then run recovery again. No account was changed.'
    }
    $account = Get-ValidatedTarget
    if ($account.SID.Value -ne $initial.SID.Value) { throw 'Target SID changed during recovery. No account was changed.' }
    $stateFile = Join-Path $directory 'state.json'
    if (Test-Path -LiteralPath $stateFile) {
        $saved = $null
        try { $saved = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json }
        catch { Write-Host 'State is corrupt. Recovery will use only the validated configured account.' }
        if ($saved -and $saved.PSObject.Properties['targetUsername'] -and $saved.targetUsername -and $saved.targetUsername -ne $target) {
            throw 'Persisted username conflicts with configuration. Restore the original target configuration and review the state before recovery.'
        }
        if ($saved -and $saved.PSObject.Properties['targetSid'] -and $saved.targetSid -and $saved.targetSid -ne $account.SID.Value) {
            throw 'Persisted SID conflicts with the configured account. Manual administrator review is required.'
        }
    }
    if (-not $account.Enabled) {
        Write-Host "Enabling ONLY $target"
        Enable-LocalUser -SID $account.SID -ErrorAction Stop
    } else { Write-Host "$target is already enabled; no enable operation needed." }
    $verified = Get-LocalUser -SID $account.SID -ErrorAction Stop
    if (-not $verified.Enabled) { throw 'Account enable verification failed. State was retained.' }
    Write-Host "Verified $target is enabled."
    if (Test-Path -LiteralPath $stateFile) {
        $backup = Join-Path $directory ('state.recovered.' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
        # Both absolute paths are children of the explicitly configured state directory.
        if ([IO.Path]::GetDirectoryName($backup).TrimEnd('\') -ne $directory.TrimEnd('\')) { throw 'Invalid backup path.' }
        Write-Host "Archiving active state to $backup"
        Move-Item -LiteralPath $stateFile -Destination $backup -ErrorAction Stop
    } else { Write-Host 'No active state file exists.' }
    Write-Host 'Recovery completed. The service remains stopped. Review configuration before restarting it.'
} finally { if ($lease) { $lease.Dispose() } }
