# TechConnect.Server — configuration CLI

Privilege-15 operators can view and change server settings from the SSH CLI with a Cisco IOS-style configuration mode. Changes are made to a **private candidate**, validated with the same validators the server uses at startup, and persisted to `appsettings.json` only by an explicit `write`. There is no HTTP management API, no database, and no automatic restart.

## Commands and privilege

Every command below requires **active privilege 15** (`enable` first if you logged in lower). Privilege is checked when the command is typed (Tab/`?` hide it below 15) **and again for every configuration-mode line**; if the active privilege is no longer 15 the session leaves configuration mode with `Permission denied.` and the candidate is discarded.

| Command (EXEC, `TechConnect#`) | Effect |
|---|---|
| `configure` / `configure terminal` (`conf t`) | Enter configuration mode with a new private candidate. Any other source (for example `configure network`) is rejected. |
| `show running-config` | Settings the running process is using. Saved changes that need a restart are listed in a trailing `! Saved changes awaiting a server restart (not active):` block. |
| `show startup-config` | Settings in `appsettings.json` (what the next start will use). |
| `write` / `write memory` (`wr`) | Outside configuration mode there is never anything to save: prints `% No unsaved configuration changes…` and never `[OK]`. |

| Command (configuration mode) | Effect |
|---|---|
| `<section>` | Enter a sub-mode, for example `ssh` → `TechConnect(config-ssh)#`. Sub-modes: `ssh`, `authentication` → `local` / `radius` → `primary-server` / `secondary-server`, `audit`, `dns`, `connector` → `client-certificate-validation`, `logging` → `file`, `serilog`. |
| `username <name> privilege <1-15>` | (under `authentication local`) For a new account: prompts for the password (hidden, confirmed) and creates the account only once it is confirmed. For an existing account: changes its privilege level without a prompt. `username <new-name>` alone is `% Incomplete command.` |
| `username <name> password` | Change an existing account's password at a hidden prompt. |
| `username <name>` | Edit an existing account in `TechConnect(config-user)#` (`enabled`, `privilege-level`, `password`, `authorized-public-keys`). |
| `enable-secret-level <1-15>` / `enable-secret-level <1-15> hash <hash>` | Create or replace a per-level enable secret (hidden prompt, or an existing Identity V3 hash). Configured on this one line in `(config-auth-local)`; there is no per-level sub-mode, and views show each level as `enable-secret-level <n> <hidden>`. |
| `no username <name>` / `no enable-secret-level <1-15>` | Remove the account / per-level secret. |
| `do show <command>` | Run a read-only EXEC `show` command from any configuration level, for example `do show running-config`, `do show startup-config`, `do show users`, `do show configuration changes`. Nothing but `show` is accepted (`do write`, `do configure`, `do enable`, `do copy` are rejected). EXEC `show` commands go through the normal EXEC authorization. |
| `<setting> <value>` | Set a scalar. A bare boolean keyword sets it to `true`. |
| `<list> add <v>` / `<list> remove <v>` / `<list> set <v> …` | Edit list settings (for example `dns` `servers add 192.0.2.53`). |
| `<map> <source> <level>` / `no <map> [source]` | Serilog `minimum-level-override`. |
| `no <setting>` / `default <setting>` | Clear a value / restore the schema default. |
| `show candidate-config` | The candidate (secrets masked). |
| `show configuration changes` | Diff of the candidate against the configuration it started from. Secret lines show `* path <set\|changed\|removed>`, never values. |
| `show running-config` / `show startup-config` | As in EXEC mode. |
| `validate` | Run full validation without writing. |
| `write` / `write memory` | Validate, persist, apply runtime settings, report (see below). Any other argument (for example a path) is rejected. |
| `exit` | Leave the current sub-mode; at `(config)` leave configuration mode. |
| `end` | Leave configuration mode from any sub-mode. |

Keywords accept unique prefixes (an exact match wins, so `enable-secret` is not ambiguous with `enable-secret-level`); values are never abbreviated or fuzzy-matched. Errors follow IOS style:

