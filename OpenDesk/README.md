# OpenDesk 0.3 · Windows remote desktop

OpenDesk now uses the familiar numeric-address workflow: your computer shows
**Your address**, and you enter another computer's number in **Remote address**.
Every viewer still needs host approval. Multiple viewers can watch, with one
remote keyboard/mouse controller. The app has its own branding and code.

0.3 fixes clipped navigation and connection controls, adds DPI-aware layouts and
Fast/Balanced/Sharp screen quality, activates input immediately after Allow control,
coalesces mouse motion, and moves image decoding off the UI thread. New peers
negotiate a two-frame video window so slow links do not accumulate stale video.
Install this version on both PCs for all improvements. The existing VPS relay
service remains compatible; it forwards the encrypted session without decoding it.

## Get started

Extract `OpenDesk-Windows-x64.zip` and run `OpenDesk.exe`. Keep the included
`OpenDesk.Service.exe` beside it if you want the local-service setup button.
Both executables are self-contained Windows x64 development builds; no .NET
runtime installation is needed. These builds are unsigned.

### Default public device network

New profiles connect automatically to the owner's VPS at
`https://139.99.135.69:47834`, with this trusted certificate fingerprint built in:

```text
5B80725FF3E6DC733FADB7AF55727DA4C622D5FB82C9AC4EE04B7C60A774AEEF
```

Open the app on both PCs, wait for their numeric addresses, enter the other
PC's number and approve the connection there. Start local service is unnecessary.
Existing saved service settings are preserved. Profiles with an empty service
address adopt the public default. Settings still allows a different service.
If the VPS certificate changes, its pin must be updated through a trusted channel.
Device numbers resolve through OpenDesk's service, not AnyDesk's network.

### Optional: host a different device network

For a Linux VPS, use the self-contained Linux service bundle and the installation
guide in [deploy/linux/README.md](deploy/linux/README.md). It includes a systemd
unit and an installer; build it locally with `./publish-linux.ps1`.

For a local/LAN test:

1. On the service PC, open **Settings → Start local service**. This starts the
   included service in the background and fills in its local HTTPS address and
   certificate fingerprint. Select **Save settings**.
2. OpenDesk goes online and displays its assigned nine-digit address. The ID
   stays the same across normal app and service restarts.
3. On the other PC, configure **Settings** with `https://SERVICE-PC-IP:47834`
   and the **same certificate fingerprint**. Enter your display name and save.
4. The service PC must permit HTTPS TCP **47834** and relay TCP **47832** on the
   chosen network. OpenDesk does not silently change firewall rules. `localhost`
   only works when client and service are on the same computer.
5. Each PC that goes online receives its own ID. Enter the other PC's number
   in **Remote address**, select **Connect**, and wait for its approval.

For internet use, run the service on a reachable server and configure both PCs
with that server's HTTPS hostname/IP, fingerprint and reachable ports. The same
ID-only connection flow then uses outbound relay connections. A domain name
is optional when using explicit certificate pinning. Independent two-PC WAN
operation still needs verification.

Normal connections use numbers; service IP/port/trust configuration happens once
in Settings. IDs are scoped to the configured service. A number locates a device;
it is not a password and does not grant unattended access.

## Working features

- Address-first red/white/gray interface with New Session, Recent Sessions,
  Favorites, Address Book, Incoming Sessions and Settings.
- Persistent numeric IDs, Windows-protected device keys, HTTPS registration and
  lookup, fresh ownership challenges, online leases and certificate checking.
- Per-viewer approval, default five viewers (1–10), view/control permissions,
  transfer/revoke control, remove one viewer and Stop All.
- Chat, text clipboard send and bidirectional file transfer. Clipboard and file
  access are **off by default** and must be allowed in the host approval dialog.
- Screenshots, fullscreen, fit-to-window rendering and MJPEG AVI recording.
- Live host monitor switching, shared by all viewers; stale control input is
  rejected across display/control changes.
- Local recent devices, friendly names, favorites and an editable address book.

### During a session

The host manages connected users under **Incoming sessions**. **Give control**
transfers the single controller role; **Revoke control** releases held input.
After **Allow control**, **Send keyboard and mouse** is enabled automatically.
Click the screen to focus it; use that checkbox to pause/resume input.
**Ctrl+Alt+F12** releases remote input locally; losing focus also releases it.
Windows-reserved shortcuts may remain local.

Chat is per viewer. **Send clipboard** explicitly sends text (up to 2,000
characters); it is not automatic clipboard synchronization. **Send file** lets
you choose a file up to 100 MiB. The receiver chooses a save path. Files are
verified with SHA-256 and incomplete transfers are cleaned up. Files are not
automatically executed. There is no arbitrary remote-filesystem browsing.

