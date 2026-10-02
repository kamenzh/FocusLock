# FocusLock — Phase 3 development build

FocusLock extends the existing .NET 9 `BrotherPCControl.sln`: `LockService`,
`LockController`, `Shared`, and `LockService.Tests`. Phase 3 adds authenticated
commands, replay protection, and administrator-controlled early unlock. Existing
UTC persistence, configured Standard-account enforcement, and administrator
recovery remain available.

The service **only listens on `http://127.0.0.1:42831`**. Both applications reject
other hosts/addresses in this phase. No firewall rules or LAN listeners are added.
LAN deployment comes after authentication has been tested, with transport security
and service installation/provisioning addressed in Phase 4.

## Architecture

- **Shared** contains public DTOs, canonical HMAC encoding, `ISecretStore`, the
  Windows DPAPI implementation, and `FocusLockApiClient`. DTOs contain no keys.
- **LockService** runs the ASP.NET Core API and periodic expiration worker.
  `IRequestAuthenticator` checks signatures using `IClock` and `INonceStore`,
  before the endpoint binds the body or changes state. Lock, unlock, startup
  recovery, and expiration share the existing `LockManager` semaphore.
- **LockController** signs every protected request, polls asynchronously about
  once per second, and distinguishes Authentication error, Disconnected, and
  API error. Commands require confirmation and cannot overlap in the UI.
- **LockService.Tests** exercises persistence, account safety using fake accounts,
  cryptography, replay, real in-process HTTP routing, the shared client, DPAPI,
  auditing, and lock/unlock/expiration races. Tests do not disable real accounts.

`IRestrictionPolicy` evaluates a `RestrictionDecision` independently of HTTP.
`ManualLockPolicy` implements current timers; `RestrictionKind` reserves manual
lock, daily allowance, and scheduled restriction. Early unlock checks whether
another policy would still restrict access after removing the manual timer.
Daily usage tracking and schedules are not implemented.

## Build and test

Use Windows and the .NET 9 SDK/desktop runtime. Close development service and
controller processes before building their binaries. From the repository root:

```powershell
dotnet restore BrotherPCControl.sln
dotnet build BrotherPCControl.sln
dotnet test BrotherPCControl.sln
```

## Generate and protect the shared secret

Run setup from the separate **local administrator account** that will run the
service and controller in development. Possession of this key authorizes both
lock and early unlock; do not provision it to the restricted Standard account.
HMAC proves possession of the key, not membership of a Windows group.

The exact generation command is:

```powershell
.\scripts\Generate-SharedSecret.ps1
```

This outputs base64 representing 32 cryptographically random bytes. Prefer the
following pipeline to generate one key and protect both local copies without
printing the key or placing its literal value in PowerShell command history:

```powershell
.\scripts\Generate-SharedSecret.ps1 | .\scripts\Set-SharedSecret.ps1 -For Both
```

`Set-SharedSecret.ps1` uses DPAPI **CurrentUser** with application-specific entropy.
Its dedicated Secrets directory permits its owner, SYSTEM, and Administrators;
plaintext key files are never written. The service and controller must run under
the Windows identity that protected their respective copies. Elevating a terminal
for the same Windows user does not change that identity. A different user cannot
simply copy and decrypt these DPAPI files.

Default protected copies:

| Application | Configuration | Default path |
| --- | --- | --- |
| Service | `Authentication:SecretFile` | `%LOCALAPPDATA%\FocusLock\Secrets\service.secret.dpapi` |
| Controller | `SecretFile` | `%LOCALAPPDATA%\FocusLock\Secrets\controller.secret.dpapi` |

For an already generated key held in a PowerShell variable, use:

```powershell
$focusLockSecret | .\scripts\Set-SharedSecret.ps1 -For Service
$focusLockSecret | .\scripts\Set-SharedSecret.ps1 -For Controller
Remove-Variable focusLockSecret
```

These must be copies of the **same** key; generating a new key independently for
each application will fail authentication. For separate Windows identities,
securely provision the same key and run each import under its intended identity.
Do not pass literal keys on command lines, commit them, or put them in appsettings.
`-SecretDirectory` supports a dedicated alternative folder; configure each
application's `SecretFile` to match. Do not point it at a general-purpose directory,
since the script restricts the directory ACL. DPAPI files and secret config files
are ignored by Git. The repository contains only paths, never a real key.

Missing, unreadable, or mismatched secrets make protected requests fail. The
controller displays **Authentication error**. Minimal health and existing timer
expiration/account restoration remain available even if the secret is missing.
To rotate the key, stop both applications and provision matching fresh copies.
Keep the state and nonce cache; key rotation must not discard account ownership.

