# TechConnect.Server

.NET 10 Generic Host SSH server for NOC operators.

## Standard .NET configuration

The host uses the normal Generic Host configuration stack:

1. `appsettings.json` (base)
2. `appsettings.{Environment}.json` (environment override)
3. Environment variables, command-line, and other default providers

Set the environment with `DOTNET_ENVIRONMENT` (or `ASPNETCORE_ENVIRONMENT`), for example:

```bash
set DOTNET_ENVIRONMENT=Development
dotnet TechConnect.Server.dll
```

### Changing settings from the SSH CLI

Privilege-15 operators can edit settings with `configure terminal` and save them to this server's `appsettings.json` (content root) with `write`. Changes are validated and written atomically; audit and DNS settings and Serilog levels apply immediately, everything else is reported as requiring a restart (the server never restarts itself). `write` persists only the settings the operator changed; values supplied by `appsettings.{Environment}.json` or environment variables (including secrets) are never copied into `appsettings.json`. See [docs/TechConnect/configuration-cli.md](../../docs/TechConnect/configuration-cli.md).

### SSH settings

```json
{
  "TechConnectServer": {
    "Ssh": {
      "Enabled": true,
      "ListenAddress": "0.0.0.0",
      "Port": 2222,
      "HostPrivateKey": "",
      "MaximumConcurrentConnections": 50,
      "ShutdownTimeoutSeconds": 10
    },
    "Authentication": {
      "Local": {
        "PasswordAuthenticationEnabled": true,
        "PublicKeyAuthenticationEnabled": false,
        "LocalUsers": [
          {
            "Username": "admin",
            "Enabled": true,
            "PrivilegeLevel": "15",
            "PasswordHash": "<Identity-V3-hash>",
            "AuthorizedPublicKeys": []
          }
        ]
      }
    }
  }
}
```

Keys and password hashes are stored as JSON string values — not as separate key files. See [docs/TechConnect/authentication.md](../../docs/TechConnect/authentication.md) for full authentication details.

### Which file receives a generated host key?

| Environment | Persistence target |
|---|---|
| Production (default) | `{ContentRoot}/appsettings.json` |
| Development | `{ContentRoot}/appsettings.Development.json` |
| Staging (etc.) | `{ContentRoot}/appsettings.Staging.json` |

`ContentRoot` is typically the application directory (`bin/...` when debugging, or the publish folder when deployed).

### Configuration precedence for `HostPrivateKey`

1. Non-empty value in `appsettings.{Environment}.json` wins.
2. Otherwise a non-empty value in `appsettings.json` is used.
3. An **empty** environment-specific `HostPrivateKey` does **not** override a valid base key and does **not** trigger regeneration. Prefer omitting the property entirely in environment files when you want to inherit the base key.
4. If no non-empty value exists, the server generates an ECDSA P-384 host key and persists it to the environment’s target file above.

Invalid non-empty material fails startup and is never auto-replaced.

### Local operator accounts

Configure accounts under `TechConnectServer:Authentication:Local:LocalUsers`. Public keys are **per user** — a key listed for one username does not authenticate another.

**Default bootstrap account (Development and Production):** username `admin`, password `admin`, privilege `15` (Identity V3 hash in config). Change this publicly known password before exposing the service to untrusted networks.

Generate a password hash for `PasswordHash`:

```bash
dotnet TechConnect.Server.dll --hash-password admin
```

Legacy `TechConnectServer:Ssh:AuthorizedPublicKeys` is rejected at startup. Migrate keys into each account’s `AuthorizedPublicKeys` list.

Cisco-style privilege levels **1–15** (cumulative **active** privilege). Privilege **1** NOC toolkit: `show version`/`privilege`/`clock`/`terminal`/`users`/`sessions`[/`detail`]/`session`/`history` (own)/`dns`/`ip interfaces`/`ip route`, `nslookup`, `clear screen`, `ping`/`traceroute`, `copy … serial:<session-id>`, `connect`/`attach`/`disconnect` (own). `show history session` ≥5; `show history user` ≥11; `show serial`/`disconnect <session-id>` ≥7. `enable` raises active when authorized (local enable secrets or RADIUS `shell:priv-lvl`; requested ≤ authorized; no original-login ceiling). `disable` returns active to **1** without ending terminal mode. Generate enable hash with `--hash-password enable`. See [docs/TechConnect/cli.md](../../docs/TechConnect/cli.md) and [docs/TechConnect/authentication.md](../../docs/TechConnect/authentication.md).