**Screenshot** saves PNG. **Record** starts/stops a local MJPEG AVI recording,
without audio. A display-size change ends the current recording so its stream
dimensions stay valid. Recording is limited to about 1.5 GB per file. **Fullscreen**
toggles the viewer window; Escape exits it when remote input is released.

The host can change its selected monitor while online; all viewers see the
new shared monitor. **Remove viewer** affects that viewer; **Stop All** goes
offline and disconnects everyone. Every reconnection requires approval.
OpenDesk filters generated input on approval, trust settings and permission
controls, and its viewer windows to prevent feedback. Ordinary host navigation
accepts authorized input. This guard is not a sandbox for a desktop controller.

Choose **Fast** on the host for less bandwidth, **Balanced** for normal use, or
**Sharp** for more detail. Capture targets up to 30 fps; actual rate depends on
screen capture, content, CPU, bandwidth and relay round-trip time. It still uses
GDI/JPEG. In the current public-relay probe, click delivery was roughly 300 ms
with an occasional 900 ms spike; this is not instant WAN performance.

## Service operation and storage

`OpenDesk.Service.exe` combines the HTTPS device directory with the relay.
Run it directly for a visible console, or start/stop the default local instance
from Settings. It has no Windows service installation or startup persistence.

```powershell
.\OpenDesk.Service.exe
.\OpenDesk.Service.exe --data C:\OpenDeskData --port 47834 --relay-port 47832
.\OpenDesk.Service.exe --stop # stops the default instance owned by this Windows user
```

Console Ctrl+C stops a manually run instance. Default local service control
uses a current-user-only named pipe. **Stop local service** disconnects devices
using that local service; it does not stop a remote server.

Default server data: `%LOCALAPPDATA%\OpenDeskServer`. Preserve `devices.json`
and `service-identity.pfx` across server upgrades. The registry associates IDs
with public-key fingerprints; it does not persist active invitation secrets.
The PFX contains the service private key and must stay in a directory accessible
only to its operator. The service fingerprint is public and can be shared with
your clients over a trusted channel. No invitation/private key is written to logs.

Client settings/contacts: `%LOCALAPPDATA%\OpenDesk\settings.json`. The device
signing key is a non-exportable Windows current-user CNG key named
`OpenDesk.DeviceIdentity.v1`. Copying settings alone does not copy that credential.
Deleting the credential or server registry requires a recovery/reset decision;
the app does not silently claim somebody else's ID.

The directory is a trust root for first-time ID lookup. Its HTTPS certificate
must be valid under normal platform trust, or exactly match the explicitly
configured fingerprint. Lookup/session metadata is not returned over plaintext.
After lookup, the viewer pins its separate TLS session to the resolved host.
The relay forwards that encrypted session. Relay routing metadata is visible
on the relay transport. This is a self-hosted development service, not an
anonymous or globally operated production network.

## Limits and feature status

See **FEATURE-PARITY.md** for what is implemented, partial or missing.
The app uses GDI/JPEG at target 10 fps and up to 1600 pixels wide; it does not
claim AnyDesk/DeskRT performance. It has no audio, remote printing, unattended
passwords, automatic NAT traversal, enterprise portal or other-OS clients.
Windows elevated/secure desktops, UAC, lock screens, remote reboot and privacy
blanking are not implemented. Windows may block input to elevated apps; control
is revoked while viewing continues. These features are not shown as fake buttons.

## Build and verification

Requires the .NET 10 SDK on Windows. Product dependencies are framework
libraries. Tests use real TCP/TLS/HTTPS plus simulated capture/input where noted.

```powershell
dotnet build OpenDesk.slnx -c Release
dotnet run --project Core.Tests -c Release
dotnet run --project App.Smoke -c Release
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

`App.Smoke` uses actual Windows capture, protected test keys, numeric-ID lookup,
relay and real viewer rendering, without injecting desktop input. Test CNG keys
are removed. Its local recording test can be checked with FFprobe/FFmpeg; those
tools are verification tools only, not application dependencies.

GUI diagnostic switches: `--smoke`, `--ui-smoke` (renders the home screen), and
`--capture-smoke` (logs only dimensions/encoded size). These do not automatically
go online. The service and legacy relay each support `--smoke` on loopback.

Actual results, including the one-PC latency measurement and outstanding
two-device checks, are recorded in VERIFICATION.md. No two-PC LAN/WAN, hardware
input, every keyboard/DPI setup or independent security audit is claimed.
