# FocusLock — Phase 2 development build

FocusLock remains the existing .NET 9 solution: `LockService`, `LockController`,
`Shared`, and `LockService.Tests`. Phase 1 timers still work with the defaults.
Phase 2 adds opt-in enforcement of **one configured local Standard account**.

**First tests must use a disposable Standard account named `FocusLockTest`.
Do not configure your real target account yet. Keep a separate local administrator
session open. Do not test a reboot yet: reboot testing waits for Phase 4, when
LockService is installed as an automatically starting Windows Service.**

If you stop the development service process while the test account is disabled,
its timer cannot re-enable that account until the service runs again. Restart the
service with the same configuration/state directory, or use the administrator
recovery script below. Closing the controller does not stop the service.

## Architecture and boundaries

- **Shared**: duration-only requests, status/results, and confirmation wording.
- **LockService**: ASP.NET Core API, one-second expiration worker, dependency-injected
  clock, state store, and `IAccountManager`. `WindowsAccountManager` uses local SAM
  and WTS APIs through P/Invoke; no shell or command execution is exposed.
- **LockController**: asynchronous WPF polling, presets/custom minutes, current
  mode/target, explicit confirmation, and recovery-required status.
- **LockService.Tests**: existing Phase 1 tests plus fake-account enforcement tests
  and real JSON persistence tests. No test disables an actual Windows account.

The listener stays on `http://127.0.0.1:42831`. Non-loopback addresses and additional
Kestrel endpoints are refused; endpoint configuration reloads cannot add listeners.
This phase has no LAN access, HMAC, firewall changes, service installation, or
anti-uninstall/recovery behavior. Local callers can request a lock of the configured
test account; there is no authentication yet. Only the configured duration can be
sent over HTTP, and unknown request properties (including usernames) are rejected.

The service identifies the built-in Administrators group using `S-1-5-32-544`,
resolves its localized name, and checks direct/indirect group membership. It refuses
missing/nonlocal accounts, administrators, the recovery administrator, the service
identity, built-in/system accounts, and an account that is already disabled at the
start of a new lock. Domain controllers are unsupported. Mutating Windows methods
revalidate the configured identity and expected SID immediately before operations.
Session logout selects only local interactive sessions matching that SID, excludes
session zero, and checks session ownership again immediately before WTS logoff.

Windows interop references: [NetUserSetInfo](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/nf-lmaccess-netusersetinfo),
[USER_INFO_23](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/ns-lmaccess-user_info_23),
[NetUserGetLocalGroups](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/nf-lmaccess-netusergetlocalgroups),
and [WTSLogoffSession](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtslogoffsession).

## Build and test

Use Windows and the .NET 9 SDK/desktop runtime. Close existing development service
and controller processes before rebuilding their binaries. From the repository root:

```powershell
dotnet restore BrotherPCControl.sln
dotnet build BrotherPCControl.sln
dotnet test BrotherPCControl.sln
```

## Configuration

`LockService/appsettings.json` ships with:

```json
"AccountEnforcement": {
  "Enabled": false,
  "TargetUsername": "",
  "RecoveryAdminUsername": "",
  "DryRun": true
}
```

The blank target intentionally requires explicit administrator configuration. Use
unqualified local names, not `DOMAIN\user` or an email address. Account settings are
snapshotted on process startup; restart after changing them. Do not change target
identity, enforcement mode, or state directory during a real lock. A new request
cannot overwrite a pending or enforced lock; wait for expiration or recover it.

- `Enabled=false`: Phase 1 timer only; no account inspection or mutation for new timers.
- `Enabled=true`, `DryRun=true`: validate the target and enumerate its sessions; log
  exactly which disable/signout/expiration-enable actions WOULD happen. No account
  is disabled or enabled and nobody is signed out.
- `Enabled=true`, `DryRun=false`: real enforcement, requiring an elevated service
  process and a safely configured target.

Service settings remain `Service:ListenAddress`, `Port`, and `StateDirectory`.
The normal state path is `%ProgramData%\FocusLock\state.json`. An optional local
`appsettings.Development.json` can override it. The walkthrough explicitly chooses
`%LOCALAPPDATA%\FocusLock-Phase2` to avoid ambiguity. Environment variables use
names such as `Service__StateDirectory` or `AccountEnforcement__DryRun`; command-line
settings have higher priority. Prefer the documented JSON configuration for account
names so recovery reads the same target. Keep service config and the chosen state
directory writable only by trusted administrators/the service identity, not the target.

Controller settings in `LockController/appsettings.json` remain `TargetHostname`
and `Port`, defaulting to `127.0.0.1` and `42831`.

