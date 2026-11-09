# OpenDesk 0.4 · Windows remote desktop

OpenDesk uses the familiar numeric-address workflow: your computer shows
**Your address**, and you enter another computer's number in **Remote address**.
Every viewer still needs host approval. Multiple viewers can watch, with one
remote keyboard/mouse controller. The app has its own branding and code.

0.4 replaces the session transport. Sessions now run over WebRTC: the two PCs
connect directly when their networks allow it, and through a TURN relay on the
device service when they do not. Video is H.264 from Desktop Duplication and the
PC's hardware encoder, decoded with the GPU on the viewer. On one PC this measured
about 55 ms click-to-screen at 720p, against roughly 300 ms for 0.3 through the VPS.
Keys are sent by position, so the host's own keyboard layout applies.

**0.4 only talks to 0.4.** Install it on every PC, and update the device service
first (see below). 0.3 installs keep working with the updated service until they
are replaced.

## Get started

Extract `OpenDesk-Windows-x64.zip` and run `OpenDesk.exe`. Keep the included
`OpenDesk.Service.exe` beside it if you want the local-service setup button. Both are
self-contained Windows x64 builds; no .NET installation is needed. They are unsigned.
OpenDesk needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes;
on Windows 10 install it from Microsoft if OpenDesk asks for it.

### Default public device network

New profiles use the owner's VPS at `https://139.99.135.69:47834`, with this trusted
certificate fingerprint built in:

```text
5B80725FF3E6DC733FADB7AF55727DA4C622D5FB82C9AC4EE04B7C60A774AEEF
```

The VPS must run the 0.4 service; going online with an older service shows
"This device service is too old". Upgrade steps, including TURN for sessions across
different networks, are in [deploy/linux/README.md](deploy/linux/README.md).
Profiles with an empty service address adopt the public default; Settings can still
choose a different service. Device numbers resolve through OpenDesk's service, not
AnyDesk's network.

### Optional: a local or LAN service

1. On the service PC, open **Settings → Start local service**. It fills in its local
   HTTPS address and certificate fingerprint. Select **Save settings**.
2. On other PCs, configure **Settings** with `https://SERVICE-PC-IP:47834` and the
   **same fingerprint**. The service PC must allow TCP **47834** inbound.
3. Without TURN, a local service connects PCs on the same network (or through routers
   that permit direct paths). That is enough for LAN testing.

## Testing with several desktops

1. Upgrade the VPS service (with TURN), or start a local service for a LAN.
2. Install 0.4 on each PC. Optional check per PC: `OpenDesk.exe --capture-smoke` writes
   `capture-smoke.txt` beside the exe with the display size and the H.264 encoder used.
3. Open OpenDesk on each PC and wait for its nine-digit address.
4. On one PC enter another's address and select **Connect**. The viewer window opens at
   once and shows each step: finding the device, waiting for approval, connecting.
5. Approve on the host. Try viewing, **Allow control**, typing (including your own
   keyboard layout), chat, clipboard, files, screenshot, recording and monitor switching.
6. Select **Stats** in the viewer window for frame rate, decode time, bitrate, whether
   the path is **direct** or **relayed (TURN)**, and the round trip. The host's
   **Incoming sessions** list also shows direct or relayed per viewer.
7. Connect a second and third viewer to one host; give and revoke control between them.

Useful things to note when reporting results: both networks (home, office, mobile
hotspot), direct or relayed, round trip, and how clicks and typing feel.

## Working features

- Classic desktop layout with a prominent local address, recent-device cards,
  selected navigation, and consistent approval, settings and chat windows.
  **Display & sharing** expands capture settings; the viewer toolbar moves
  extra actions into its overflow menu when the window is narrow.
- Persistent numeric IDs, Windows-protected device keys, fresh ownership challenges,
  pinned service certificate. The viewer remembers each device's key and refuses a
  silent change (remove the contact to accept a reinstalled PC).
- Per-viewer approval, default five viewers (1–10), view/control permissions,
  transfer/revoke control, remove one viewer and Stop All.
- Chat, text clipboard send and bidirectional file transfer. Clipboard and file
  access are **off by default** and must be allowed in the host approval dialog.
- Screenshots (PNG), fullscreen, fit-to-window rendering and WebM recording.
- Live host monitor switching shared by all viewers; stale control input is rejected
  across display/control changes.
- Local recent devices, friendly names, favorites and an editable address book.

### During a session

