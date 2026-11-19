# TechConnect.Client — Serial RX diagnostics and loopback

## Purpose

When the COM port opens but the console shows `RX: 0`, isolate whether the failure is:

1. Electrical / cable / device (no data into the adapter)
2. USB serial driver / Windows (adapter not delivering bytes)
3. Serial configuration mismatch (baud/framing)
4. Application receive path / UI

Remote TCP/mTLS must never be required for this diagnosis.

## Automated vs physical

| Layer | What it proves | How |
|---|---|---|
| Unit / fake-port tests | Application RX pipeline, counters, UI batching | `dotnet test tests/TechConnect.Client.Tests` |
| Physical loopback | Adapter TX→RX through Windows into the app | Explicit **Loopback test** button |
| Device console | End-to-end device TX → console cable → adapter → app | Manual; not claimed by loopback |

**Physical RX is not verified by unit tests alone.**

## Loopback test (explicit only)

The application never transmits diagnostic bytes on Connect.

### Wiring (USB-to-serial adapter only)

1. Disconnect the adapter from the network device.
2. On the adapter’s RS-232 (or breakout) side, jumper **TX to RX**.
3. Ensure a common **GND** reference where the adapter exposes it.
4. Do **not** short RS-232 levels to TTL UART pins.
5. Connect → open COM → click **Loopback test** → confirm the warning.

The probe sequence is ASCII: `TECHCONNECTOR-RX-TEST-12345`.

### Interpreting results

| Result | Meaning |
|---|---|
| **Matched** | Written bytes were observed on RX. Adapter + Windows + app RX path work for this COM port at the current settings. |
| **Did not match, RX still 0** | Windows delivered no echo. Check jumper, driver, or that TX/RX pins are correct. Does **not** validate the network-device cable. |
| **Did not match, some RX** | Noise or partial echo; inspect cabling. Do not treat as success. |
| **TX without loopback** | Bytes may disturb a live device console. TX ≠ RX proof. |

## Effective settings

After open, logs include a single **effective settings** line read from the live `SerialPort` instance (baud, data bits, parity, stop bits, handshake, DTR, RTS, timeouts, buffer sizes).

Defaults are **115200 8N1, Handshake.None**. They may be wrong for a given device; do not assume they match the console. Remote configuration still applies when a valid remote session provides settings.

## Hardware / cabling still required (device path)

Loopback does **not** identify:

- Rollover vs straight-through vs null-modem pinout for the specific device
- Whether the console is RS-232 (±12 V class) or TTL UART (3.3 V / 5 V)
- Correct TX/RX orientation on the device console header
- Required baud rate for that device’s console

**Do not connect RS-232 adapters directly to TTL UART.** Use a level shifter when the device console is TTL.

Information still needed from the device/cable documentation:

- Electrical standard (RS-232 vs TTL)
- Pinout / cable type
- Console baud / framing

## Log signals (no payloads)

- `Serial port effective settings after open: …`
- `Serial RX reader active=…`
- `Serial RX first data…` (Information, once)
- Loopback start/finish with byte **counts** only
- Reader terminate summary with counters
