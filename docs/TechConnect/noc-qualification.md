# TechConnect NOC command set — manual qualification checklist

Use this checklist after deploying a build that includes the privilege-1 NOC expansion.
**This document does not claim that manual testing has been performed.**

Work top-to-bottom. Mark each item Pass / Fail / Skip (with reason).

Legend for preconditions:

| Tag | Meaning |
|---|---|
| **NOW** | Can run immediately with a single SSH login at the stated privilege |
| **OPS2** | Needs a second concurrent SSH operator |
| **CONN** | Needs at least one connected TechConnect.Client agent |
| **SERIAL** | Needs connector + reachable serial/COM path |
| **NET** | Needs outbound network (ICMP/DNS); may fail in locked-down labs — note Skip |

---

## A. Privilege 1 — discovery and shell hygiene

| # | Command / action | Expected | Pass when | Pre |
|---|---|---|---|---|
| A1 | `?` (empty) | Alphabetical authorized roots including `clear`, `copy`, `nslookup`, `show`, `ping`, `traceroute` | List present; no `show serial` / admin-only verbs | NOW |
| A2 | `help` | Same authorized set as `?` | Help text matches A1 policy | NOW |
| A3 | `show ?` | Subcommands: clock, dns, history, ip, privilege, session, sessions, terminal, users, version | No `serial` | NOW |
| A4 | Tab on `sh` / `show ` | Completes / lists show children alphabetically | Matches A3 | NOW |
| A5 | `show history ?` | No session/user children at privilege 1 | Empty / no `session` `user` | NOW |
| A6 | `clear screen` | Terminal cleared (ANSI); prompt returns | Prompt usable afterward | NOW |
| A7 | `exit` then reconnect | Clean goodbye; new session works | Session ends without server fault | NOW |
| A8 | `quit` | Same as exit | Session ends | NOW |

## B. Privilege 1 — identity / clock / terminal / history

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| B1 | `show version` | App name, assembly version, runtime, protocol, uptime | No filesystem paths / secrets | NOW |
| B2 | `show privilege` | Username, login privilege, active privilege | Values match login | NOW |
| B3 | `show clock` | Local time, timezone, UTC | Unambiguous local vs UTC | NOW |
| B4 | `show terminal` | This session id, privilege, CLI state, TERM/cols/rows | Matches PTY | NOW |
| B5 | `show history` | Own audit events only (or empty-for-user message) | No other users’ events | NOW |
| B6 | `show history session x` | `Permission denied.` | Denied | NOW |
| B7 | `show history user x` | `Permission denied.` | Denied | NOW |
| B8 | Invalid: `show` alone | `% Incomplete command` | Incomplete | NOW |

## C. Privilege 1 — SSH users vs connector sessions

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| C1 | `show users` (alone) | Your row; **REMOTE** shows real `ip:port` or `[ipv6]:port` | Endpoint matches SSH client peer | NOW |
| C2 | `show users` with second operator | Two rows, different endpoints | Independent remotes | OPS2 |
| C3 | Second operator disconnects | Row disappears | Listing shrinks | OPS2 |
| C4 | Reconnect from different host/port | New endpoint on new row | Not stale | OPS2 |
| C5 | `show sessions` | TechConnect.Client agents only (not SSH users) | No SSH usernames as session rows | CONN |
| C6 | `show sessions detail` | Expanded blocks; same order as C5 | Hostname/version/TLS/attachment fields | CONN |
| C7 | `show session <id>` | Sectioned detail for one agent | Unknown id → not found | CONN |
| C8 | Empty registry | Clear empty messages for sessions/detail | No crash | NOW |

## D. Privilege 1 — host network inspection

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| D1 | `show dns` | TechConnect Dns servers / timeout / enabled | Not OS resolver settings | NOW |
| D2 | `show ip interfaces` | Names, status, type, IPv4/IPv6 + prefixes | Multi-address NICs shown | NOW |
| D3 | `show ip route` | **IPv4** and **IPv6** sections from OS table | Default + on-link rows plausible vs OS | NOW |
| D4 | Compare D3 to OS view | Windows: matches forwarding table conceptually; Linux/macOS: matches native table | No fabricated gateways | NOW |
| D5 | Large table readability | Columns aligned; both families | Readable | NOW |

