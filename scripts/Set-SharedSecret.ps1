#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)][string]$Base64Secret,
    [ValidateSet('Service', 'Controller', 'Both')][string]$For = 'Both',
    [string]$SecretDirectory = (Join-Path $env:LOCALAPPDATA 'FocusLock\Secrets')
)
process {
    $ErrorActionPreference = 'Stop'
    Add-Type -AssemblyName System.Security
    $key = $null
    try {
        try { $key = [Convert]::FromBase64String($Base64Secret) }
        catch { throw 'Input must be a base64-encoded 32-byte secret.' }
        if ($key.Length -ne 32) { throw 'FocusLock requires exactly 32 cryptographically random bytes.' }
        $directory = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($SecretDirectory))
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        # Restrict this dedicated secret directory to its Windows owner, SYSTEM and administrators.
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetOwner($owner)
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]'S-1-5-18', [Security.Principal.SecurityIdentifier]'S-1-5-32-544')) {
            $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $directory -AclObject $acl
        $entropy = [Text.Encoding]::UTF8.GetBytes('FocusLock.SharedSecret.v1')
        $protected = [Security.Cryptography.ProtectedData]::Protect($key, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $roles = if ($For -eq 'Both') { @('Service','Controller') } else { @($For) }
        foreach ($role in $roles) {
            $destination = Join-Path $directory ($role.ToLowerInvariant() + '.secret.dpapi')
            $temporary = $destination + '.tmp'
            try {
                [IO.File]::WriteAllBytes($temporary, $protected)
                Move-Item -LiteralPath $temporary -Destination $destination -Force
                Write-Host "Stored DPAPI CurrentUser copy: $destination"
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
        }
    } finally {
        if ($key) { [Array]::Clear($key, 0, $key.Length) }
        $Base64Secret = $null
    }
}
