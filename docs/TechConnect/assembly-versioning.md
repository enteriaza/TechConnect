# Assembly versioning

Every TechConnect project shares one product version derived from the UTC calendar date:

```text
1.1.YYDDD
```

| Part | Meaning |
|---|---|
| `1.1` | Release line (major.minor), set once in `Directory.Build.props` |
| `YY` | Two-digit UTC year |
| `DDD` | UTC day of year (`001`–`366`) |

Examples:

| UTC date | Version |
|---|---|
| 19 November 2026 | `1.1.26323` |
| 1 January 2027 | `1.1.27001` |

This is a **daily** identifier. Multiple builds on the same UTC day intentionally share the same version. Builds on different UTC days get different versions.

## Configuration

Single source of truth: `Directory.Build.props`.

| Property | Value |
|---|---|
| `TechConnectVersionMajorMinor` | `1.1` |
| `TechConnectBuildNumber` | `YYDDD` from `DateTime.UtcNow` (overridable) |
| `Version` / `InformationalVersion` | `1.1.YYDDD` |
| `AssemblyVersion` | Stable `1.1.0.0` (assembly identity does not change per day) |
| `FileVersion` | `1.1.YYDDD.0` |
| `Deterministic` | `true` |

Git / Source Link revision metadata is excluded (`IncludeSourceRevisionInInformationalVersion=false`, empty `SourceRevisionId`).

### Changing the release line

Edit `TechConnectVersionMajorMinor` in `Directory.Build.props` (for example `1.2`). Keep `AssemblyVersion` on the same major.minor with `.0.0` so binding identity stays stable within that line.

### Reproducible builds / tests

Pass an explicit build number when needed:

```text
dotnet build -p:TechConnectBuildNumber=26323
```

## Windows FileVersion limits

Windows stores each of the four `FileVersion` components as a 16-bit value (`0`–`65535`). With `1.1.YYDDD.0`, the build component is `YYDDD`. That stays within range until UTC year **2066** (`66001` and above). Before then, no alternate mapping is required. `Version` / `InformationalVersion` remain the full `1.1.YYDDD` string regardless.