## E. Privilege 1 — DNS lookup

Requires `TechConnectServer:Dns:Servers` for success paths (IPv4 and/or IPv6 DNS server addresses). Empty list disables DNS.

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| E1 | `nslookup` | usage / incomplete | Incomplete | NOW |
| E2 | `nslookup <name>` with Dns servers | A/AAAA + configured server used | Success path | NET |
| E3 | `nslookup` with empty Dns servers | DNS not configured error | No OS fallback | NOW |
| E4 | `nslookup ?` | `<hostname>` help | Contextual help | NOW |
| E5 | Ctrl+C during slow lookup | Cancel; session remains | No disconnect | NET |
| E6 | `nslookup <ip>` | PTR via configured DNS | Hostname or clear failure | NET |

### DNS failover (resolver behaviour)

| Condition | Behaviour |
|---|---|
| Success | Return immediately |
| NXDOMAIN | Stop; do not try next server |
| NODATA (NOERROR, no matching records) | Stop; do not try next server |
| SERVFAIL / REFUSED / FORMERR | Try next configured server |
| UDP/TCP timeout, transport error, malformed | Try next configured server |
| Truncated UDP | TCP fallback on **same** server first; then next server if TCP fails |

IPv6 preference: when IPv6 is usable on the host and AAAA exists, prefer IPv6; otherwise IPv4. Literals never query DNS.

### Reverse DNS (traceroute)

- PTR only when Dns servers are configured; skipped for `*` timeouts.
- Failed/timed-out PTR leaves the hop IP visible; per-trace cache avoids repeats.

## F. Privilege 1 — ping / traceroute (algorithms unchanged)

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| F1 | `ping 127.0.0.1` / `ping 8.8.8.8` | Echo summary | Works or clear error | NET |
| F2 | `ping 2001:4860:4860::8888` / hostname | Family auto-detect; IPv6 prefer when enabled | Consistent with discovery | NET |
| F3 | `ping session <id>` | Connector health | Needs live agent | CONN |
| F4 | `traceroute <dest>` | Hop table; PTR when Dns configured | Cancel with Ctrl+C safe | NET |
| F5 | Invalid args | Usage / `%` errors | No crash | NOW |
| F6 | `ping`/`traceroute` hostname, Dns empty | Immediate DNS-not-configured; no probes | No OS resolver | NOW |

### Live network qualification log (executed)

Record actual runs here; do not mark Pass without inspected output.

| Date | Environment | Command | Result |
|---|---|---|---|
| 2026-11-18 | Windows, configured `1.1.1.1`, raw ICMP available | `show dns` | Enabled; server `1.1.1.1`; OS resolver note present |
| 2026-11-18 | same | `nslookup google.com` | A + AAAA via `1.1.1.1` |
| 2026-11-18 | same | `nslookup 1.1.1.1` | PTR `one.one.one.one` |
| 2026-11-18 | Dns servers empty | `ping google.com` / `traceroute google.com` | Immediate DNS-not-configured; no probes |
| 2026-11-18 | Dns empty | `ping 127.0.0.1 repeat 1` | 100% success |
| 2026-11-18 | Dns `1.1.1.1` | `ping 8.8.8.8 repeat 2` | 100% (RTT ~18–20 ms) |
| 2026-11-18 | Dns `1.1.1.1`, IPv6 usable | `ping dns.google repeat 2` | Resolved to IPv6 `2001:4860:4860::8844`; 100% |
| 2026-11-18 | same | IPv6 literal ping `2001:4860:4860::8888` | Success (~18 ms) |
| 2026-11-18 | same | ICMP traceroute `8.8.8.8` (ttl 5) | Probes returned; responders present |
| 2026-11-18 | same | PTR on hop `198.18.0.65` | No PTR (displayed IP only) |