## Prepare a disposable Standard account

Open **64-bit Windows PowerShell as Administrator**, using a separate **local
administrator account**, and switch to this repository. Run these once:

```powershell
$password = Read-Host 'Password for disposable FocusLockTest' -AsSecureString
New-LocalUser -Name 'FocusLockTest' -Password $password -Description 'Disposable FocusLock Phase 2 test account'
Add-LocalGroupMember -SID 'S-1-5-32-545' -Member "$env:COMPUTERNAME\FocusLockTest"
Get-LocalUser -Name FocusLockTest | Select-Object Name, Enabled
```

Do not add it to Administrators. If that name already exists, inspect it before
using it; do not repurpose an account containing real work. Group SID `S-1-5-32-545`
selects the local Users group independently of the Windows display language.

Configure DryRun from that administrator session:

```powershell
$configPath = Join-Path (Get-Location) 'LockService\appsettings.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.AccountEnforcement.Enabled = $true
$config.AccountEnforcement.TargetUsername = 'FocusLockTest'
$config.AccountEnforcement.RecoveryAdminUsername = (Get-LocalUser -SID ([Security.Principal.WindowsIdentity]::GetCurrent().User)).Name
$config.AccountEnforcement.DryRun = $true
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8
```

Do not commit these enabled settings. Leave the shipped defaults disabled.

## Exact DryRun procedure

Start the service in the administrator terminal:

```powershell
dotnet run --project LockService --launch-profile LockService -- --Service:StateDirectory="$env:LOCALAPPDATA\FocusLock-Phase2"
```

In another terminal, start the controller:

```powershell
dotnet run --project LockController
```

Verify the UI says **DryRun** and **FocusLockTest**. Enter 1 minute, press LOCK PC,
and explicitly confirm. Alternatively, exercise the same duration-only API:

```powershell
Invoke-RestMethod http://127.0.0.1:42831/api/health
Invoke-RestMethod http://127.0.0.1:42831/api/lock -Method Post -ContentType application/json -Body '{"durationMinutes":1}'
Invoke-RestMethod http://127.0.0.1:42831/api/status
Get-LocalUser -Name FocusLockTest | Select-Object Name, Enabled
```

Expect `Enabled=True` throughout and no session signout. The service logs validation
success and `DryRun: WOULD ...` actions. If the test account has no interactive
session, there is no session to list. Wait for expiration and verify the UI becomes
UNLOCKED. As a negative check, pointing DryRun at your local administrator must be
refused with HTTP 409 and a validation error; restore `FocusLockTest` afterward.

## Exact real FocusLockTest procedure

Finish the DryRun timer, then stop its service with Ctrl+C. In the administrator
terminal, change only DryRun (retain the target/recovery names configured above):

```powershell
$configPath = Join-Path (Get-Location) 'LockService\appsettings.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.AccountEnforcement.DryRun = $false
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding UTF8
Get-LocalUser -Name FocusLockTest | Select-Object Name, Enabled
dotnet run --project LockService --launch-profile LockService -- --Service:StateDirectory="$env:LOCALAPPDATA\FocusLock-Phase2"
```

The account must initially be enabled. To test signout, sign in to the disposable
account once, leave only disposable work there, and switch back to your separate
administrator session. Keep the service and controller in that administrator session.

Confirm the controller says **real enforcement · FocusLockTest**. Select 1 minute,
press LOCK PC, read the warning that the account will be disabled and signed out and
unsaved work may be lost, then choose Yes. Choosing No must leave state unchanged.
The API equivalent, after you are ready for the target's session to be signed out, is:

```powershell
Invoke-RestMethod http://127.0.0.1:42831/api/lock -Method Post -ContentType application/json -Body '{"durationMinutes":1}'
Get-LocalUser -Name FocusLockTest | Select-Object Name, Enabled
```

Expect `Enabled=False`. Only FocusLockTest sessions may be signed out. After one
minute, run `Get-LocalUser -Name FocusLockTest` again: expect `Enabled=True`, an
UNLOCKED UI, and cleared active state. Logs show disable/enable verification.

For **process restart** testing only, start a longer test lock, record its deadline,
stop the development service, and promptly restart the identical command with the
same configuration. The original deadline must remain. If restarting after expiry,
the service restores the verified owned account before clearing state. **Do not
reboot for this test. Automatic service startup belongs to Phase 4.**

## Administrator recovery

The recovery script is intentionally accessible at `scripts/Recover-TargetAccount.ps1`.
From the **same local administrator account** used to run this walkthrough, stop the
development process with Ctrl+C, then run:

```powershell
.\scripts\Recover-TargetAccount.ps1 -Environment Development -StateDirectory "$env:LOCALAPPDATA\FocusLock-Phase2"
Get-LocalUser -Name FocusLockTest | Select-Object Name, Enabled
```

The script requires elevation, reads the configured username (there is no username
argument), refuses administrator/built-in/system accounts, and stops an installed
service named FocusLock or LockService if present. A state-directory lease detects
an unclosed development process; it refuses recovery until that process stops.
It enables only the validated configured account, verifies enabled state, and moves
`state.json` to `state.recovered.<UTC timestamp>.json` in the same directory. This
both backs up the incident and removes the active timer so restart cannot replay it.
Every action is printed. It leaves the service stopped for configuration review.

For normal production-path configuration, use `-Environment Production` without
`-StateDirectory`. If you used another state path on the service command line, pass
that exact absolute path to the recovery script. It reads the base and environment
JSON plus relevant environment-variable overrides; avoid account-name command-line
overrides because they are not available to a separate recovery process. A persisted
username/SID mismatch requires administrator review/restoring the original config;
the script will not guess which account to manipulate. Corrupt JSON can be recovered
using the validated configured account, with the original file archived afterward.

After testing, stop the service, recover any outstanding test lock, then restore
`Enabled=false`, `DryRun=true`, and an empty `TargetUsername` in configuration.
Never delete an active enforced state file as a substitute for restoring the account.

## Persistence and failure behavior

Timers always use absolute UTC deadlines, not a duration-long `Task.Delay`. State
writes serialize to a unique temp file in the state directory, flush it to disk,
then use `File.Move(temp, state, overwrite: true)`; temp cleanup runs in `finally`.
An exclusive `service.lock` handle prevents two service/recovery processes from
writing the same state directory. Windows closes the handle on process exit.

Legacy schema 1 state remains supported as timer-only state. It is never upgraded
into an enforced lock merely because enforcement was enabled. Schema 2 adds:
`targetUsername`, `targetSid`, `disablePending`, `accountDisabledByFocusLock`,
`simulatedEnforcement`, and `recoveryRequired`.

A real lock first saves `disablePending=true`, then disables and verifies the account,
then saves `accountDisabledByFocusLock=true` before requesting any session logoff.
If disable fails and the target is verifiably still enabled, the intent is rolled
back. No sessions are logged off on disable failure. If ownership is uncertain
(including a crash between disabling and saving success), the intent stays on disk
and automatic mutation stops with RECOVERY REQUIRED. This deliberately requires
administrator recovery rather than enabling an account without verified ownership.

Expiration revalidates the configured name and recorded SID, enables only a verified
owned account, verifies enabled state, then clears state. Re-enabling an already
enabled account is harmless and does not issue an unnecessary write. An enable or
identity validation failure retains state for recovery. Session logoff failure keeps
the timer/ownership so expiration can still restore access.

An active real lock restored on startup revalidates the account and ensures it is
disabled. An active simulated lock stays simulated even if DryRun is switched off.
Turning enforcement off or DryRun on while real ownership exists never enables or
disables automatically: recovery is required. Never change the target while locked.

Corrupt state never causes account mutations and blocks enforcement until recovery.
For Phase 1 compatibility, disabled-enforcement API clients can replace corrupt JSON
with a simulated timer, but a durable recovery marker prevents later enforcement
from hiding that incident. The UI prominently reports RECOVERY REQUIRED. SID details
stay out of the public status response; no credentials are logged.

## API and Phase 1 checks

- `GET /api/health`: reports the running service.
- `GET /api/status`: existing timer fields plus `enforcementEnabled`, `dryRun`,
  `targetAccountConfigured`, configured display name `targetUsername`,
  `recoveryRequired`, and an actionable `enforcementMessage`.
- `POST /api/lock`: only `{ "durationMinutes": 1 }`, from 1 through 720. Invalid
  input is HTTP 400; safety/enforcement failures are HTTP 409; disk errors are
  reported as errors rather than accepting an unpersisted lock.

With enforcement disabled, verify presets (15/30/60/120), invalid durations (0/721/
text), cancellation, countdown, disconnect/reconnect, identical deadlines across
process restarts, and expiration while stopped. Keep corruption experiments in a
separate timer-only directory; use recovery rather than editing real enforced state.

The program does not attempt to defeat administrators, prevent removal, interfere
with Safe Mode/Windows Recovery, change BitLocker/BIOS/UEFI, block recovery media,
or tamper with security software. Administrator recovery remains available.

## License

[MIT](LICENSE).