| Error | Meaning |
|---|---|
| `% Incomplete command.` | A required keyword or argument is missing. |
| caret line + `% Invalid input detected at '^' marker.` | Unknown keyword or unexpected extra input; the caret marks the first bad word. |
| `% Ambiguous command:  "…"` | An abbreviation matches more than one keyword. |
| caret line + `% Invalid value. …` | A well-formed command whose argument failed validation (range, format, username rules). |
| `% Configuration validation failed.` | `validate` / `write` found the candidate as a whole invalid. |

`?` lists what may come next at the cursor, with `<cr>` only when the line as typed would execute (for example `username admin ?` offers `privilege`, `password`, `<cr>`; `username newop ?` offers only `privilege`). Tab completes a unique keyword at the cursor, extends an ambiguous one to the common prefix and lists the choices. Neither `?` nor Tab executes or changes anything, and the typed line is kept.

Line editing: Left/Right, Home/End (also Ctrl+A/Ctrl+E), Backspace, Delete, Ctrl+U (clear line), Up/Down (history). Hidden prompts and lines containing secret material never enter the history.

## Candidate, running, and startup

| View | Source |
|---|---|
| **candidate** | Private per operator session. Created by `configure` from the current **effective** configuration (file plus any overriding configuration source). Never visible to other operators. |
| **startup** | `appsettings.json` in the server content root. |
| **running** | A snapshot taken when the server started, updated only for settings applied at runtime after a successful `write`. |

- Only `write` persists. Leaving configuration mode with `exit`/`end`, disconnecting, or the session being cancelled (server shutdown) **discards** unsaved changes and prints `% Unsaved configuration changes discarded.` (when the session is still connected). Discards are logged (counts only) and audited.
- After a successful `write` the candidate becomes the new baseline: `show configuration changes` is empty and `end` does not warn.
- A new `configure` always starts from the latest saved and effective configuration; candidates are never inherited from another operator or an earlier session.
- The candidate shows the **effective** values (file plus any overriding source such as `appsettings.{Environment}.json` or environment variables), but `write` persists **only the settings the operator changed**. Every other value in `appsettings.json` is left exactly as it was, so values — including secrets — supplied by an environment variable or environment-specific file are never copied into `appsettings.json`, and an override never replaces a value already in the file. `configure` prints a note when overrides are present.
- If the operator changes a setting that another source overrides, the new value is saved to `appsettings.json` and `write` reports `% Note: … another configuration source … still takes precedence for: <setting>`. That setting is not applied at runtime or listed as restart-required, because the override continues to win.

## Write: validation, atomicity, conflicts

`write` runs under a server-wide lock (writes are serialized), in this order:

1. **Conflict check** — the file is re-read and hashed. If it differs from the hash taken at `configure` (another operator saved, or the file was edited externally) the write is refused: `% startup-config (appsettings.json) was modified after this configuration session began …`. Nothing is overwritten; `end` and `configure terminal` to start from the current file.
2. **Validation** — the candidate is merged into the current file JSON and bound to the real options types, then checked with the same validators used at startup (`% Configuration validation failed. Configuration not saved.` plus each failure).
3. **Atomic persist** — the current file with **only the operator's changed settings** applied (see above; values from other configuration sources are never added) is written to a sibling temp file (`.appsettings.json.<guid>.tmp`), flushed to disk, then swapped in with `File.Replace` (or moved into place if the file does not exist). Readers see either the old or the new complete file. On any failure the temp file is deleted and the original is unchanged (`% Unable to write startup-config. The existing file is unchanged.`). Cancellation before the swap also leaves the file unchanged.
4. **Saved** — the candidate baseline is updated.
5. **Runtime apply** — the configuration is reloaded and the resulting **effective** runtime-applicable settings are pushed to their services.
6. **Report** — `[OK]`, `Configuration saved.`, any override note, then `Applied to the running server: …`, `% Saved but not applied at runtime (takes effect after restart): …` (when a runtime apply failed or the service is unavailable), and `% Server restart required for: …`. The report is computed from what is actually running after the apply, so nothing is claimed as applied unless it is. **The server is never restarted automatically.**

Unrelated JSON (unknown sections and unmanaged keys) is kept; comments are not. The target path is fixed to `appsettings.json` under the content root; no CLI input selects a path.

## Runtime-applicable vs restart-required