**Automated vs live:** unit tests under `tests/.../Dns` and `NameResolution` cover wire protocol and failover with fake servers. The table above is live CLI/ICMP evidence from this environment.

## G. Privilege 1 — serial console (lifecycle unchanged)

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| G1 | `connect <id>` | Attach + serial apply + terminal | Enter terminal | SERIAL |
| G2 | Ctrl+] | Return to CLI; attachment kept | `show terminal` / users show attachment | SERIAL |
| G3 | `attach` | Resume terminal | Works after G2 | SERIAL |
| G4 | `disconnect` | Release own attachment | Connector stays up | SERIAL |
| G5 | `disconnect <id>` | `Permission denied.` | Still privilege 7 | CONN |

## H. Privilege 7

Login or `enable` to active privilege **7**.

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| H1 | `show ?` | Includes `serial` | Visible | NOW |
| H2 | `show serial` | Allowed when attached | Or clear not-attached | SERIAL |
| H3 | `show history session <id>` | Allowed | Events or empty filter | NOW |
| H4 | `show history user x` | Still denied | Permission denied | NOW |
| H5 | `disconnect <id>` | Confirm prompt; admin teardown | Completes or cancel safe | CONN |
| H6 | Discovery: `disconnect <Tab>` | Session ids offered | Privilege-gated | CONN |

## I. Privilege 15

| # | Command | Expected | Pass when | Pre |
|---|---|---|---|---|
| I1 | Prompt | `TechConnect#` | Hash prompt | NOW |
| I2 | `show history user <name>` | Allowed | Filter works | NOW |
| I3 | All privilege-1 commands | Still work | No regression | NOW |
| I4 | `disable` | Active → 1; prompt `>` | Serial attachment retained if any | NOW |
| I5 | After disable: admin cmds | Denied again | Policy restored | NOW |

## J. Cross-cutting negative / resilience

| # | Action | Expected | Pass when | Pre |
|---|---|---|---|---|
| J1 | Ambiguous Tab (`dis`) | Lists disable/disconnect | Alphabetical | NOW |
| J2 | Unknown command | Caret + `% Invalid command` | Session stays up | NOW |
| J3 | Rapid `show ip route` ×10 | Stable; no native leak symptoms | Process healthy | NOW |
| J4 | Concurrent `show users` from two ops | Consistent independent rows | OPS2 |
| J5 | Server restart | History cleared; routes/dns still work | Expected | NOW |

## K. Privilege 1 — `copy` to attached serial

| # | Command / action | Expected | Pass when | Pre |
|---|---|---|---|---|
| K1 | `copy ?` / Tab after `copy ` | Source schemes / usage | Discoverable at priv 1 | NOW |
| K2 | `copy http://h/f.bin flash:x` | Destination must be `serial:` | Rejected | NOW |
| K3 | `copy http://h/f.bin serial:deadbeef` | Session not found / no attachment | Rejected before fetch | NOW |
| K4 | Unattached: `copy … serial:<id>` | Requires existing attachment | Rejected | CONN |
| K5 | Attached then `copy https: serial:<id>` | Interactive host/file/(user)/password prompts | Password not echoed; display URL has no password | SERIAL |
| K6 | `copy https://user:pass@host/file serial:<id>` | Transfer starts without re-asking known fields | History / logs show `user:****@` only | SERIAL+NET |
| K7 | HTTP(S)/TFTP/SCP of a **real binary** firmware file | Bytes streamed; completion reports transport write count | Device may or may not accept — do **not** claim flash success from TechConnect alone | SERIAL+NET |
| K8 | `copy xmodem: serial:<id>` | Prompt to begin upload; client XMODEM send | Payload only on serial; framing not forwarded | SERIAL |
| K9 | `copy ymodem: serial:<id>` | YMODEM receive; padding trimmed when size known | Multi-file batch rejected or empty end handled | SERIAL |
| K10 | Second `copy` while first active | Rejected | One transfer per attachment | SERIAL |
| K11 | Cancel mid-transfer (Ctrl+C) | Stops; CLI restored; attachment kept | Connector still connected | SERIAL |
| K12 | Other operator uses your `serial:<id>` | Denied (not their attachment) | OPS2+CONN |

