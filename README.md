# FocusLock

FocusLock is a Windows 11 PC time-management project built with C# and .NET.

It is designed to let an administrator temporarily restrict access to a specific Standard Windows user account from another computer on the same local network.

The project is currently being developed in stages so that networking, timers, persistence, authentication, and Windows account management can each be tested safely before being combined.

## Project Structure

```text
FocusLock/
├── BrotherPCControl.sln
├── LockService/
├── LockController/
├── Shared/
└── README.md
```

### LockService

`LockService` runs on the computer being controlled.

Its responsibilities will include:

* receiving lock requests
* storing lock expiration times
* restoring active lock state after a restart
* automatically ending locks when their timer expires
* eventually managing the configured Windows Standard user account

### LockController

`LockController` is the desktop application used by the administrator.

It will provide controls such as:

* 15 minute lock
* 30 minute lock
* 1 hour lock
* 2 hour lock
* custom lock duration
* connection status
* remaining lock time
* lock expiration time

### Shared

`Shared` contains models and DTOs used by both the controller and service.

Examples include:

```text
LockRequest
LockStatus
ApiResult
```

## Requirements

* Windows 11
* .NET 9 SDK
* Visual Studio, Rider, VS Code, or another C# editor
* PowerShell or Windows Terminal

Check your installed .NET version with:

```powershell
dotnet --version
```

## Building

Clone the repository:

```powershell
git clone https://github.com/kamenzh/FocusLock.git
cd FocusLock
```

Restore packages:

```powershell
dotnet restore
```

Build the solution:

```powershell
dotnet build
```

If tests exist:

```powershell
dotnet test
```

## Running During Development

Start the service:

```powershell
dotnet run --project .\LockService\LockService.csproj
```

Then open another terminal and start the controller:

```powershell
dotnet run --project .\LockController\LockController.csproj
```

During early development, the service should only listen locally:

```text
127.0.0.1:42831
```

This allows the controller and service to be tested safely on the same computer before LAN access is enabled.

## Lock Timer Design

FocusLock uses an absolute UTC expiration time rather than relying on an in-memory countdown.

For example:

```text
Lock started:     14:00
Duration:         60 minutes
Lock expires:     15:00
```

The persisted state may look like:

```json
{
  "schemaVersion": 1,
  "locked": true,
  "lockUntilUtc": "2026-09-26T12:00:00Z"
}
```

If the computer restarts at 14:20, the service reads the saved expiration time after startup.

Because the expiration time is still 15:00, the remaining lock continues instead of resetting.

## Planned Architecture

```text
Administrator PC
┌──────────────────────┐
│ FocusLock Controller │
└──────────┬───────────┘
           │
           │ authenticated HTTPS
           │ local network
           ▼
Target Windows PC
┌──────────────────────┐
│ FocusLock Service    │
│                      │
│ Persistent timer     │
│ Authentication       │
│ Account management   │
└──────────────────────┘
```

## Development Roadmap

### Phase 1 — Controller and Timer

* WPF controller UI
* local HTTP API
* lock duration selection
* persistent UTC expiration time
* restart-safe lock state
* automated tests

### Phase 2 — Windows Account Management

* configure one target Standard user
* verify the account exists
* reject Administrator accounts
* disable the target account during a lock
* log out the target user's active session
* automatically re-enable the account when the timer expires

### Phase 3 — LAN Security

* HTTPS
* HMAC-SHA256 request authentication
* timestamp validation
* nonce/replay protection
* secure secret storage
* certificate validation

### Phase 4 — Windows Service

* install LockService as a Windows Service
* automatic startup
* service restart recovery
* Windows Firewall configuration

### Phase 5 — Deployment

* install LockService on the target PC
* install LockController on the administrator PC
* configure LAN addressing
* test restart persistence
* test recovery procedure

## Security Model

FocusLock is intended to restrict a **Standard Windows user account**.

The target user should not have administrator privileges.

A separate administrator account should always remain available for recovery.

FocusLock is not intended to prevent a Windows administrator from:

* stopping the service
* uninstalling the software
* enabling the restricted account manually
* using Windows Recovery
* reinstalling Windows

The software should never attempt to interfere with Windows recovery mechanisms, BitLocker recovery, BIOS/UEFI access, Safe Mode, external boot devices, security software, or administrator recovery.

## Safety

Before enabling real Windows account restrictions, test everything using a temporary Standard Windows account.

Never test account-disable functionality using your only administrator account.

Recommended setup:

```text
ParentAdmin     Administrator
Brother         Standard User
```

FocusLock should only ever manage the explicitly configured Standard user.

## Sensitive Files

Do not commit:

* shared authentication secrets
* HTTPS private keys
* `.pfx` certificates
* `.pem` private keys
* local configuration containing secrets
* runtime lock state

These files are excluded by `.gitignore`.

## Emergency Recovery

Once Windows account management is implemented, the project should include an administrator-only recovery script.

The recovery procedure should remain approximately:

```text
1. Sign in using the recovery Administrator account.
2. Stop the FocusLock service.
3. Enable the target Standard account.
4. Back up or remove the active FocusLock state file.
5. Restart the service if required.
```

FocusLock should never intentionally prevent an administrator from performing this recovery.

## License
MIT License

Copyright (c) 2026 Камен Железарски

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.