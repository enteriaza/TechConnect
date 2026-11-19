# Release publishing

This document describes `tools/Publish-Release.ps1`, which publishes TechConnect applications into the repository-root `release` directory.

Publishing modes differ by project:

| Project | Windows x64 | Linux x64 |
|---|---|---|
| **TechConnect.Client** | Self-contained **non-AOT** (bundled .NET runtime) | **Unsupported** (WinForms / `net10.0-windows`) |
| **TechConnect.Server** | **Native AOT** | **Native AOT** via **StuDev.AotAnywhere** |

The Client does **not** use Native AOT. WinForms + `PublishAot` is unsupported by the .NET SDK (`NETSDK1175`); forcing it produces binaries that fail to start. The Client release is a standard self-contained publish: it includes the .NET runtime and does **not** require a separately installed .NET runtime on the target machine.

## Requirements

- Windows host recommended for the full matrix (Client is Windows-only).
- .NET SDK **10.0.404** (see `global.json`; `rollForward: latestPatch`).
- Visual Studio C++ build tools (MSVC) for Server `win-x64` Native AOT linking.
- **StuDev.AotAnywhere 1.1.0** MSBuild SDK for Linux AOT cross-compilation from Windows:
  - Pinned in `global.json` under `msbuild-sdks`.
  - Referenced by `TechConnect.Server.csproj` via `<Sdk Name="StuDev.AotAnywhere" … />`.
  - Restored by NuGet on first `dotnet publish` / restore (bundles Zig; no WSL/Docker required).

Do not replace AotAnywhere with “install the Linux AOT workload only” — that does not enable cross-OS linking from Windows.

## Usage

From any working directory:

```powershell
# All supported targets
powershell -NoProfile -File .\tools\Publish-Release.ps1 -Platform all

# Windows only (Client self-contained + Server Native AOT)
powershell -NoProfile -File .\tools\Publish-Release.ps1 -Platform win-x64

# Server Linux only (cross-compile from Windows)
powershell -NoProfile -File .\tools\Publish-Release.ps1 -Platform linux-x64 -Project Server

# Client Windows only (self-contained non-AOT)
powershell -NoProfile -File .\tools\Publish-Release.ps1 -Platform win-x64 -Project Client
```

The script resolves the repository root from `$PSScriptRoot` (the `tools` folder). Output always goes to:

`<repository-root>\release`

Never to `artifacts\`, `.artifacts\`, or `bin\Release` as the final location.

## Output layout

```text
release/
├── TechConnect.Client/
│   └── win-x64/
│       ├── TechConnect.Client.exe
│       ├── TechConnect.Client.dll          # managed app (self-contained)
│       ├── hostfxr.dll / coreclr.dll / …   # bundled runtime
│       ├── appsettings.json
│       └── …
└── TechConnect.Server/
    ├── win-x64/
    │   ├── TechConnect.Server.exe          # Native AOT
    │   ├── appsettings.json
    │   └── …
    └── linux-x64/
        ├── TechConnect.Server              # Native AOT (ELF)
        ├── appsettings.json
        └── …
```

Each project/RID directory is deleted and recreated before publish so a previous Native AOT Client build cannot be mistaken for the current self-contained Client. Cleanup is confined to that directory under `<repo>\release`.

Development/staging appsettings and local cert registries are removed from the release directory after publish when present.

## Verification performed by the script

**Client (self-contained non-AOT):**

1. `dotnet publish` exit code is zero.
2. `TechConnect.Client.exe` exists and is a non-empty PE32+ x64 image.
3. Managed `TechConnect.Client.dll` is present.
4. Bundled runtime files (`hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`) are present.
5. `TechConnect.Client.deps.json` and `appsettings.json` are present.

**Server (Native AOT):**

1. `dotnet publish` exit code is zero.
2. Expected native executable exists and is non-empty.
3. No managed application `*.dll` or shared host/coreclr runtime files are present.
4. Windows: PE32+ x64 with empty CLR runtime directory.
5. Linux: ELF 64-bit little-endian x86-64 (`ET_EXEC` or `ET_DYN`).
6. `appsettings.json` is present.

## What is *not* verified here

- Automatic GUI interaction testing of the Client (launch the published `.exe` separately).
- Linux Server runtime execution (cross-compiled on Windows).
- Application-level integration (serial, SSH, mTLS) against live hardware/PKI.
- Target-host ICU / native library presence for globalization on Linux.

Cross-compilation success is not the same as Linux runtime qualification.

## Linux runtime notes

Cross-compiled Server binaries may require ICU on the target Linux host (`libicu`). Install the distribution ICU package, or rebuild with `/p:InvariantGlobalization=true` only if culture-invariant behaviour is acceptable (not the default release setting).

## Failures

The script uses strict mode, fails immediately on the first error, writes `error: …` to stderr, and exits with code `1`. Unsupported combinations (for example Client + `linux-x64`) fail with an explicit message rather than producing a placeholder artifact.

## Server Native AOT analysis notes

`TechConnect.Protocol` uses System.Text.Json source generation (`ProtocolJsonContext`) so control-message JSON is Native AOT compatible. Application and protocol code should not emit unresolved AOT/trimming warnings under this setup.

Server publish passes `-p:IlcTreatWarningsAsErrors=false` **only** because third-party packages still produce ILC analysis warnings:

| Category | Source | Implication |
|---|---|---|
| Application code | TechConnect.* | Should remain clean; do not suppress C# `TreatWarningsAsErrors`. |
| Protocol serialization | `ProtocolJsonContext` | Source-generated; preferred AOT path. |
| Serilog | `Serilog`, `Serilog.Settings.Configuration` | IL2104 / IL3000 / IL3002 / IL3053 — config assembly scanning / `DependencyContext` may be limited in single-file/native hosts. Prefer code-configured sinks where critical. |
| Microsoft.DevTunnels.Ssh | SSH package | IL2104 trim warnings — revisit when the package ships full AOT annotations. |
| Other | — | None expected beyond the above for Server. |

Client publish does **not** use Native AOT and does **not** pass `IlcTreatWarningsAsErrors` or `_SuppressWinFormsTrimError`.
