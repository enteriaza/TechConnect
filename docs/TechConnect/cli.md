# TechConnect SSH CLI — attachment and terminal mode

Operators authenticate over SSH (see [authentication.md](authentication.md)) and interact with TechConnect.Server through an in-process CLI. The authenticated operator identity established at SSH login is reused for the CLI session and any connector attachment. Command authorization uses that identity’s privilege.

## Interactive terminal

TechConnect owns the SSH shell channel (no OS shell, no X11). After authentication the server accepts `pty-req` / `shell`, records terminal type and size (including live `window-change` resize), and runs an in-process line editor:

- Echo of printable characters
- Enter via CR, LF, or CRLF (exactly one command)
- Backspace / Delete, Ctrl+C (cancel line), Ctrl+D (exit when empty), Ctrl+U (clear line)
- Up/Down command history (per SSH session, not persisted; no secrets stored)
- Cisco-inspired Tab completion and context-sensitive `?` help (not IOS compatibility)

Invalid commands use a caret under the bad token and `% Invalid command`. Incomplete commands (for example `show` alone) return `% Incomplete command`.

## Command discovery (Tab and `?`)

Parsing, help text, Tab completion and context-sensitive `?` share one catalog (`CliCommandCatalog`) plus connect serial providers in `ConnectSerialSettings`. Visibility is derived from `CliCommandPolicy` / `OperatorAuthorizationService` — discovery never bypasses execution authorization, and never discloses commands above the active privilege.

Commands, subcommands, parameter keywords, finite values, session IDs, and other enumerable discovery candidates are presented in **case-insensitive alphabetical order** (ordinal tie-break). Authorization filtering still runs first. Catalog insertion order and command syntax are unchanged; only presentation/discovery lists are sorted.

### Tab

- Unique match: completes the current token inline (adds a trailing space when more tokens or arguments follow).
- Ambiguous match: lists **next-token** candidates in columns sized to the PTY width (default 80), extends the token to the **longest common prefix** when that grows the input, restores the prompt and input, does not pick a candidate arbitrarily, and does not execute.
- Already-complete commands with nothing left to complete: no-op (terminal bell).
- Session identifiers: Tab completes authorized, discoverable connector session IDs from the in-memory registry (same visibility as `show sessions` / `connect`), in alphabetical order.
- After `connect <session-id>`, Tab offers remaining serial keywords (`baud`, `databits`, `parity`, `stopbits`) and supported values for the keyword under the cursor. Keywords already supplied are not offered again. Baud values come only from `ControlProtocol.SupportedBaudRates`.

Examples:

```text
TechConnect> con<Tab>
TechConnect> connect

TechConnect> connect ff<Tab>
TechConnect> connect ff530f5b

TechConnect> connect ff530f5b <Tab>
  baud  databits  parity  stopbits

TechConnect> connect ff530f5b parity n<Tab>
TechConnect> connect ff530f5b parity none
```

### Context-sensitive `?`

Typing `?` is intercepted by the line editor: it is never inserted into the buffer and never submitted as a command. It shows authorized next tokens (with descriptions) or argument placeholders / values, then restores the prompt, input line and cursor.

- Empty prompt / partial command token: authorized commands (or matching candidates).
- `connect ?`: shows the `<session-id>` placeholder (not a live session dump — use Tab for IDs).
- `connect <session-id> ?`: remaining serial parameter keywords.
- `connect <session-id> parity ?` (and likewise for `baud` / `databits` / `stopbits`): supported values only.

Examples:

```text
TechConnect> ?
  attach      Resume terminal mode on your existing attachment (after Ctrl+])
  clear       ...
  connect     Attach, apply serial settings (default 9600 8N1), and enter terminal
  copy        Copy a file to an attached serial console (binary-safe stream)
  disable     ...
  disconnect  ...
  enable      ...
  exit        ...
  help        ...
  nslookup    ...
  ping        ...
  show        ...
  traceroute  ...

TechConnect> show ?
  clock
  dns
  history
  ip
  privilege
  session
  sessions
  terminal
  users
  version

TechConnect> connect ?
  <session-id>  Connector session identifier

TechConnect> connect ff530f5b ?
  baud       Serial baud rate
  databits   Serial data bits
  parity     Serial parity
  stopbits   Serial stop bits

TechConnect> connect ff530f5b parity ?
  even  mark  none  odd  space

TechConnect> ping 8.8.8.8 ?
  df  repeat  size  source  sweep  timeout

TechConnect> ping 8.8.8.8 sweep ?
  interval  max  min
```