## Configuration and local startup

Service settings are `Service:ListenAddress` (`127.0.0.1`), `Service:Port` (`42831`),
`Service:StateDirectory` (normally `%ProgramData%\FocusLock`), and the secret path
above. An ignored `appsettings.Development.json` may override the state directory.
Controller settings are top-level JSON properties: `TargetHostname` (`127.0.0.1`), `Port`
(`42831`), and `SecretFile`. Environment variables use double underscores, such as
`Authentication__SecretFile` and `Service__StateDirectory`.

**The current repository's service configuration enables real enforcement for
`FocusLockTest`, with recovery administrator `msi`. This existing configuration
was preserved.** The settings class defaults remain disabled enforcement and
DryRun. Explicitly select the intended mode before starting the service:

- `Enabled=false`: timer only, without account inspection or mutation for new locks.
- `Enabled=true`, `DryRun=true`: validate the configured target and report intended
  disable/signout/enable actions, without performing them.
- `Enabled=true`, `DryRun=false`: real enforcement, requiring elevation and a
  safely configured disposable Standard account.

For the first authenticated test, start a **separate timer-only instance** using
a dedicated state directory. Stop any development instance already using 42831.
Do not abandon an outstanding real lock: expire, unlock, or recover it first.
After generating both protected secret copies, run:

```powershell
dotnet run --project LockService --launch-profile LockService -- --Service:StateDirectory="$env:LOCALAPPDATA\FocusLock-Phase3" --AccountEnforcement:Enabled=false --AccountEnforcement:DryRun=true
```

From another terminal under the same administrator identity:

```powershell
dotnet run --project LockController
```

These commands use the default protected secret paths. Check that the controller
shows **Connected**, **UNLOCKED**, and **timer only** before testing. Closing the
controller does not stop the service. Stop the service console using Ctrl+C.

## Exact manual authentication, lock, and unlock checks

1. Run the timer-only service and controller commands above. Open
   `http://127.0.0.1:42831/api/health`; expect only `{"status":"ok"}`.
2. An unsigned request must fail:
   `Invoke-RestMethod http://127.0.0.1:42831/api/status` returns HTTP 401 with
   `Authentication failed.` It must not expose status or change the timer.
3. Choose 15, 30, 60, or 120 minutes and check that the custom minutes field follows
   the preset. Try 0, 721, and non-numeric input; these must not create locks.
4. Enter 2 minutes, press **LOCK PC**, and cancel. State must remain UNLOCKED.
   Repeat and confirm: expect LOCKED, a decreasing HH:MM:SS countdown, and a local
   unlock time. No account changes occur in timer-only mode.
5. While locked, press **UNLOCK NOW**. The dialog names the configured target
   (for example `Unlock FocusLockTest now?`) and warns:
   `This will end the active FocusLock restriction early.` Choose **Cancel**;
   the deadline and active lock must remain unchanged.
6. Press **UNLOCK NOW** again and choose **Unlock**. Expect UNLOCKED on success,
   no active deadline, and the button disabled. No target-user password is needed.
   Escape and closing the confirmation dialog also cancel.
7. Create a 2-minute lock, record the unlock time, stop only LockService, and
   observe Disconnected. Restart the identical service command promptly. Expect
   Connected with the original deadline, not a reset duration. Early unlock it.
8. Create a 1-minute lock and let it expire; expect UNLOCKED. Repeat, stop the
   service, wait past the deadline, then restart: expect UNLOCKED immediately.
9. To check Authentication error, stop the controller and temporarily configure
   its `SecretFile` to a nonexistent path. Restart it: expect
   Authentication error while the public health URL still succeeds. Restore the
   original path and restart; do not overwrite the service key for this test.
10. Run the read-only replay check below. Inspect the structured audit log for
    successful lock/unlock, authentication rejection, replay, and expiration.

```powershell
.\scripts\Test-ReplayProtection.ps1
Get-Content "$env:LOCALAPPDATA\FocusLock-Phase3\audit\security-audit.jsonl" -Tail 20
```

The replay script decrypts the controller copy, signs one GET `/api/status`, and
sends exactly the same signed request twice. Expect HTTP 200 followed by HTTP 401.
It performs no lock, unlock, or account changes. Optional `-SecretFile` and `-Port`
match custom local settings. Automated tests additionally check altered bodies,
durations, paths, methods, headers, signatures, past/future timestamps, concurrent
replays, and replay detection after service restart.

## Disposable-account enforcement and early unlock

Keep a separate administrator session open. Use only a disposable Standard
account, initially `FocusLockTest`. Do not test your real target account or a
reboot yet; automatic Windows Service startup belongs to Phase 4. Stopping the
development service while the account is disabled prevents expiration from
restoring it until the process runs again or an administrator recovers it.