The host manages connected users under **Incoming sessions**. **Give control**
transfers the single controller role; **Revoke control** releases held input.
After **Allow control**, **Send keyboard and mouse** is enabled automatically.
Click the screen to focus it; use that checkbox to pause/resume input.
**Ctrl+Alt+F12** releases remote input locally; losing focus also releases it.
Windows-reserved shortcuts may remain local.

Chat is per viewer. **Send clipboard** explicitly sends text (up to 2,000
characters); it is not automatic clipboard synchronization. **Send file** sends a file
up to 100 MiB; the receiver chooses a save path, and files are verified with SHA-256.
Files are not executed. There is no remote-filesystem browsing.

**Screenshot** saves PNG. **Record** saves a WebM (VP8) video of the session, without
audio, up to about 1.5 GB per file. **Fullscreen** toggles the viewer window; Escape
exits it when remote input is off.

Choose **Fast** (1024 px), **Balanced** (1280 px) or **Sharp** (1600 px) on the host.
Video is sent only when the screen changes, up to 60 frames per second. If the host's
display turns off or shows a secure desktop, viewers keep the last image; remote mouse
input wakes the display and video resumes.

## UI verification

From this directory, run `dotnet run --project App.Smoke -c Release -- --ui`.
It uses isolated fixture profiles, captures the existing screens at default and
minimum sizes, and checks layout, contrast, navigation, permission defaults,
keyboard handlers and protected toolbar overflow. Screenshots and `result.txt`
are in `App.Smoke/bin/Release/net10.0-windows/ui-checks/`. The report records the
actual display DPI; it does not claim coverage of unavailable DPI configurations.
`OpenDesk.exe --ui-smoke` also captures an isolated home screen as `ui-smoke.png`
beside the executable, without connecting to a service or loading your profile.

Run the same smoke project without `--ui` for the real local-service session,
approval, video and input check. It needs an active display and WebView2.

## How a session works

1. Online hosts keep one WebSocket to the device service and prove their device key.
2. A viewer looks up the number, receives the host's public key, and sends a WebRTC
   offer with its display name.
3. The host owner approves first. Only then does the host create the connection, so an
   unapproved viewer learns nothing about the host's network.
4. The host signs its answer with its device key. The signature covers both sides'
   encryption fingerprints, so the service cannot read or alter sessions.
5. Video, input, chat and files then flow over encrypted WebRTC data channels, directly
   or through TURN.

Details: `../docs/superpowers/specs/2026-10-19-desktop-webrtc-v2.md`.

## Service operation and storage

`OpenDesk.Service.exe` combines the HTTPS directory, v2 signaling and the 0.3 relay.
Run it directly for a visible console, or start/stop the default local instance from
Settings. It has no Windows service installation or startup persistence.

```powershell
.\OpenDesk.Service.exe
.\OpenDesk.Service.exe --data C:\OpenDeskData --port 47834 --relay-port 47832
.\OpenDesk.Service.exe --turn-url 203.0.113.10:3478 --turn-secret-file C:\OpenDeskData\turn-secret
.\OpenDesk.Service.exe --stop # stops the default instance owned by this Windows user
```

Default server data: `%LOCALAPPDATA%\OpenDeskServer`. Preserve `devices.json` and
`service-identity.pfx` across upgrades. The PFX holds the service private key; keep it
private. Client settings/contacts: `%LOCALAPPDATA%\OpenDesk\settings.json`; WebView2
data: `%LOCALAPPDATA%\OpenDesk\WebView2`. The device signing key is a non-exportable
Windows current-user CNG key named `OpenDesk.DeviceIdentity.v1`.

## Limits

No audio, unattended access, remote printing, other-OS clients, multi-monitor streaming
at once, or control of elevated/secure desktops (UAC, lock screen). Windows may block
input to elevated apps; control is then revoked while viewing continues. Measured
latency so far is from one PC over loopback; two-PC LAN and WAN results are pending
your tests. TURN relays count against the VPS bandwidth.

## Build and verification

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build OpenDesk.slnx -c Release
dotnet run --project Core.Tests -c Release
dotnet run --project App.Smoke -c Release   # needs the display on; writes session-smoke.txt
powershell -ExecutionPolicy Bypass -File .\publish.ps1
powershell -ExecutionPolicy Bypass -File .\publish-linux.ps1
```

`App.Smoke` runs the whole path through the real app on one PC: local service, two app
homes, the real approval dialog, WebRTC, Desktop Duplication, H.264 and 20 clicks, and
reports click-to-screen p50/p95. It injects no OS input. `WebRtcProbe` holds the
latency experiments that led to this design.
