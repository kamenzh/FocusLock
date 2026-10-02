#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SecretFile = (Join-Path $env:LOCALAPPDATA 'FocusLock\Secrets\controller.secret.dpapi'),
    [ValidateRange(1,65535)][int]$Port = 42831
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$key = $null
$hmac = $null
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $entropy = [Text.Encoding]::UTF8.GetBytes('FocusLock.SharedSecret.v1')
    $key = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes([Environment]::ExpandEnvironmentVariables($SecretFile)), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ($key.Length -ne 32) { throw 'Invalid protected secret.' }
    $nonceBytes = New-Object byte[] 32
    $rng.GetBytes($nonceBytes)
    $nonce = ([BitConverter]::ToString($nonceBytes)).Replace('-', '').ToLowerInvariant()
    $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString([Globalization.CultureInfo]::InvariantCulture)
    # SHA256 of the empty body. UTF-8, LF separators, no trailing newline.
    $bodyHash = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
    $canonical = "FocusLock-HMAC-SHA256-v1`nGET`n/api/status`n$timestamp`n$nonce`n$bodyHash"
    $hmac = New-Object Security.Cryptography.HMACSHA256
    $hmac.Key = $key
    $headers = @{
        'X-FocusLock-Timestamp' = $timestamp
        'X-FocusLock-Nonce' = $nonce
        'X-FocusLock-Signature' = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical)))
    }
    $uri = "http://127.0.0.1:$Port/api/status"
    $first = Invoke-WebRequest $uri -Headers $headers -UseBasicParsing
    if ($first.StatusCode -ne 200) { throw 'The first authenticated status request failed.' }
    Write-Host 'First authenticated GET /api/status: HTTP 200'
    try {
        Invoke-WebRequest $uri -Headers $headers -UseBasicParsing | Out-Null
        throw 'Replay was unexpectedly accepted.'
    } catch {
        if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 401) { throw }
        Write-Host 'Identical replay: HTTP 401. Replay protection passed. No accounts were changed.'
    }
} finally {
    if ($key) { [Array]::Clear($key, 0, $key.Length) }
    if ($hmac) { $hmac.Dispose() }
    $rng.Dispose()
}
