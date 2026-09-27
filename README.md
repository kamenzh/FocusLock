# FocusLock — development timer preview

This phase records a lock timer only. It does **not** lock Windows, disable accounts,
log users off, alter security settings, create firewall rules, or execute commands.
The existing `BrotherPCControl.sln` and projects remain on .NET 9.

## Architecture

- **Shared** contains `LockRequest`, `LockStatus`, and `ApiResult` DTOs.
- **LockService** hosts an ASP.NET Core API and a `BackgroundService` that checks
  expiration once per second. It can run as a console process or under the Windows
  Service host lifetime. No service installation is required for this preview.
- **LockController** is a WPF client using one `HttpClient`, asynchronous requests,
  and a one-second polling timer. It reconnects automatically and cancels polling
  on close. A disconnected service is shown as UNKNOWN, never as unlocked.
- **LockService.Tests** tests the real JSON store with isolated temporary directories
  and an injected fake clock, so expiration tests do not wait for real minutes.

`LockManager` serializes state changes, writes successfully before accepting a lock,
and derives remaining time from an absolute UTC deadline. `IClock` and
`ILockStateStore` are injected. A new request replaces the deadline with the current
UTC time plus its duration, even if another timer is active.

The JSON file contains `schemaVersion`, `locked`, and `lockUntilUtc`. Writes use a
unique temporary file in the same directory, flush it to disk, then replace the
existing file atomically (or rename on first creation). Missing state starts unlocked;
expired state is cleared on startup; malformed or unsupported state is logged and
starts unlocked. Corrupt files are replaced on the next successful lock request.
Other disk/permission errors are surfaced rather than silently losing an active timer.

## Build and test

Use Windows with the .NET 9 SDK and desktop runtime. From the repository root:

```powershell
dotnet restore BrotherPCControl.sln
dotnet build BrotherPCControl.sln
dotnet test BrotherPCControl.sln
```

## Run the service

```powershell
dotnet run --project LockService --launch-profile LockService
```

The development launch profile selects `appsettings.Development.json`, which stores
state in `%LOCALAPPDATA%\FocusLock\state.json` without needing administrator rights.
The service logs its listener and responds at `http://127.0.0.1:42831`.

Normal non-development configuration uses `%ProgramData%\FocusLock\state.json`
(normally `C:\ProgramData\FocusLock\state.json`). The service identity must have
write permission to that directory. To run with normal configuration in a fresh shell:

```powershell
dotnet run --project LockService --no-launch-profile
```

Configure `Service:ListenAddress`, `Service:Port`, and `Service:StateDirectory` in
the service settings files, via environment variables such as `Service__Port`,
or command-line arguments. The state directory must be absolute after environment
variable expansion. For example:

```powershell
dotnet run --project LockService --launch-profile LockService -- --Service:StateDirectory="$env:LOCALAPPDATA\FocusLock-Dev"
```

Only loopback IP addresses are accepted. Defaults are `127.0.0.1` and port `42831`.
Additional `Kestrel:Endpoints` configuration is rejected; URL environment variables
do not replace the explicit loopback listener. Run one service process per state
directory. This preview has no authentication and is intended for local testing only.

## Run the controller

In a second terminal:

```powershell
dotnet run --project LockController
```

`LockController/appsettings.json` defines `TargetHostname` and `Port`, defaulting to
`127.0.0.1` and `42831`. It is copied beside the controller executable. Restart the
controller after changing configuration. Keep the target local for this phase.

## API

- `GET /api/health`: HTTP 200 with `{ "success": true, "message": "..." }`.
- `GET /api/status`: `locked`, `lockUntilUtc`, `remainingSeconds`, `machineName`.
- `POST /api/lock`: JSON `{ "durationMinutes": 30 }`; returns the saved status.
  Whole minutes from 1 through 720 are accepted. Invalid durations return HTTP 400;
  a persistence failure returns HTTP 503 without accepting the new timer.

```powershell
Invoke-RestMethod http://127.0.0.1:42831/api/health
Invoke-RestMethod http://127.0.0.1:42831/api/status
Invoke-RestMethod http://127.0.0.1:42831/api/lock -Method Post -ContentType application/json -Body '{"durationMinutes":1}'
```

## Exact manual verification

1. Start the service and controller using the development commands above. Verify
   health returns success and the controller shows Connected. A fresh state file
   starts UNLOCKED; an existing active timer is restored.
2. Click each preset and check the textbox becomes 15, 30, 60, or 120. Enter `0`,
   then click LOCK PC: validation must prevent a request. Repeat with `721` and text.
3. Set 30, click LOCK PC, and choose No. The state must remain unchanged. Repeat
   and choose Yes. Verify LOCKED, a decreasing HH:MM:SS countdown, and local unlock time.
4. Inspect `Get-Content "$env:LOCALAPPDATA\FocusLock\state.json"`. Confirm schema 1,
   `locked: true`, and a UTC deadline. Record the deadline.
5. Press Ctrl+C in the service terminal. Leave the controller open: within a few
   seconds it must show Disconnected and UNKNOWN with no stale countdown.
6. Restart the service with the same development command and state directory.
   Verify automatic reconnection, the identical deadline, and reduced remaining
   time rather than a fresh 30 minutes.
7. Enter 1 minute and confirm another lock. Wait for expiration: the UI must become
   UNLOCKED and the file must contain `locked: false` and `lockUntilUtc: null`.
8. Lock for 1 minute again, stop the service, wait over 60 seconds, then restart it.
   Verify startup clears the expired state and the UI shows UNLOCKED.
9. To test corruption, stop the service, then run
   `Set-Content "$env:LOCALAPPDATA\FocusLock\state.json" 'invalid json'`.
   Restart: verify an error is logged and status is UNLOCKED. Confirm a new lock
   works and repairs the file.
10. Close/reopen the controller and verify it obtains the current service state.
    Throughout these tests, the Windows desktop and accounts remain usable.

## Future phases and license

LAN authentication, account management, service installation, and deployment remain
future work. No Windows account restrictions or LAN access are implemented here.
FocusLock is licensed under the [MIT license](LICENSE).