Submitted `help` or `?` as a full command still prints the full authorized command list from the same catalog.

## Privilege and commands

Authorization uses Cisco-style privilege levels **1–15**. Access is cumulative on the session’s **active** privilege (see [authentication.md](authentication.md#original-versus-active-privilege)). Levels **2, 4, 6, 8–10, 12–14** are valid and inherit by number only.

### Connect, attach, and terminal

Angle brackets are **placeholders**; curly braces mark **optional** groups. Neither is typed literally.

| Capability | Min | Notes |
|---|---:|---|
| `connect <session-id> {baud …}` | 1 | Attach, apply serial settings, enter terminal on success |
| Ctrl+] | 1* | Leave terminal; keep attachment (*only in terminal) |
| `attach` | 1 | Resume terminal on the existing attachment (after Ctrl+]) |
| `disconnect` | 1 | Release own attachment; connector stays up |
| Serial TX/RX | 1 | While in terminal mode |

Default serial settings when omitted: **9600 8N1** (baud 9600, databits 8, parity none, stopbits 1). Unspecified parameters always use these defaults — never a previous operator’s settings.

Examples:

```text
connect a1b2c3d4
connect a1b2c3d4 baud 115200
connect a1b2c3d4 baud 115200 databits 8 parity none stopbits 1
```

`connect` validates parameters **before** attaching. It sends one complete configuration via the existing SetBaud / SetBaudResult path, waits for success, then enters terminal. On configuration failure or timeout the attachment created by that connect is released; the TechConnect.Client session and COM port stay up.

Privilege changes do **not** close the connector TLS session or COM port. Dropping to privilege 1 (including via `disable`) does **not** exit terminal mode. `show serial` and administrative `disconnect <session-id>` remain min **7**.

### DNS configuration

TechConnect uses **only** DNS servers listed under `TechConnectServer:Dns` in `appsettings.json`. It never uses the operating-system resolver, never falls back to localhost, and never invents servers.

```json
{
  "TechConnectServer": {
    "Dns": {
      "Servers": [ "1.1.1.1", "2606:4700:4700::1111" ],
      "QueryTimeoutSeconds": 3
    }
  }
}
```

- Empty `Servers` disables DNS. Literal IP destinations still work; hostname resolution and reverse DNS fail immediately with a clear error.
- Invalid server addresses or timeout values fail startup validation.
- A privilege-15 operator can change `Servers` and `QueryTimeoutSeconds` with `configure terminal` → `dns`; after `write` they apply at runtime (queries already in flight finish with their original settings).
- `show dns` displays this TechConnect configuration (enabled state, ordered servers, timeout) — not the host OS DNS settings.
- Queries are sent directly over UDP (with TCP fallback when the truncation flag is set). Supported types: **A**, **AAAA**, **PTR**.

### Ping diagnostics

Privilege **1**. Unified destination syntax (address family is detected automatically):

```text
ping <destination> {repeat <n>} {size <bytes>} {timeout <seconds>} {source <addr>} {df} {sweep ...}
ping session <session-id> {repeat <n>} {timeout <seconds>}
```

`<destination>` may be an IPv4 literal, an IPv6 literal, or a hostname.

| Parameter | Default | Range |
|---|---:|---|
| `repeat` | 5 | 1–100 |
| `timeout` | 1 s | 1–60 s |
| `size` (ICMP payload) | 56 bytes | 0–9000 |
| `sweep` min/max/interval | 100 / 1500 / 100 | max ≤ 9000; total probes ≤ 200 |

- IPv4/IPv6 use **raw-socket ICMP/ICMPv6** on TechConnect.Server (not `ping.exe`). `df` is IPv4-only (`Socket.DontFragment`).
- Hostnames are resolved with TechConnect DNS only. Prefer **IPv6** when IPv6 is usable on the server and an AAAA record exists; otherwise use IPv4. Output shows the original name and selected address. If DNS is disabled, hostname pings fail immediately (no probes).
- IPv6 literals require a **global** IPv6 address **and** an IPv6 default gateway (link-local alone is insufficient). No silent IPv4 fallback.
- `ping session` is an application health check over the connector mTLS session (`SessionPing` / `SessionPingResult`). It does **not** touch the COM port or serial stream.
- Sweep reports ICMP **payload** sizes and approximate IP packet size (payload + header overhead). Largest successful payload is reported; it is **not** claimed as exact path MTU.
- Abort with **Ctrl+C**; summary includes probes sent so far.

