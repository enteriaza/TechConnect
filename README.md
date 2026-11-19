# TechConnect

TechConnect is a remote serial console system for NOC operators and field technicians.

- **TechConnect.Client** — Windows WinForms agent that owns a serial (COM) port and connects to the server over TCP/mTLS.
- **TechConnect.Protocol** — Shared binary framing protocol and control-message DTOs.
- **TechConnect.Server** — SSH operator CLI plus native TCP/mTLS connector listener, DNS/ICMP/routing tooling, and audit.

Certificate common names retain the historical `TechConnector Development *` identities for compatibility; project and assembly names are `TechConnect.*`.

Repository root: `C:\Users\ChrisKnipe-National\source\repos\TechConnect`

## Architecture

```text
Technician PC                         NOC / server host
┌─────────────────────┐               ┌──────────────────────────┐
│ TechConnect.Client  │  TCP/mTLS     │ TechConnect.Server       │
│  serial COM ↔ frame │──────────────▶│  connector listener      │
│                     │               │  SSH CLI (operators)     │
└─────────────────────┘               └──────────────────────────┘
         ▲                                        ▲
         └──────── TechConnect.Protocol ──────────┘
```

Operators authenticate to the server over SSH. Field agents authenticate with mTLS. Serial bytes and control messages use the shared protocol frames.

## Solution structure

```text
TechConnect.slnx
src/
  TechConnect.Client/
  TechConnect.Protocol/
  TechConnect.Server/
tests/
  TechConnect.Client.Tests/
  TechConnect.Protocol.Tests/
  TechConnect.Server.Tests/
tools/
  Publish-Release.ps1
  TechConnect.Client.DevCerts/
release/                  # Publish-Release.ps1 output (gitignored)
docs/
  TechConnect/
  TechConnect.Client/
```

## Prerequisites

- .NET SDK **10.0.404** (see `global.json`; `rollForward: latestPatch`)
- Windows for TechConnect.Client, Client.Tests, and DevCerts (`net10.0-windows`)
- Optional: provisioned development certificates for local mTLS work (see below)

## Restore and build

From the repository root:

```powershell
dotnet restore .\TechConnect.slnx
dotnet build .\TechConnect.slnx --no-restore
```

## Tests

```powershell
dotnet test .\TechConnect.slnx --no-build
```

Client TCP/mTLS happy-path tests require provisioned development certificates; they fail with a clear prerequisite error when certificates are missing (they never auto-provision).

## Local development

### Development certificates

```powershell
dotnet run --project tools/TechConnect.Client.DevCerts -- provision
dotnet run --project tools/TechConnect.Client.DevCerts -- provision --confirm-trust
dotnet run --project tools/TechConnect.Client.DevCerts -- status
```

Details: [docs/TechConnect.Client/development-certificates.md](docs/TechConnect.Client/development-certificates.md).

### Server

Copy `src/TechConnect.Server/appsettings.Development.example.json` to `appsettings.Development.json` when needed (gitignored). Default Development bootstrap account is documented in the server README.

```powershell
dotnet run --project src/TechConnect.Server
```

SSH (default local): `ssh -p 2222 admin@127.0.0.1`

### Client

Copy `src/TechConnect.Client/appsettings.Development.example.json` to `appsettings.Development.json` when needed (gitignored), set the remote endpoint and client certificate thumbprint, then:

```powershell
dotnet run --project src/TechConnect.Client
```

Publish produces `TechConnect.Client.exe` (`AssemblyName` = `TechConnect.Client`).

## Release publishing

Publish release binaries into the repository-root `release` directory:

```powershell
powershell -NoProfile -File .\tools\Publish-Release.ps1 -Platform all
```

- Client: `win-x64` self-contained **non-AOT** (WinForms; bundled .NET runtime; Linux unsupported).
- Server: `win-x64` and `linux-x64` **Native AOT** (Linux cross-compile from Windows uses **StuDev.AotAnywhere**).

Details: [docs/TechConnect/publish-release.md](docs/TechConnect/publish-release.md).

## Documentation

| Topic | Path |
|---|---|
| Binary protocol | [docs/TechConnect/protocol.md](docs/TechConnect/protocol.md) |
| Protocol extensions | [docs/TechConnect/protocol-extensions.md](docs/TechConnect/protocol-extensions.md) |
| SSH CLI | [docs/TechConnect/cli.md](docs/TechConnect/cli.md) |
| Authentication | [docs/TechConnect/authentication.md](docs/TechConnect/authentication.md) |
| NOC qualification checklist | [docs/TechConnect/noc-qualification.md](docs/TechConnect/noc-qualification.md) |
| Release publishing | [docs/TechConnect/publish-release.md](docs/TechConnect/publish-release.md) |
| Development certificates | [docs/TechConnect.Client/development-certificates.md](docs/TechConnect.Client/development-certificates.md) |
| Serial RX diagnostics | [docs/TechConnect.Client/serial-rx-diagnostics.md](docs/TechConnect.Client/serial-rx-diagnostics.md) |
| Server operations | [src/TechConnect.Server/README.md](src/TechConnect.Server/README.md) |