### Operational audit (`TechConnectServer:Audit`)

In-memory audit trail for authentication, authorization denials, connector lifecycle, and operator activity. Defaults: `Enabled: true`, `MaximumEvents: 10000` (oldest events evicted). History is lost on process restart — not persistent, not tamper-proof, not a compliance archive. Inspect with `show history`, `show history session <id>`, and `show history user <username>`. Serilog remains the diagnostic logger. See [docs/TechConnect/cli.md](../../docs/TechConnect/cli.md#operational-audit-history).

### Protecting private keys

`HostPrivateKey` is secret material. Protect the JSON file with filesystem ACLs. **Do not commit production private keys to source control.** Keep secrets only in the deployment content root (or a carefully managed local Development file that is gitignored).

### Preserving configuration during deployment updates

`dotnet publish` copies project `appsettings*.json` into the publish output and a naive “copy publish folder over the deployment” step can **overwrite** a generated host key.

Recommended update procedure:

1. Stop the service.
2. Back up deployment `appsettings.json` and any `appsettings.*.json` that contain secrets/keys.
3. Publish the new version to a staging folder.
4. Copy binaries and non-secret assets into the deployment directory.
5. **Restore** the backed-up configuration files (or exclude them from the overwrite).
6. Start the service.
7. Confirm the SSH host-key fingerprint is unchanged (`ssh-keyscan -p 2222 host`).

Do not rely on publish alone to preserve runtime-generated keys.

### Deliberate host-key rotation

1. Stop the server.
2. Clear `HostPrivateKey` in the **effective** persistence file for that environment (see table).
3. Ensure no valid base key remains if you intend a full rotation.
4. Start the server (empty effective value → generate and persist).
5. Update operator `known_hosts` entries.

### Connect

Default local password (until changed):

```bash
ssh -p 2222 admin@127.0.0.1
```

Or with a per-user public key:

```bash
ssh -p 2222 -i operator_key admin@127.0.0.1
```

```text
Connected to NMS TechConnect Server

TechConnect#   (Administrator)
TechConnect>   (Operator / ReadOnly)

Interactive discovery: Tab completes uniquely matching tokens; ambiguous matches list next-token candidates and extend the longest common prefix. Context-sensitive `?` shows authorized next tokens/arguments (including connect serial keywords and values) without executing. Session IDs complete from the connector registry. Terminal width defaults to 80 and follows SSH window-change. This is Cisco-inspired appliance behaviour, not IOS compatibility.
```

CLI commands: `help`, `?`, `clear screen`, `show version`/`privilege`/`clock`/`terminal`/`users`/`sessions`[/`detail`]/`session`/`serial`/`history` (+ session/user filters)/`dns`/`ip interfaces`/`ip route`, `nslookup`, `connect` (optional serial parameters), `ping` (unified IPv4/IPv6/hostname ICMP, or connector `session` health), `traceroute` (unified ICMP Echo path trace with optional PTR when DNS is configured), `copy` (HTTP/HTTPS/TFTP/SCP/XMODEM/YMODEM binary stream to an attached `serial:<session-id>`; SCP requires per-connection SHA-256 host-key confirmation), `attach`, `disconnect` / `disconnect <session-id>`, `exit`. DNS is controlled exclusively by `TechConnectServer:Dns` (never the OS resolver); empty `Servers` disables hostname resolution while literals still work. `show users` lists SSH operators with TCP remote endpoints from the accepted socket; `show sessions` lists TechConnect.Client agents; `show dns` shows TechConnect DNS config; `show ip *` inspects the host (`show ip route` uses Windows `GetIpForwardTable2`, Linux rtnetlink, or macOS `NET_RT_DUMP`). `show history` (privilege 1) is own-operator only. Manual qualification checklist: [docs/TechConnect/noc-qualification.md](../../docs/TechConnect/noc-qualification.md). There is no heartbeat. Operator attach / terminal / disconnect transitions are mirrored to the connector via on-change `OperatorStatus` (`0x30`) frames — not inferred from serial traffic. See [docs/TechConnect/cli.md](../../docs/TechConnect/cli.md) and [docs/TechConnect/protocol.md](../../docs/TechConnect/protocol.md).