Examples:

```text
TechConnect> ping 8.8.8.8

Type escape sequence to abort.
Sending 5, 56-byte ICMP Echos to 8.8.8.8, timeout is 1 seconds:

!!!!!
Success rate is 100 percent (5/5)
Round-trip min/avg/max = 12/14/18 ms

TechConnect> ping google.com

Resolving google.com...
Using IPv6 address 2001:4860:4860::8888

Sending 5, 56-byte ICMPv6 Echos to google.com [2001:4860:4860::8888], timeout is 1 seconds:
...

TechConnect> ping session e92b5fa2

Pinging connector session e92b5fa2 (CHRIS-PC):

!!!!!
Success rate is 100 percent (5/5)
Round-trip min/avg/max = 2/3/5 ms
Session state: Connected
```

### Traceroute diagnostics

Privilege **1**. Native ICMP Echo traceroute on TechConnect.Server (not `tracert.exe`). Probes are **ICMP Echo Requests** with incrementing IPv4 TTL or IPv6 Hop Limit — the same probe model Windows `tracert` uses.

On Windows, UDP-based traceroute is not viable with the available raw-socket APIs: ICMP Time Exceeded / Dest Unreachable generated for UDP probes are **not delivered** to a raw ICMP receive socket (verified against destinations that `tracert` reaches in one hop). TechConnect therefore uses ICMP Echo probes and correlates Time Exceeded (embedded Echo Request id/seq) and Echo Reply.

```text
traceroute <destination> {timeout <seconds>} {probe <count>} {ttl <hops>} {source <address>} {port <port>} {size <bytes>}
```

`<destination>` may be an IPv4 literal, an IPv6 literal, or a hostname (same address-selection rules as ping).

| Parameter | Default | Range |
|---|---:|---|
| `timeout` | 3 s | 1–60 s (per probe) |
| `probe` | 3 | 1–10 probes per hop |
| `ttl` | 30 | 1–64 (max TTL / Hop Limit) |
| `port` | 33434 | Accepted for CLI compatibility; **not used** for ICMP Echo probes |
| `size` | 40 bytes | 0–1472 **ICMP Echo payload** (not total IP packet size) |

Additional guards: estimated worst-case duration (`ttl × probe × timeout`) must be ≤ 600 s.

- Hostnames use TechConnect DNS only. Literals work with DNS disabled. No OS resolver fallback.
- When DNS servers are configured, responding hops get a bounded PTR lookup (cached per traceroute). Output shows `hostname [ip]` when PTR succeeds; IP only on failure/timeout. Timeouts (`*`) are not reverse-looked-up. PTR slowness does not change RTT values.
- Correlation uses ICMP Echo identifier + per-probe sequence (not UDP ports).
- `source` must match the destination family and be assigned to a local interface; otherwise the OS selects the route source.
- IPv6 literals require the same IPv6 availability gate as ping. No silent IPv4 fallback.
- ICMP Time Exceeded identifies intermediate hops. Echo Reply means the destination was reached. Other Destination Unreachable / administratively prohibited responses abort the trace (distinct from a probe timeout `*`).
- A timeout does **not** stop the trace. Abort with **Ctrl+C**; partial hops already printed remain visible. Diagnostic failures must not close the SSH session.

Examples:

```text
TechConnect# traceroute 8.8.8.8

Tracing route to 8.8.8.8
Maximum hops: 30, probes per hop: 3

 1  192.168.1.1       1 ms    1 ms    2 ms
 2  *                 *       *       *
 3  8.8.8.8          22 ms   22 ms   23 ms

Trace complete.

TechConnect> traceroute ?
  <destination>    IPv4 address, IPv6 address, or hostname

TechConnect> traceroute 8.8.8.8 ?
  port  probe  size  source  timeout  ttl
```

### Copy — binary file transfer to serial

Privilege **1**. Streams a file from a network or modem source to an **already attached** connector serial console. The path is **binary-safe**: no UTF-8 decoding, no newline conversion, and no interpretation of payload bytes as CLI input.