| Applied at runtime after `write` | Requires a restart |
|---|---|
| `audit enabled`, `audit maximum-events` (shrinking evicts the oldest events immediately) | everything else, including all `ssh`, `authentication`, `connector`, `logging file` settings |
| `dns servers`, `dns query-timeout-seconds` (in-flight queries finish with the settings they started with) | `serilog enrich` |
| `serilog minimum-level`; `serilog minimum-level-override` **level changes for sources present at startup** | adding or removing a `minimum-level-override` source |

## Secrets

| Setting | Stored as | Entry |
|---|---|---|
| `authentication local enable-secret`, `enable-secret-level <n>` | Identity V3 hash | Hidden prompt with confirmation, or `<kw> hash <identity-v3-hash>` |
| `username <name> privilege <n>` (new account), `username <name> password` | Identity V3 hash (subject = username) | Hidden prompt with confirmation; `password hash <hash>` in `config-user` |
| `radius primary-server shared-secret`, `secondary-server shared-secret` | Value (RADIUS needs it) | Hidden prompt with confirmation |
| `ssh host-private-key` | PEM | `host-private-key generate` only |

- Hidden input is never echoed, is not added to command history, and Ctrl+C at the prompt cancels (`% Cancelled. Value not changed.`, or `% Cancelled. 'username <name>' was not created.` for a new account).
- A secret typed on the command line (`shared-secret abc`, `username admin password abc`, `username newop privilege 15 secret abc`, `enable-secret-level 5 abc`) is rejected, never echoed back, and the line is removed from history.
- A secret supplied by an environment variable or `appsettings.{Environment}.json` is never written to `appsettings.json` by `write`, and never replaces a secret already in the file, unless the operator explicitly sets that secret.
- Every view and diff **masks** secrets (`<hidden>` / `(not set)`; diffs `<set|changed|removed>`). Secrets are never written to logs, exception messages, or audit records.

## Settings inventory

All JSON paths are relative to `appsettings.json`. *Runtime* = applied after `write`; *Restart* = takes effect at the next start.

