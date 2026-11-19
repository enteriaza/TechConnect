# TechConnect binary protocol

Shared by TechConnect.Client and TechConnect.Server over authenticated TCP/mTLS (`SslStream`).

Implementation: `src/TechConnect.Protocol`.

## Wire format

Every frame begins with a **6-byte header**, then an optional payload.

| Offset | Size | Field | Description |
|---:|---:|---|---|
| 0 | 4 | `PayloadLength` | Unsigned 32-bit **big-endian** length of the payload only |
| 4 | 1 | `MessageType` | Explicit message type (see below) |
| 5 | 1 | `ProtocolVersion` | Protocol version (`1`) |
| 6 | N | `Payload` | Exactly `PayloadLength` bytes |

`PayloadLength` **excludes** the 6-byte header.

Constants: `ProtocolConstants.HeaderSize = 6`, `ProtocolConstants.ProtocolVersion = 1`.

### Example

A `SerialData` frame carrying bytes `01 02`:

```text
00 00 00 02   10   01   01 02
└─ length=2 ─┘ └type┘ └ver┘ └payload┘
```

## Message types

| Value | Name | Payload |
|---|---|---|
| `0x01` | Hello | UTF-8 JSON control |
| `0x02` | HelloResult | UTF-8 JSON control |
| `0x10` | SerialData | **Opaque raw bytes** (no UTF-8 / JSON) |
| `0x11` | SerialConfiguration | UTF-8 JSON control |
| `0x20` | SetBaud | UTF-8 JSON control |
| `0x21` | SetBaudResult | UTF-8 JSON control |
| `0x30` | OperatorStatus | UTF-8 JSON control (**server → connector only**) |
| `0x40` | SessionPing | UTF-8 JSON control (**server → connector** app health probe; not serial) |
| `0x41` | SessionPingResult | UTF-8 JSON control (**connector → server**) |
| `0x7E` | Error | UTF-8 JSON control |
| `0x7F` | Disconnect | UTF-8 JSON control |

Unknown message types and unsupported protocol versions are **fatal** framing errors.

Message type and protocol version are **not** repeated inside JSON payloads.

## Control JSON conventions

- Encoding: UTF-8
- Naming: camelCase (`System.Text.Json`)
- Optional correlation: `correlationId` (string) on request/response control messages
- Required fields are validated on deserialize; malformed JSON is rejected

### Hello (client → server)

```json
{
  "correlationId": "optional",
  "clientSessionId": "required",
  "hostname": "optional",
  "portName": "COM3",
  "baudRate": 115200,
  "dataBits": 8,
  "parity": "None",
  "stopBits": "One",
  "handshake": "None"
}
```

### HelloResult (server → client)

```json
{
  "correlationId": "optional",
  "success": true,
  "serverName": "TechConnect.Server",
  "sessionId": "required-when-success",
  "error": null
}
```

### OperatorStatus (server → connector only)

On-change notification of NOC operator attachment / terminal mode. **Not** a heartbeat; absence does not unregister a live TLS session. Serial traffic must never be used as a proxy for this state.

```json
{
  "stateVersion": 1,
  "attached": false,
  "terminalActive": false
}
```

| Field | Rules |
|---|---|
| `stateVersion` | Unsigned integer ≥ 1; strictly increasing within one TCP connection generation |
| `attached` | `true` iff an operator owns this connector session |
| `terminalActive` | May be `true` only when `attached` is `true` |

### SessionPing (server → connector)

Application-level health probe for `ping session`. Must not access the COM port or serial stream.

```json
{
  "correlationId": "required"
}
```

### SessionPingResult (connector → server)

```json
{
  "correlationId": "required",
  "success": true,
  "hostname": "optional",
  "error": null
}
```

`error` is required when `success` is false.

Typical sequence after successful registration: `Waiting` (`attached=false`) → `Attached` → `Active` (terminal after connect) → `Attached` (Ctrl+]) → `Active` (`attach`) → `Waiting` (disconnect / SSH drop).

Client acceptance: apply only when `stateVersion` is **higher** than the last applied version. Same version with identical state is ignored; same version with different state is a protocol consistency warning; lower versions are stale and ignored. Reconnect resets the version sequence.

## SerialData semantics

`SerialData` payloads are opaque octets:

- No UTF-8 conversion
- No JSON
- No newline / terminal interpretation
- Every byte preserved exactly (including `0x00`, `0xFF`, CR/LF, ESC, invalid UTF-8)

## Payload limits (defaults)

| Category | Maximum payload |
|---|---:|
| Control messages | 16 KiB |
| Serial data | 64 KiB |
| Hard frame ceiling | 64 KiB |

Limits are enforced **after** reading the header and **before** allocating/reading the payload.

Configurable via `ProtocolLimits`.

## Framing behaviour

| Condition | Behaviour |
|---|---|
| Clean EOF before a new frame | `FrameReader.ReadAsync` returns `null` |
| EOF mid-header or mid-payload | `ProtocolTruncatedFrameException` (fatal) |
| Unknown type / bad version / oversize | Fatal `ProtocolException` subclass |
| Fatal framing error | Terminate the protocol connection; do not resynchronize |

## Stream ownership

`FrameReader` / `FrameWriter` / `ProtocolSession` **do not** dispose the underlying stream.

Caller owns the `SslStream` / `NetworkStream` lifetime (typically via `ConnectorConnection` or `TcpMtlsRemoteConsoleTransport` teardown).

Concurrent writes are serialized by `FrameWriter` so headers/payloads never interleave.

## Public API (summary)

- `MessageType`, `ProtocolConstants`, `ProtocolLimits`, `ProtocolFrame`
- `Framing.FrameReader`, `Framing.FrameWriter`, `Framing.ProtocolSession`
- `Messages.*` DTOs
- `Serialization.ControlMessageSerializer`
- `ProtocolException` hierarchy

TCP/mTLS traffic uses binary framing. `ControlProtocol` is the Client-side JSON envelope mapped to/from those binary control frames.