```text
copy {http:|https:|tftp:|scp:|xmodem:|ymodem:} serial:<session-id>
copy http://192.0.2.10/firmware.bin serial:<session-id>
copy https://user:password@firmware.example.com/device.bin serial:<session-id>
copy tftp://192.0.2.10/firmware.bin serial:<session-id>
copy scp://admin@192.0.2.10/firmware.bin serial:<session-id>
```

Requirements and behaviour:

- Destination is always `serial:<session-id>` and must match **your** current attachment. Copy does **not** auto-attach, change baud settings, release the attachment, or disconnect the connector.
- Incomplete sources (scheme-only such as `https:`) prompt interactively for host, filename, and credentials as needed. Complete URLs skip questions whose answers are already present.
- URL user-info (`scheme://user:password@host/path`) is accepted. Passwords are stripped from display/log/history forms (`user:****@…`). Password prompts do not echo. Credentials are not persisted.
- Only one active copy per serial attachment.
- Progress shows bytes (and percent when Content-Length / YMODEM size is known). Ctrl+C cancels; CLI input handling is restored; attachment and connector remain.
- **HTTP/HTTPS**: GET with streaming body; TLS validation remains enabled for HTTPS; redirects are not followed automatically (avoids credential forwarding).
- **TFTP**: RRQ octet mode, 512-byte blocks, ACK/retransmit timeouts.
- **SCP**: Outbound SSH using the existing DevTunnels SSH client stack (not an OS `scp` process). Password authentication only. **Every SCP connection** presents the server host-key **SHA-256** fingerprint (OpenSSH `SHA256:…` form, no Base64 padding) calculated from the public key bytes exchanged during SSH negotiation. The operator must type `yes` to trust that key **for this connection only**. Approval is never persisted (no known-hosts / TOFU / appsettings cache). A changed server key simply shows a new fingerprint on the next connection — no administrative cleanup. Verify fingerprints out-of-band when possible; accepting an unverified key risks MITM. Password prompts occur only after host-key acceptance.

```text
TechConnect# copy scp://admin@192.0.2.10/firmware.bin serial:1234abcd

Connecting to SCP server 192.0.2.10:22...

SSH server host key:
  Host:        192.0.2.10
  Port:        22
  Algorithm:   ecdsa-sha2-nistp384
  Fingerprint: SHA256:xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx

Approval applies only to this connection and is not remembered.
Do you trust this host key for this connection only? [yes/no]: yes
Host key accepted for this connection.
Authenticating...
```

- **XMODEM / YMODEM**: TechConnect receives on the **SSH** session. Start the upload from a terminal client that supports the protocol after the CLI prompts you. Framing/CRC bytes are never written to serial — only file payload. YMODEM multi-file batches are rejected. **Classic XMODEM has no file-size field**, so the receiver forwards each complete data block including any final-block padding (commonly `0x1A`); padding is **not** stripped, because those bytes can also be legitimate file content. Prefer **YMODEM** when exact length matters — YMODEM trims final-block padding using the block-0 size when present.

**What success means:** source retrieval finished and N payload bytes were written into the TechConnect→connector serial transport. It does **not** prove the device accepted, stored, or flashed the image.

```text
TechConnect# copy https: serial:1234abcd
Address or name of remote host []? firmware.example.com
Source filename []? device.bin
Username []? alice
Password:
Source: https://alice@firmware.example.com/device.bin
Destination: serial:1234abcd
...
Copy completed: 1048576 payload byte(s) written to the serial transport in 12.4 s.
This confirms TechConnect transport write completion, not device-level acceptance or flash success.
```

### Implemented command minimums

| Command | Min privilege |
|---|---:|
| `help` / `?` | 1 |
| `exit` / `quit` | 1 |
| `clear screen` | 1 |
| `enable` / `enable <level>` | 1 |
| `disable` | 1 |
| `show version` | 1 |
| `show privilege` | 1 |
| `show clock` | 1 |
| `show terminal` | 1 |
| `show users` | 1 |
| `show sessions` | 1 |
| `show sessions detail` | 1 |
| `show session <session-id>` | 1 |
| `show history` (own events only) | 1 |
| `show dns` | 1 |
| `show ip interfaces` | 1 |
| `show ip route` | 1 |
| `nslookup <hostname>` | 1 |
| `connect <session-id> [serial parameters]` | 1 |
| `ping …` | 1 |
| `traceroute …` | 1 |
| `copy … serial:<session-id>` | 1 |
| `attach` | 1 |
| `disconnect` (own attachment) | 1 |
| `show history session <session-id>` | 5 |
| `show history user <username>` | 11 |
| `show serial` | 7 |
| `disconnect <session-id>` (admin) | 7 |
| `configure` / `configure terminal` | 15 |
| `write` / `write memory` | 15 |
| `show running-config` / `show startup-config` | 15 |
| Every configuration-mode line | 15 (re-checked per line) |