### K — qualification results (automated vs live)

Recorded during the copy integrity qualification pass. **Git was not modified as part of that pass.**

#### 1. Automated unit / integration (CI)

| Area | Evidence | Result |
|---|---|---|
| Binary integrity | Payloads with `0x00`–`0xFF`, NULs, CR/LF, invalid UTF-8, long `0x00`/`0xFF` runs, fragmented reads (1–9000 byte source fragments), SHA-256 match to serial-write sink | Pass |
| XMODEM padding | Final 128-byte block with trailing `0x1A` forwarded in full (not stripped); 1K CRC; checksum mode; duplicate block; bad CRC retransmit | Pass — padding retained by design |
| YMODEM exact size | Sizes 0, 1, 100, 128, 129, 256; padding trimmed; malformed size keeps full blocks; multi-file rejected; oversized advertised size rejected | Pass |
| HTTP | Loopback `HttpListener` 100 000-byte binary (SHA-256); empty file; 404/500/401; 302 not followed; Basic auth; mid-stream IO failure; cancel | Pass |
| TFTP | Loopback UDP sizes 0/1/511/512/513/2048 incl. zero-length final DATA; duplicate block; ERROR packet | Pass |
| SCP host key | Operator confirms SHA-256 fingerprint per connection via DevTunnels `AuthenticateServerAsync` / `ServerPublicKey`; reject before password; no persistence; same key A twice and key B each require `yes` | Pass (automated Mini SSH server) |
| Credentials | History redaction; percent-encoded user/pass; completion text denies device flash claims | Pass |
| Concurrency / cancel | Second copy same attachment rejected; registry released on cancel/failure; wrong owner cannot End | Pass |

Focused copy + related tests are run before full regression (see completion report for counts).

#### 2. Live source protocol / serial / device

| Layer | Status |
|---|---|
| Live HTTP(S)/TFTP/SCP against production file servers | **Outstanding** — lab/network dependent |
| Live XMODEM/YMODEM from a modem-capable SSH client (e.g. SecureCRT, Tera Term) | **Outstanding** — generic OpenSSH/`ssh` without modem support is **not** compatible |
| Live serial via TechConnect.Client → COM/USB | **Outstanding** |
| Bootloader/device acceptance or flash verify | **Outstanding** — TechConnect success ≠ device acceptance |

Manual binary check (K7): use a known firmware image; record size + SHA-256; confirm TechConnect reports **transport write** bytes only. Verify on the device separately if firmware acceptance matters.

**Example result row (fill when hardware-tested):**

| Field | Value |
|---|---|
| Date / build | _TBD_ |
| File size / SHA-256 | _TBD_ |
| Source protocol | _TBD_ |
| Terminal client (X/YMODEM) | _TBD or N/A_ |
| Serial endpoint | _TBD_ |
| Device acknowledged? | No / Yes (how) |
| Approx. rate | _TBD_ |
| Cancel tested? | _TBD_ |

---

## Suggested session order

1. Single privilege-1 login: sections **A → B → C1/C8 → D → E → F (local) → J**.
2. Attach TechConnect.Client: **C5–C7, F3, G, H5, K**.
3. Second SSH operator: **C2–C4, J4, K12**.
4. Raise privilege: **H**, then **I**.

## Platform notes for `show ip route`

| Platform | Implementation | Manual check |
|---|---|---|
| Windows | `GetIpForwardTable2` | Compare defaults / on-link prefixes to OS routing UI |
| Linux | rtnetlink dump | Compare to knowledge of host routes (do **not** use test by spawning `ip`) |
| macOS | `sysctl NET_RT_DUMP` | Same — conceptual match only |
| Other | Unsupported message | Message clear; no invented rows |