To create a disposable account if it does not already exist, use **64-bit Windows
PowerShell as Administrator**:

```powershell
$password = Read-Host 'Password for disposable FocusLockTest' -AsSecureString
New-LocalUser -Name 'FocusLockTest' -Password $password -Description 'Disposable FocusLock test account'
Add-LocalGroupMember -SID 'S-1-5-32-545' -Member "$env:COMPUTERNAME\FocusLockTest"
Get-LocalUser -Name FocusLockTest | Select-Object Name, Enabled
```

Inspect an existing account before using it. Do not add the target to
Administrators. Configure `AccountEnforcement` in `LockService/appsettings.json`
with `Enabled=true`, `TargetUsername="FocusLockTest"`, your separate local
administrator's name in `RecoveryAdminUsername`, and initially `DryRun=true`.
Keep configuration/state writable only by trusted administrators/the service
identity. Do not change target, mode, or state directory during a real lock.
Prefer configured account names over command-line overrides so recovery reads
the same configuration. Never commit secrets with these settings.

After finishing the timer-only checks, stop that instance. For DryRun testing,
start from an elevated terminal under the identity that provisioned the key:

```powershell
dotnet run --project LockService --launch-profile LockService -- --Service:StateDirectory="$env:LOCALAPPDATA\FocusLock-Phase3-Enforcement" --AccountEnforcement:Enabled=true --AccountEnforcement:DryRun=true
```

Use the controller for a 1-minute lock and an early unlock. Confirm the UI says
DryRun and FocusLockTest; `Get-LocalUser -Name FocusLockTest` must remain enabled,
with no signouts. For real testing, finish the timer and stop the process; ensure
the configured account settings also have `Enabled=true` and `DryRun=false`, then:

```powershell
dotnet run --project LockService --launch-profile LockService -- --Service:StateDirectory="$env:LOCALAPPDATA\FocusLock-Phase3-Enforcement" --AccountEnforcement:Enabled=true --AccountEnforcement:DryRun=false
```

If testing logout, sign in once to FocusLockTest with only disposable work, then
switch back to the administrator session. Confirm the controller says real
enforcement and FocusLockTest. Create a 2-minute lock and accept the warning:
only that account should become disabled and only its sessions signed out.
Cancel **UNLOCK NOW** once and check that it remains disabled. Confirm **Unlock**
on the next attempt, then verify `Get-LocalUser -Name FocusLockTest` reports
`Enabled=True`, the UI is UNLOCKED, and state no longer records an active lock.
Create a 1-minute lock and let expiration restore it too. Audit output must show
AccountDisabled and AccountEnabled in addition to the corresponding command or
expiration events. Other accounts must remain unchanged.

For restart persistence, use the same state directory/configuration and promptly
restart the service during an active lock. The original deadline must remain.
If restarting after expiration, the verified owned account is restored before
state is cleared. Do not reboot for this development test.

## Administrator recovery remains available

Stop the development process with Ctrl+C. From the same separate administrator
account, in an elevated terminal, run (using the **actual active state directory**):

```powershell
.\scripts\Recover-TargetAccount.ps1 -Environment Development -StateDirectory "$env:LOCALAPPDATA\FocusLock-Phase3-Enforcement"
Get-LocalUser -Name FocusLockTest | Select-Object Name, Enabled
```

The recovery script requires elevation and reads the configured target; it accepts
no arbitrary username. It refuses administrator/built-in/system accounts, stops
an installed FocusLock/LockService if present, and requires the development
process to have released its state-directory lease. It enables only the validated
configured account, verifies the result, archives state as
`state.recovered.<UTC timestamp>.json`, and leaves the service stopped for review.
It does not depend on HMAC or the protected secret.

For normal production-path configuration use `-Environment Production` without a
state-directory override. If you used a custom directory, supply that exact path.
The script reads base/environment JSON and environment-variable overrides, not
another process's command-line account-name overrides. A username/SID mismatch
requires administrator review and restoring the original configuration. Corrupt
JSON can be recovered using the validated configured account and archived.
Never delete active enforced state instead of restoring the account. After tests,
recover outstanding locks and restore disabled enforcement/DryRun if desired.

## HMAC wire format and replay protection

Protected endpoints: GET `/api/status`, POST `/api/lock`, POST `/api/unlock`.
Only GET `/api/health` is public, exposing no account, timer, or configuration data.
Lock accepts only `{ "durationMinutes": 1 }`, with integer durations 1–720.
Status and unlock require an empty body; unlock never accepts an account identifier.
Unknown lock properties, including usernames, are rejected. Query strings are
rejected for protected requests. Clients use the literal paths shown above.

