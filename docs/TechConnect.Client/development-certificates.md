# TechConnect.Client development certificates

Persistent, self-signed development PKI for local TCP/mTLS work.

**Builds, tests, and application execution never modify the Windows certificate store.**

Only the explicit `tools/TechConnect.Client.DevCerts` utility installs or renews certificates.

## Architecture

| Certificate | Subject | Store | Purpose |
|---|---|---|---|
| Development CA (public) | `CN=TechConnector Development CA` | `CurrentUser\Root` | Trust anchor for ordinary .NET TLS validation |
| Development CA (private key) | same | `CurrentUser\My` | Issues development leaves |
| TCP/mTLS server | `CN=TechConnector Development WSS` | `CurrentUser\My` | Local TechConnect.Server connector listener (subject name is historical; identity unchanged) |
| TechConnect.Client client | `CN=TechConnector Development Client` | `CurrentUser\My` | mTLS client authentication |

- Validity: **10 years**
- Server SANs: `DNS:localhost`, `IP:127.0.0.1`, `IP:::1`
- Private keys are non-exportable for routine use
- Identities are matched by **recorded thumbprint** plus subject/issuer/EKU checks (not subject alone)

> Note: certificates previously provisioned for the obsolete WebSocket path may still appear in developer stores under the same server subject. They are obsolete for transport use; do not remove them during normal development unless you intentionally re-provision.

## One-time provisioning

```powershell
dotnet run --project tools/TechConnect.Client.DevCerts -- provision
```

Creates/reuses certificates in `CurrentUser\My` and writes local thumbprint files.
If the CA is not yet trusted, it prints the trust command and **does not** modify `CurrentUser\Root`.

Then, after reviewing the planned change:

```powershell
dotnet run --project tools/TechConnect.Client.DevCerts -- provision --confirm-trust
```

Type `YES` when prompted. This installs only the CA **public** certificate into `CurrentUser\Root`.
No certificates are removed.

## Status (read-only)

```powershell
dotnet run --project tools/TechConnect.Client.DevCerts -- status
```

Never writes to any certificate store.

## Configuration

| File | Tracked? | Purpose |
|---|---|---|
| `tools/TechConnect.Client.DevCerts/dev-certs.local.json` | No | Authoritative local thumbprint registry |
| `tests/TechConnect.Client.Tests/dev-certs.json` | No | Test companion thumbprints |
| `src/TechConnect.Client/appsettings.Development.json` | No | App client thumbprint + development identities |
| `*.example.json` / `dev-certs.example.json` | Yes | Placeholders only |

TechConnect.Client loads the client certificate via `RemoteService:ClientCertificate:Thumbprint` using the existing `ClientCertificateProvider` (Windows store + validity/EKU/identity checks).

## Automated TCP/mTLS tests

```powershell
dotnet test tests/TechConnect.Client.Tests/TechConnect.Client.Tests.csproj
```

Happy-path tests load the persistent store certificates. If provisioning is incomplete they **fail with a clear prerequisite error** (they never skip, never auto-provision, never install/remove certificates).

Negative TLS tests use **in-memory** certificates only and never touch Windows stores.

## Local pre-production TCP/mTLS

1. Provision certificates (once).
2. Start TechConnect.Server with the development **server** certificate from `CurrentUser\My` on the connector listener.
3. Launch TechConnect.Client with `appsettings.Development.json` pointing at `RemoteService:Host` / `Port` / `TlsHostName` and the development **client** thumbprint.
4. Ordinary chain + hostname validation applies (no TLS bypass).

## Renewal

```powershell
dotnet run --project tools/TechConnect.Client.DevCerts -- renew
```

Issues new server/client leaves, updates local config, and **retains** previous certificates. Does not replace the CA. Never runs during build/test/app startup.

## Do not remove during normal development

- `CN=TechConnector Development CA` (Root + My)
- `CN=TechConnector Development WSS` (TCP/mTLS server leaf; historical subject)
- `CN=TechConnector Development Client`

Removing them breaks local TLS until you re-run provisioning (and trust confirmation if Root was cleared).

## Diagnostics

Missing / expired / wrong EKU / wrong issuer:

1. `dotnet run --project tools/TechConnect.Client.DevCerts -- status`
2. Confirm `dev-certs.json` / `appsettings.Development.json` thumbprints match `status`
3. Confirm CA appears under `CurrentUser\Root` (certmgr.msc → Trusted Root Certification Authorities → Certificates)
