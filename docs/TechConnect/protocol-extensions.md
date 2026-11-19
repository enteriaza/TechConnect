# TechConnect — protocol extensions

## Implemented: OperatorStatus (`0x30`)

Server → connector on-change status for NOC attachment and terminal mode.

See [protocol.md](protocol.md#operatorstatus-server--connector-only) for the wire contract.

Summary:

- Direction: **TechConnect.Server → TechConnect.Client only**
- Payload: `{ stateVersion, attached, terminalActive }`
- Initial `Waiting` published immediately after successful `HelloResult`
- Serialized publisher: version allocation and transmission cannot reorder
- No heartbeat / polling; publication failure must not block attachment cleanup
- No operator username on the wire

## Proposed (not implemented)

These items were identified during Phase 7 operational visibility work. **None are implemented.** Do not treat this section as a shipping contract.

### Motivation

Server-side inspection can display certificate-validated identity, Hello/SerialConfiguration fields, attachment state, and transport lifecycle. It cannot currently establish:

- Whether the connector’s COM port is open.
- Connector-local subsystem faults distinct from TCP/TLS failure.
- Connector-reported uptime independent of server connected-at.

### Candidates

#### 1. Serial port state (extend existing serial configuration)

Optional fields on the existing serial configuration path (Hello seed and/or `SerialConfiguration`):

- `IsOpen` — whether the connector currently has the port open.
- `LastError` — optional short connector-local error string (never a secret).

#### 2. Optional connector status message

An on-change or infrequent status message could report:

- Connector process uptime.
- Serial open state.
- Last RX/TX activity (connector clock).

Such a message **must not** become a heartbeat requirement for session validity. Absence of status must not unregister a live TLS session.

#### 3. Optional last-fault on disconnect

A graceful `Disconnect` may carry a final reason. Not every transport close is a “fault”; server-observed I/O failures remain distinct from connector-reported faults.

### Explicit non-goals here

- Session persistence / database history of disconnected connectors.
- Multi-operator attachment or takeover.
- Device configuration or firmware management.