| CLI path | JSON path | Type / validation | Default | Apply |
|---|---|---|---|---|
| `ssh enabled` | `TechConnectServer:Ssh:Enabled` | bool | true | Restart |
| `ssh listen-address` | `…:Ssh:ListenAddress` | IP address | 0.0.0.0 | Restart |
| `ssh port` | `…:Ssh:Port` | int 1–65535 | 2222 | Restart |
| `ssh host-private-key` | `…:Ssh:HostPrivateKey` | secret, PEM (`generate`) | empty | Restart |
| `ssh maximum-concurrent-connections` | `…:Ssh:MaximumConcurrentConnections` | int ≥ 1 | 50 | Restart |
| `ssh shutdown-timeout-seconds` | `…:Ssh:ShutdownTimeoutSeconds` | int ≥ 1 | 10 | Restart |
| `authentication local password-authentication-enabled` | `…:Authentication:Local:PasswordAuthenticationEnabled` | bool | true | Restart |
| `authentication local public-key-authentication-enabled` | `…:Local:PublicKeyAuthenticationEnabled` | bool | true | Restart |
| `authentication local enable-secret` | `…:Local:EnableSecretHash` | secret, Identity V3 hash | empty | Restart |
| `authentication local username <n>` (`enabled`, `privilege-level`, `password`, `authorized-public-keys`) | `…:Local:LocalUsers[*]` | bool; 1–15; secret hash; OpenSSH key lines | enabled true, level 1 | Restart |
| `authentication local enable-secret-level <n>` | `…:Local:EnableSecrets[*]:SecretHash` | secret, Identity V3 hash | — | Restart |
| `authentication radius enabled` / `local-fallback-enabled` | `…:Radius:Enabled` / `LocalFallbackEnabled` | bool | false / false | Restart |
| `authentication radius timeout-milliseconds` | `…:Radius:TimeoutMilliseconds` | int 100–120000 | 3000 | Restart |
| `authentication radius max-attempts` | `…:Radius:MaxAttempts` | int 1–5 | 2 | Restart |
| `authentication radius nas-identifier` | `…:Radius:NasIdentifier` | text ≤ 253 | techconnect | Restart |
| `authentication radius nas-ip-address` | `…:Radius:NasIpAddress` | IP address or unset | unset | Restart |
| `authentication radius primary-server` / `secondary-server` (`address`, `port`, `shared-secret`) | `…:Radius:PrimaryServer` / `SecondaryServer` | host ≤ 253; int 1–65535; secret ≤ 256 | —, 1812, empty | Restart |
| `audit enabled` | `TechConnectServer:Audit:Enabled` | bool | true | Runtime |
| `audit maximum-events` | `…:Audit:MaximumEvents` | int 1–1000000 | 10000 | Runtime |
| `dns servers` | `TechConnectServer:Dns:Servers` | list of IP addresses | empty (DNS disabled) | Runtime |
| `dns query-timeout-seconds` | `…:Dns:QueryTimeoutSeconds` | int 1–60 | 3 | Runtime |
| `connector enabled` | `TechConnectServer:Connector:Enabled` | bool | false | Restart |
| `connector listen-address` / `port` | `…:Connector:ListenAddress` / `Port` | IP; int 1–65535 | 0.0.0.0 / 9443 | Restart |
| `connector server-certificate-thumbprint` | `…:ServerCertificateThumbprint` | thumbprint | empty | Restart |
| `connector server-certificate-store-name` / `-store-location` | `…:ServerCertificateStoreName` / `StoreLocation` | `StoreName` / `StoreLocation` enum | My / CurrentUser | Restart |
| `connector expected-server-subject` / `expected-server-issuer` | `…:ExpectedServerSubject` / `ExpectedServerIssuer` | text | empty | Restart |
| `connector maximum-concurrent-connections` / `handshake-timeout-seconds` / `shutdown-timeout-seconds` | `…:Connector:…` | int ≥ 1 | 100 / 30 / 10 | Restart |
| `connector client-certificate-validation require-client-certificate` | `…:ClientCertificateValidation:RequireClientCertificate` | bool (must be true when enabled) | true | Restart |
| `connector client-certificate-validation allowed-certificate-authorities` | `…:AllowedCertificateAuthorities` | list of thumbprints | empty | Restart |
| `logging file directory` | `Logging:File:Directory` | directory path ≤ 260, no wildcards/control chars | empty (default location) | Restart |
| `logging file file-name-prefix` | `Logging:File:FileNamePrefix` | file-name prefix ≤ 100, no separators | techconnect-server | Restart |
| `logging file retained-file-count-limit` / `file-size-limit-bytes` | `Logging:File:…` | int ≥ 1 / long ≥ 1024 | 14 / 10485760 | Restart |
| `serilog minimum-level` | `Serilog:MinimumLevel:Default` | Serilog level | Information | Runtime |
| `serilog minimum-level-override <source> <level>` | `Serilog:MinimumLevel:Override` | map source → level | — | Runtime (level change); Restart (add/remove source) |
| `serilog enrich` | `Serilog:Enrich` | list of supported enrichers | — | Restart |

**Not managed by the CLI** (preserved unchanged on `write`): `TechConnectServer:Ssh:AuthorizedPublicKeys` (legacy server-wide key list; use per-user `authorized-public-keys`) and any other keys not listed above (for example additional Serilog keys). An automated test (`ConfigurationInventoryTests`) fails if a bound option is added without being classified here.

## Limitations

- Comments in `appsettings.json` are not preserved by `write` (the file is re-serialized; unknown keys and values are kept).
- Lists are saved whole when changed: if another source overrides individual list elements (for example `TechConnectServer__Dns__Servers__0`), an operator change to that list writes the candidate's complete list. Keyed collections (`username`, `enable-secret-level`) are merged per entry and per setting, so an override-supplied entry or password hash is written only if the operator edits that entry's setting.
- Removing an entry that another source supplies (for example a user defined only in environment variables) cannot remove it from that source.
- Validation runs against the effective view assuming the overriding sources stay as they are when the server next starts.
- `configure` waits for any in-progress `write` to finish before creating the candidate.
- The running view is a startup snapshot plus applied runtime changes; restart-required changes remain pending until the process restarts.
- Configuration history and rollback are not provided; the audit trail records who entered, saved, failed to save, or discarded (no values).
