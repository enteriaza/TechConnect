# TechConnect.Server — operator authentication

Authentication decides **who** the operator is. Authorization uses a Cisco-style numeric privilege level (**1–15 only**; there is no level 0).

## Original versus active privilege

| Concept | Meaning |
|---|---|
| **Original privilege** | Established at SSH login. Immutable for the session. Used for audit/identity only — **not** an enable ceiling and **not** the destination of `disable`. |
| **Active privilege** | Starts equal to original. Used for authorization, help, discovery, and the prompt. Raised by successful `enable`. `disable` sets active to **1**. |

There is **no** enable ceiling based on the original login privilege. Fresh enable authentication (local secret or RADIUS Cisco-AVPair) authorizes the maximum for that attempt; the requested level must be ≤ that maximum. Failed enable leaves active unchanged.

Public-key local operators use the local enable-secret path. RADIUS operators re-authenticate with their RADIUS username/password (per-user; not Cisco `$enab15$`). Configure mode is not implemented.

## Privilege levels (1–15)

| Level | Meaning |
|---|---|
| **1** | Lowest — `connect` / `attach` / `disconnect` (own); serial console; enable/disable |
| **2–6** | Intermediate; inherit by number |
| **7** | `show serial` / `disconnect <session-id>` (and all lower capabilities) |
| **8–14** | Intermediate; inherit by number |
| **15** | Full admin — `disconnect`; prompt `#` |

Invalid, missing, or out-of-range privilege values fail closed. Values are never silently clamped or defaulted.

### Mapping of former roles → numeric levels

| Former role | Numeric level | Notes |
|---|---|---|
| ReadOnly | **1** | Anchor for migration/`PrivilegeLevel` legacy names; see command mins in [cli.md](cli.md) |
| Operator | **7** | Anchor for operational console access |
| Administrator | **15** | Anchor for full administrative access |

Sensitive history commands require mins above 1 (history ≥5, history user ≥11). `connect` (with optional serial parameters; default 9600 8N1), `attach`, `show sessions` / `show session`, bare `disconnect`, and serial TX/RX are min **1**. `show serial` and administrative `disconnect <session-id>` are min **7**. See [cli.md](cli.md).

## Default administrator account

Both Production (`appsettings.json`) and Development (`appsettings.Development.json`) ship with a local bootstrap administrator:

| Property | Value |
|---|---|
| Username | `admin` |
| Password | `admin` |
| Privilege | `15` |
| Provider | Local |
| Method | Password |

The password is stored only as an ASP.NET Identity V3 hash (`PasswordHash`). There is no plaintext password field in configuration and no special authentication bypass for this username.

**Change this publicly known default password before exposing TechConnect.Server to untrusted networks.** Generate a replacement hash with:

```bash
dotnet TechConnect.Server.dll --hash-password admin
```

## Local accounts

Local accounts live under `TechConnectServer:Authentication:Local`:

```json
{
  "TechConnectServer": {
    "Authentication": {
      "Local": {
        "PasswordAuthenticationEnabled": true,
        "PublicKeyAuthenticationEnabled": true,
        "EnableSecretHash": "<Identity-V3-hash-for-enable>",
        "LocalUsers": [
          {
            "Username": "admin",
            "Enabled": true,
            "PrivilegeLevel": "15",
            "PasswordHash": "<Identity-V3-password-hash>",
            "AuthorizedPublicKeys": [
              "ssh-ed25519 AAAA... admin@example"
            ]
          }
        ]
      }
    }
  }
}
```

| Field | Purpose |
|---|---|
| `EnableSecretHash` | Enable secret authorizing privilege **15** (Identity V3, subject `enable`). Optional convenience for a single level-15 secret. |
| `EnableSecrets` | Optional list of `{ PrivilegeLevel, SecretHash }` for other max levels. Requested enable level must be ≤ the verified secret’s level. Empty lists + empty `EnableSecretHash` → local enable fails closed. **Not** the login password. |
| `Username` | 1–64 characters; letters, digits, `. _ @ + -`. Case-insensitive match; display casing preserved. |
| `Enabled` | Disabled accounts cannot authenticate. |
| `PrivilegeLevel` | Integer **1–15** (canonical). Legacy names `Administrator` / `Operator` / `ReadOnly` map to 15 / 7 / 1 for migration only. |
| `PasswordHash` | ASP.NET Identity V3 login hash. Empty for public-key-only accounts. |
| `AuthorizedPublicKeys` | OpenSSH public-key lines authorized **only** for this username. |

Generate hashes:

```bash
dotnet TechConnect.Server.dll --hash-password admin
dotnet TechConnect.Server.dll --hash-password enable
```

Use the `enable` subject hash as `EnableSecretHash`. Never log or commit plaintext secrets.

Legacy `TechConnectServer:Ssh:AuthorizedPublicKeys` fails startup — migrate keys into per-user lists.

Privilege is never inferred from the username. Existing password hashes are preserved when migrating `PrivilegeLevel` from legacy role names to numeric values.

### Public-key authentication

Uses the local account store only. It does **not** query RADIUS and does **not** use local password fallback. Provider = `Local`, method = `PublicKey`.

### Local password authentication

When RADIUS is **disabled**, SSH password authentication uses local Identity V3 hashes (`PasswordAuthenticationEnabled`).

## RADIUS password authentication

Enable under `TechConnectServer:Authentication:Radius`:

```json
{
  "TechConnectServer": {
    "Authentication": {
      "Radius": {
        "Enabled": true,
        "LocalFallbackEnabled": false,
        "PrimaryServer": {
          "Address": "127.0.0.1",
          "Port": 1812,
          "SharedSecret": "<shared-secret>"
        },
        "SecondaryServer": {
          "Address": "127.0.0.1",
          "Port": 1812,
          "SharedSecret": "<shared-secret>"
        },
        "TimeoutMilliseconds": 3000,
        "MaxAttempts": 2,
        "NasIdentifier": "techconnect",
        "NasIpAddress": null
      }
    }
  }
}
```

When `Radius.Enabled` is true, SSH password authentication is handled by RADIUS (not local-first). Example values are illustrative — do not commit production secrets.

### Access-Request construction

Each attempt builds an RFC 2865 `Access-Request` with:

- `User-Name`
- `User-Password` (PAP hiding via Phase 1 `RadiusPapPassword`)
- `NAS-Identifier`
- Optional `NAS-IP-Address`
- `Service-Type` = Login (1)
- Fresh 16-byte CSPRNG Request Authenticator per attempt

Outbound Message-Authenticator is **not** mandated in this phase. If a response includes Message-Authenticator, it is verified; failure yields `InvalidResponse`.

### Retries (`MaxAttempts`)

`MaxAttempts` applies to **one server**. Retries occur only for genuine `Unavailable` (timeout/transport).  
Never retry `Access-Reject`, `InvalidResponse`, or `Cancelled`.  
Worst-case wait per server ≈ `MaxAttempts × TimeoutMilliseconds`.

### Primary / secondary failover

| Primary | Secondary | Local fallback |
|---|---|---|
| Accept + valid Cisco-AVPair `shell:priv-lvl` | Not queried | No |
| Accept + missing/invalid privilege | Not queried | No |
| Reject | Not queried | No |
| InvalidResponse | Not queried | No |
| Cancelled | Not queried | No |
| Unavailable | Accept + valid privilege | No |
| Unavailable | Accept + invalid privilege | No |
| Unavailable | Reject / InvalidResponse / Cancelled | No |
| Unavailable | Unavailable (or no secondary) | Only if `LocalFallbackEnabled` |

A verified Access-Reject or invalid response is never treated as an outage. Missing or invalid privilege on Access-Accept is an authentication **failure**, not unavailability — secondary and local fallback are not used as a workaround.

### Cisco-AVPair privilege (`shell:priv-lvl`)

Privilege comes only from the Cisco Vendor-Specific Attribute:

- Vendor-Id **9** (Cisco)
- Vendor type **1** (Cisco-AVPair)
- Value: `shell:priv-lvl=<n>` where `<n>` is an integer **1–15**

Example RADIUS reply attribute:

```text
Cisco-AVPair = shell:priv-lvl=1
```

Requirements:

- Missing Cisco-AVPair / missing `shell:priv-lvl` → reject authentication
- Malformed, padded (`01`), level 0, level 16, or conflicting values → reject
- `Filter-Id` alone does **not** grant privilege (no fallback to Filter-Id)
- No default privilege; no client-supplied privilege substitute

### Local password fallback

Controlled by `LocalFallbackEnabled` (default `false`). Attempted only after every configured RADIUS server is genuinely Unavailable. Uses local Identity V3 verification. Identity: Provider = `Local`, Method = `Password`, privilege from the local account.

Startup fails if fallback is enabled without at least one usable local password account.

## Authenticated identity

| Path | Provider | Method | Privilege source |
|---|---|---|---|
| Public key | Local | PublicKey | Local account |
| Local password (RADIUS off) | Local | Password | Local account |
| RADIUS Accept | Radius | Password | Cisco-AVPair `shell:priv-lvl` |
| Local fallback | Local | Password | Local account |

RADIUS identities have no fabricated local account ID or public-key fingerprint. The same identity flows SSH → CLI → attachment.

## Authorization (command enforcement)

Authentication establishes `AuthenticatedOperator` with a numeric `Privilege` (1–15). Authorization compares that level to each command’s configured minimum in `CliCommandPolicy` (default-deny).

Authorization uses **active** privilege. See the full matrix in [cli.md](cli.md).

SSH prompts reflect **active** privilege:

- Levels **1–14**: `TechConnect>`
- Level **15**: `TechConnect#`

### Enable / disable audit

Audit events include enable attempt / success / failure / unavailable, enable authorization denied (requested &gt; authorized), privilege changed, and disable-to-level-1. Events never contain passwords, enable secrets, or RADIUS shared secrets.

RADIUS enable uses the operator’s own username (not `$enab15$`) so the AAA server can return that user’s authorized `shell:priv-lvl`.

## Logging

Logs may include endpoint, username (consistent with existing policy), provider, outcome category, duration, and **mapped** privilege after success. Authorization events may include operator, privilege, command identity, and allow/deny. Never log passwords, shared secrets, PAP material, authenticators, raw serial data, or raw RADIUS packets. Never log an unverified RADIUS attribute as an authoritative privilege.

## Current limitations

- Configuration mode (`configure terminal`) is not implemented.
- RADIUS accounting, CoA/Disconnect, and RadSec are not implemented.
- There is no account provisioning UI or password-change UI.
- Future CLI commands require an explicit authorization policy entry (default-deny).