Configuration mode (`TechConnect(config)#`), candidate/startup/running semantics, atomic `write`, runtime vs restart-required settings, and secret handling are documented in [configuration-cli.md](configuration-cli.md).

### `disconnect` forms

| Form | Min | Effect |
|---|---:|---|
| `disconnect` | 1 | Releases **your** current attachment only. Does **not** terminate the TechConnect.Client / TLS session. Errors cleanly if you are not attached. |
| `disconnect <session-id>` | 7 | Administratively terminates the named **connector** session (any operator attachment is released). Denied below privilege 7 before any session is affected. |

Examples:

```text
disconnect
disconnect a1b2c3d4
```

The former `detach`, `terminal`, and `set baud` commands are removed. Serial settings are specified only on `connect`.

### `enable` / `disable`

| Command | Behaviour |
|---|---|
| `enable` | Target **15** (default). Prompts when raising above active privilege. |
| `enable <level>` | Target 1–15. If target ≤ active, no-op (does **not** reduce privilege). If target > active, authenticate then set active to the **requested** level when authorized. |
| `disable` | Returns active privilege to **level 1**. Idempotent. No password. Original login privilege is unchanged (audit/identity only). |

**Local:** verify enable secret(s). `EnableSecretHash` authorizes level **15** (and any requested ≤ 15). Optional `EnableSecrets` entries authorize their configured max level. Requested must be ≤ secret’s authorized level. Login password is never an enable secret.

**RADIUS:** fresh Access-Request with the original username. Cisco-AVPair `shell:priv-lvl=N` is the authorized maximum for that attempt. Requested must be ≤ N (e.g. enable 15 with N=7 is denied). Insufficient privilege is an authorization denial — not Unavailable, and not a secondary/local fallback. Active becomes the **requested** level on success (not automatically N).

Failed enable leaves active privilege unchanged. Prompt reflects **active** privilege: `>` for 1–14, `#` for 15.

Unauthorized commands return `Permission denied.` Help, Tab, and `?` use the same policy as execution. Minimums are in `CliCommandPolicy`.

## Operational inspection (`show`)

Inspection uses in-memory snapshots assembled from the session registry, attachment manager, serial configuration cache, and pending baud tracker. Components are read separately (no cross-lock); slight timing skew between fields is acceptable for CLI use.

### Connection state vs attachment

| Concept | Meaning |
|---|---|
| **Connection state** | For sessions still in the registry: always `Connected`. Disconnected connectors are removed; there is no stale or “faulted” history in the CLI. |
| **Operator attachment** | Separate Yes/No (and username / terminal-active). A connector can be Connected with no operator attached. |

Do not treat “Attached” as a connection-health state.

### Server-observed vs connector-reported

| Kind | Examples |
|---|---|
| **Server-observed** | Session ID, abbreviated certificate identity, remote endpoint, TLS protocol, connected-at, last activity, computed duration, attachment, pending baud |
| **Connector-reported** | Hostname, application version, port name, baud/parity/stop/data/handshake (from Hello / SerialConfiguration) |

Missing connector-reported values show as `Not reported`. Missing optional server fields show as `-`. **Duration** is computed on the server from connected-at; it is not connector uptime.

### Commands

#### Operator and connector sessions

- `show users` — connected **human SSH operator** CLI sessions (username, session id, **SSH TCP remote endpoint** `ip:port` / `[ipv6]:port` captured from the accepted socket at session open, login time, active privilege, attachment). Distinct from TechConnect.Client agent listings. Endpoint is omitted (`-`) only when the transport did not expose a remote `IPEndPoint`.
- `show sessions` — compact table of connected **TechConnect.Client agents** (width-aware; default 80 columns): session, connector (hostname or abbreviated identity), version, remote, connected duration, connection state, operator.
- `show sessions detail` — expanded per-agent blocks (session id, hostname, application version, remote, TLS, connected/last activity, state, attachment, terminal, serial when reported). Same chronological ordering as `show sessions`.
- `show session <id>` — sectioned detail for one connector: Connector, Serial, Operator Attachment, Pending Baud Request (unchanged).
- `show terminal` — this operator’s SSH CLI session (privilege, attachment, TERM, columns/rows).
- `show serial` — cached serial settings for the **current** attachment only; does not query the connector or COM port.