Each request supplies:

- `X-FocusLock-Timestamp`: canonical decimal UTC Unix seconds.
- `X-FocusLock-Nonce`: 32 random bytes as 64 lowercase hexadecimal characters.
- `X-FocusLock-Signature`: base64 HMAC-SHA256 of the canonical bytes below.

Canonical encoding is UTF-8 **without BOM**, six lines separated by a single LF
(`\n`), **without a trailing newline**:

```text
FocusLock-HMAC-SHA256-v1
HTTP METHOD IN UPPERCASE
exact request path
X-FocusLock-Timestamp
X-FocusLock-Nonce
lowercase hexadecimal SHA256 of the exact transmitted body bytes
```

The controller serializes the body once, signs those bytes, and sends the same
bytes. GET/status and POST/unlock sign an empty body. The server independently
hashes the received body (maximum 4096 bytes), reconstructs the value, and uses
`CryptographicOperations.FixedTimeEquals` to compare authentication values.
No raw headers, signature, or secret are logged. All authentication rejection
responses are HTTP 401 with `Authentication failed.`; server diagnostics contain
only failure categories. Ordinary input errors are 400, enforcement refusals 409,
and persistence failures 503 with structured `ApiResult` responses.

Timestamps within +/-60 seconds are accepted, using injected `IClock`. A valid
signature reserves the nonce atomically before endpoint execution. Nonce digests
are retained through the last acceptable timestamp second, including future skew
(up to 121 seconds), and persisted in `nonce-cache.json` before accepting a request.
Thus restarting the service does not reopen an accepted nonce's validity window.
A worker prunes expired entries every 15 seconds; capacity is 4096 live entries.
A full, corrupt, or unavailable cache fails protected requests closed rather than
dropping unexpired entries. Clients use fresh nonces for every poll and command;
commands are not automatically retried after an uncertain network result.

HMAC authenticates requests; it does not encrypt HTTP or authenticate server
responses. Loopback restriction remains mandatory. LAN support requires a separate
transport-security and deployment review, not changing the hostname to a LAN IP.

## Persistence, account safety, and auditing

Timers use absolute UTC timestamps. State is written to a same-directory temp
file, flushed to disk, then moved over `state.json` with
`File.Move(temp, state, overwrite: true)` and cleanup in `finally`.
The state-directory lease prevents concurrent service/recovery writers.

Schema 1 remains timer-only, never implicitly becoming account enforcement.
Schema 2 records configured username/SID, disable intent, verified ownership,
simulation, and recovery flags. A real lock saves intent before disabling,
verifies the result, and saves ownership before session logout. Uncertain ownership
requires administrator recovery. Startup revalidates an active owned lock;
expiration and early unlock validate the configured identity and recorded SID,
enable only a proven owned account, verify enabled state, then clear the timer.
An already unlocked request is idempotent and does not inspect/enable accounts.
An account FocusLock did not disable is never enabled by early unlock.

Name/SID conflicts, interrupted disable intent, corrupt state, or unsafe mode
changes block early unlock and retain recovery information. Existing restrictions
against administrator/recovery/service/system accounts remain. Session logout
selects only matching local interactive sessions and rechecks ownership.
Corrupt state cannot cause account mutations. Timer-only replacement preserves
its recovery marker, preventing later enforcement from hiding the incident.

Structured JSONL audit output is at `<StateDirectory>\audit\security-audit.jsonl`:
UTC timestamp, event, and optional non-sensitive reason code. Events include
LockAccepted, UnlockAccepted, AuthenticationRejected, ReplayRejected,
AccountDisabled, AccountEnabled, and TimerExpired. Rotation keeps the current file
and one previous file at approximately 5 MiB each. Audit-write failure logs an
error without blocking account restoration. Protect this local diagnostic log;
it is not a tamper-proof external security ledger.

## Remaining Phase 4 work

- Install/run an automatically starting Windows Service with deliberate identity,
  permissions, recovery/startup behavior, and DPAPI provisioning for that identity.
- Test reboot/crash behavior using the disposable account and available recovery.
- Design secure LAN transport, server authentication, key provisioning/rotation,
  and deployment limits before enabling any remote listener or firewall rule.
- Implement daily allowance/usage tracking and scheduled policies separately.

No arbitrary commands/processes/usernames, administrator disabling, anti-uninstall,
Safe Mode/Windows Recovery restrictions, BitLocker/BIOS/UEFI changes, or security
software tampering are provided. Administrator recovery remains accessible.

## License

[MIT](LICENSE).