#### Identity, clock, and version

- `show version` — TechConnect application name/version (assembly metadata), .NET runtime, protocol version, server uptime.
- `show privilege` — authenticated username, login privilege, and **active** privilege (distinct after `enable` / `disable`).
- `show clock` — server local time, time zone, and UTC.

#### Server network inspection (TechConnect host)

These inspect the **TechConnect.Server host**, not a field device or connector:

- `show dns` — TechConnect DNS configuration (enabled/disabled, ordered servers, query timeout). Never shows OS resolver settings.
- `show ip interfaces` — local network interfaces, status, type, and IPv4/IPv6 addresses with prefix lengths.
- `show ip route` — host routing table (IPv4 and IPv6) via native APIs (not `route` / `ip` / `netstat` shell commands). Output is split into **IPv4 Routing Table** and **IPv6 Routing Table** sections (destination/prefix, next hop or `On-link`, interface, metric). Ordering is by family, destination, prefix length, then metric. Platform implementations:
  - **Windows:** `GetIpForwardTable2` / `FreeMibTable` (AF_UNSPEC) — full IPv4 and IPv6 forwarding table.
  - **Linux:** rtnetlink `NETLINK_ROUTE` / `RTM_GETROUTE` dump.
  - **macOS:** `sysctl` `NET_RT_DUMP`.
  - Other platforms report unavailability rather than inventing routes.

#### DNS lookup

- `nslookup <hostname|ip-address>` — A/AAAA (or PTR for IP literals) via the dedicated TechConnect DNS resolver. Shows query name, configured server used, query type, addresses/PTR, and failure reason. Fails immediately when DNS is not configured. Never uses the OS resolver or `nslookup.exe`.

#### History

- `show history` — **your** recent operational audit events only (privilege **1**). See [Operational audit history](#operational-audit-history).
- `show history session <id>` — audit events for a connector session ID (min **5**; works after the session leaves the live registry).
- `show history user <username>` — audit events for an exact operator username (min **11**; case-insensitive; includes failed authentication where the requested username is known).

#### Terminal

- `clear screen` — clears **this** SSH terminal only (ANSI clear); does not affect other operators. Prompt and input behaviour are unchanged afterwards.

### Current limitations

- No heartbeat / liveness polling; idle sessions remain Connected until the TCP/TLS connection ends.
- No serial port open/closed or physical device health (not in the protocol today).
- Live `show sessions` / `show session` have no disconnected-session registry; use `show history` for recent operational events (in-memory only).
- Full certificate material is never dumped (identity is abbreviated).

See [protocol-extensions.md](protocol-extensions.md) for proposed future protocol fields (not implemented).

## Operational audit history

TechConnect.Server keeps a **bounded in-memory** application audit trail for NOC operators. It answers who did what, to which connector/session, when, and whether it succeeded — separately from Serilog diagnostic logs.

### Configuration

`TechConnectServer:Audit`:

| Setting | Default | Notes |
|---|---|---|
| `Enabled` | `true` | When `false`, writes are no-ops and `show history*` explains that auditing is disabled |
| `MaximumEvents` | `10000` | Oldest events are evicted when capacity is exceeded; invalid values fail startup |

Both settings can be changed by a privilege-15 operator with `configure terminal` → `audit` and take effect at runtime after `write` (disabling stops recording immediately; lowering `MaximumEvents` evicts the oldest events immediately). See [configuration-cli.md](configuration-cli.md).

History is **discarded when the process restarts**. It is not a database, not exported, not tamper-proof, and not a compliance-certified audit archive.

### Event outcomes

| Outcome | Meaning |
|---|---|
| `Success` | Operation completed successfully |
| `Failure` | Operation failed |
| `Denied` | Explicit rejection (authorization or RADIUS Access-Reject / duplicate connector) |
| `Informational` | Lifecycle notice (disconnect, terminal enter/exit, baud start, RADIUS unavailable before fallback) |

Ordinary connector or SSH disconnects are informational, not failures. A successful login followed by SSH disconnect is not a failed authentication.

### Event types (summary)

Authentication (password / public-key / RADIUS / local fallback), authorization denials, connector register/reject/disconnect, operator attach/detach, automatic attachment release (SSH or connector disconnect), terminal enter/exit, baud request start/success/failure/timeout, and configuration mode enter / write / write failure / discard (`config-enter`, `config-write`, `config-write-fail`, `config-discard`; operator and session only, never values).

### Deliberately excluded

Passwords and hashes, RADIUS shared secrets, private keys, authentication packet contents, serial payload, terminal I/O, and full certificate private material. Event descriptions and metadata are sanitized (control characters stripped) for safe CLI display.

### Audit history vs Serilog

| | Application audit (`show history`) | Serilog |
|---|---|---|
| Purpose | Queryable operational trail for operators | Diagnostic / troubleshooting logs |
| Retention | In-memory, bounded, process lifetime | File/console per host logging config |
| Audience | NOC via SSH CLI | Operators of the host / log pipeline |

There are no CLI commands to delete or clear audit history. Audit settings are changed only through configuration mode.

## Distinct states

| State | Meaning |
|---|---|
| **CLI mode** | Interactive line editing with echo (`TechConnect#` at privilege 15, `TechConnect>` at 1–14) |
| **Terminal mode** | Raw serial byte forwarding to/from the attached connector |
| **Attached** | This SSH session owns the connector for control and (when in terminal mode) serial I/O |
| **Detached** | No connector ownership |

Terminal mode and attachment are **separate**. Leaving the terminal does not release the attachment.

## Interaction model

| Event | Terminal | Attachment |
|---|---|---|
| SSH session starts | CLI | Detached |
| `connect <session-id> […]` succeeds | Terminal | Attached |
| **Ctrl+]** (byte `0x1D`) | CLI | **Attached** (kept) |
| `attach` | Terminal | Attached (unchanged; no serial reconfig) |
| `show serial` | CLI | Attached (required; privilege ≥7) |
| `disconnect` | CLI | **Detached** |
| SSH disconnect | Ended | Detached |
| Connector disconnect | CLI (notified) | Detached |

## Ctrl+]

- Consumed locally; **never** forwarded to the COM port.
- Bytes before Ctrl+] in the same read buffer are forwarded unchanged.
- Bytes after Ctrl+] are **not** forwarded as serial TX.
- Returns the operator to the CLI prompt with the attachment intact.
- While at the CLI with attachment kept, device RX is **not** delivered to the SSH session. Resume with `attach` (does not reapply serial settings).

## `attach`

- Available when an attachment already exists (typically after Ctrl+]).
- Does **not** accept a session id, create a new attachment, or reconfigure serial.
- Fails cleanly if unattached or if the connector has disconnected (stale attachment is released).

## `disconnect` (own attachment)

- Releases operator↔connector ownership on both sides.
- Cleans up pending serial-configuration requests for that operator.
- Does **not** close the connector TCP/mTLS session or the physical COM port.
- Errors cleanly if already unattached.

## Automatic cleanup

SSH disconnect and connector disconnect still release attachments automatically and fail outstanding baud requests as appropriate.

## Administrative connector disconnect

Administrators may forcibly end a live TechConnect.Client console session:

```text
TechConnect# disconnect a1b2c3d4

Disconnect connector a1b2c3d4?
This will terminate the remote console connection.
Confirm [y/N]:
```

- **Privilege 7+** (levels below 7 denied at authorization and discovery).
- Confirmation defaults to **No** (empty Enter, `n`, or any non-yes response cancels).
- Explicit `y` / `yes` proceeds; Ctrl+C or SSH disconnect cancels with no deferred teardown.
- Session identity and connection ownership are revalidated after confirmation so a replacement connection is never torn down by mistake.
- An attached operator is released through the normal connector-disconnect lifecycle (terminal mode ends; pending baud requests fail; SSH CLI remains usable for that operator).
- Remote disconnect ends the NOC↔connector session only. It does **not** close the connector’s locally owned COM port, change serial configuration, or replace reconnect behaviour on TechConnect.Client.
- Audit events: decline / cancel / initiate / success / failure (Phase 8 in-memory trail). Lifecycle may also record ordinary connector disconnect and attachment-release events from the existing teardown path.
- Success is reported only when the original connection is disposed **and** listener cleanup for that `ConnectionId` is observed. If disposal was requested but cleanup has not finished yet, the CLI reports that cleanup is incomplete (not success). Listener cleanup remains authoritative.
