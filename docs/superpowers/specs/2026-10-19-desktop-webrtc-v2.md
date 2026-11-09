# OpenDesk desktop v2: WebRTC sessions on Windows

Date: 2026-10-19. Status: approved for implementation.
Parent plan: `2026-10-19-cross-platform-architecture.md` (this is its phase 1, Windows only).

## Decisions (confirmed)

- WebRTC engine: Chromium inside WebView2 (Evergreen runtime, preinstalled on Windows 11).
- Windows only; the WinForms app stays. Avalonia and other platforms come later.
- The VPS runs the updated service plus coturn; the owner deploys it.
- The app speaks v2 only. The service keeps its v1 endpoints so 0.3 installs keep
  working until they are retired; the new app does not use them.

## What stays the same

Numeric IDs, device keys (`OpenDesk.DeviceIdentity.v1` CNG key, P-256), the
directory as trust root, pinned service certificate, attended approval for every
viewer, view/control/clipboard/file permissions, one controller at a time with
epochs (`HostCoordinator`), chat, clipboard text, file transfer up to 100 MiB with
SHA-256, screenshots, recording, fullscreen, monitor switching, quality presets,
recent sessions, favorites and address book.

## Components

```
OpenDesk.App (host side)                              OpenDesk.Service (VPS)
 MainForm ── HostEngine ─┬─ SignalingClient ══ wss /v2/host ══╗   SignalingHub
                         ├─ PeerHost (Core): approval,        ║   DeviceRegistry (IDs)
                         │   HostCoordinator, permissions      ║   TURN credentials
                         ├─ VideoPipeline: Desktop Duplication ║   (coturn alongside)
                         │   → NV12 + cursor → MfEncoder H.264 ║
                         └─ hidden WebView2 host.html:         ║
                             one RTCPeerConnection per viewer  ║
OpenDesk.App (viewer side)                                    ║
 ViewerForm ─┬─ SignalingClient ══ wss /v2/connect/{id} ══════╝
             ├─ PeerViewer (Core): status, permissions, messages
             └─ WebView2 viewer.html: RTCPeerConnection, WebCodecs decode,
                canvas display, pointer/keyboard capture, recording
```

## Flows

### 1. Going online (host)

1. MainForm loads the device key and opens `wss://service/v2/host` with the pinned
   service certificate.
2. Service sends `{type:"challenge", challenge}` (fresh, 60 s, single use).
3. Host sends `{type:"register", publicKey, signature}` where the signature is
   ECDSA P-256 over `SignalProof.Register(challenge, publicKey)`.
4. Service verifies the key and signature, maps the key fingerprint to the
   persistent nine-digit ID (same `devices.json` as v1, so IDs survive), refuses if
   that ID is already online over another socket, and replies
   `{type:"registered", id, ice:[...]}` with short-lived TURN credentials.
5. The host is online while that socket lives. WebSocket keep-alives every 15 s.
   If it drops, the host reconnects with backoff (1, 2, 5, 10 s) and shows
   "Reconnecting"; active peer connections are not affected. If the same device
   registers from another window, the older socket receives `{type:"replaced"}` and
   stays offline instead of reconnecting.

### 2. Connecting (viewer) and approval

1. Viewer opens `wss://service/v2/connect/{id}`. Service replies
   `{type:"ready", session, publicKey, ice}` or `{type:"error", reason}` (offline).
2. The viewer compares the host key fingerprint with the one saved for that
   address (`KnownKeys`, trust on first use). A changed key stops the connection
   with an explanation; removing the contact forgets it. Renaming does not.
3. viewer.html creates the peer connection and four data channels, gathers ICE
   candidates (complete, or 3 s cap) and returns the offer. The viewer sends
   `{type:"offer", name, sdp}`.
4. Service forwards `{type:"offer", session, name, sdp}` to the host socket.
5. Host checks capacity and pending requests (at most 4), then shows the approval
   dialog (60 s). No peer connection exists before approval, so unapproved viewers
   learn nothing about the host's network addresses.
6. Declined, full or timed out: host sends `{type:"reject", session, reason}`.
7. Approved: `HostCoordinator.Add` assigns permissions; host.html answers the
   offer (complete gathering, 3 s cap); the host signs
   `SignalProof.Answer(id, session, offerSdp, answerSdp)` and sends
   `{type:"answer", session, sdp, signature}`.
8. Viewer verifies the signature with the host key from step 1, then applies the
   answer. The service closes the viewer socket once it relays the answer.
9. When the peer's `control` channel opens, the host sends `Accepted(status)`, and
   the video pipeline sends a keyframe. Connection setup has a 90 s overall limit.

The signed answer binds both SDPs, and therefore both DTLS fingerprints, to the host
device key: the service can refuse or delay a session but cannot read or alter it.

### 3. Data channels (created by the viewer, all reliable and ordered)

| Channel | Content |
| --- | --- |
| `control` | Core `Message` frames (kind, length, payload): Accepted, Rejected, Status, Chat, Clipboard, Keyframe request |
| `input` | `Message(Input, InputEvent JSON)` sent by viewer.html directly |
| `files` | FileOffer, FileChunk, FileFinish, FileAck |
| `video` | Host to viewer: flags (1 byte, bit 0 = keyframe), display generation (8), timestamp (8), length (4), H.264 Annex B; split into 60 KB messages |

Every message sent through the bridge completes only when the page reports its channel
drained below 1 MB (or fails when the channel is closed), so files cannot pile up in the
page and a lost session fails sends on every channel. Each peer's incoming messages are
handled in order through a bounded inbox; a peer that floods it is disconnected. The
message kind numbers the pages use are injected from the C# `MessageKind` enum.

### 4. Video

- Desktop Duplication on the selected monitor; NV12 conversion with scaling on the
  CPU; the cursor arrow drawn into the frame (Desktop Duplication omits it).
  Cursor-only movement re-encodes the last image. Conversions are paced to 60 per
  second and only happen when the encoder can take a frame; changes in between stay
  in the duplication mirror. With nothing to do, the thread waits inside Desktop
  Duplication (which also wakes on pointer movement) instead of polling.
- Quality presets map to output width: Fast 1024, Balanced 1280, Sharp 1600 px
  (never upscaled), at 4, 8 and 14 Mbit/s.
- Media Foundation H.264: hardware encoders are tried in system order (one that
  fails to start, such as NVENC on some hybrid laptops, is skipped); the Microsoft
  software encoder is the fallback. Low-latency mode, no B-frames, CBR.
- Only the newest unencoded frame is kept. The pipeline runs only while at least
  one approved viewer is connected.
- Host fan-out (host.html): each approved peer gets every frame while its
  `video` channel has less than 1 MB buffered. A peer above that skips frames until
  the next keyframe, and the host requests one. New peers and decoder errors also
  request keyframes. The video pipeline alone rate-limits them, to one per 500 ms.
- Monitor or quality changes restart the encoder at the new size and bump both the
  control epoch and the display generation (`SessionStatus.Display`). Frames carry
  the display generation they were captured for. Control changes bump only the
  epoch, so they need no new frame.
- If duplication is lost (secure desktop, display change), the pipeline retries
  every second and the viewer shows the last frame.

### 5. Input

- viewer.html captures pointer, wheel and keyboard on the canvas while the viewer has
  control, input is enabled, and the last shown frame is of the current display
  generation (so coordinates match the host's monitor). Each event carries the
  status epoch, which the host checks.
- Coordinates are normalized to the image (letterboxing excluded). Buttons: 1 left,
  2 right, 3 middle. Wheel: 120 per notch, clamped to ±1200.
- Keys carry W3C `KeyboardEvent.code`; the host maps them to Windows virtual keys
  (`KeyCodes`). Unknown codes are ignored.
- Blur, hidden page, Ctrl+Alt+F12, or input switched off send `reset`, which releases
  held keys and buttons on the host. The host still validates everything in
  `HostCoordinator` (controller, epoch, ranges).

### 6. Session tools

- Chat and clipboard text: `control` channel; clipboard and files only when approved.
- Files: `files` channel; `FileTransfer` unchanged.
- Screenshot: viewer.html returns the canvas as PNG; the viewer saves it.
- Recording: `MediaRecorder` on the canvas stream (WebM/VP8, 30 fps), written to the
  chosen file in chunks; capped at about 1.5 GB.

### 7. Ending

Viewer Disconnect, host Remove viewer, Stop All, going offline, or a failed peer
connection all close that peer; `HostCoordinator.Remove` releases held input.

## Service additions

- `GET /v2/info` returns `{protocol: 2, signaling: true}`.
- `wss /v2/host` and `wss /v2/connect/{id}` as above. Limits: 32 KB messages,
  14 open viewer sessions per host, 64 viewer sockets per IP per minute (the existing
  HTTP rate limiter covers upgrades), and timeouts as listed.
- TURN: `--turn-url host:port` and `--turn-secret-file path` produce STUN and TURN
  (UDP and TCP) entries with coturn REST credentials (`expiry:user`,
  HMAC-SHA1, 1 h). Without them, only host candidates are offered (LAN only).

## WebView2 configuration

One shared environment under `%LOCALAPPDATA%\OpenDesk\WebView2`. Pages are embedded
resources served from `https://app.opendesk/`. Any other navigation is
blocked, web messages are accepted only from that origin, dev tools, context
menus, zoom and browser shortcuts are off, and background throttling is off so a
hidden or minimized host page keeps full speed.

## Verification

- Core tests: proofs, TURN credentials, frame packing, registry signaling,
  PeerHost permissions and approval, signaling hub end to end.
- App smoke: local service, two app homes, real approval dialog, WebRTC loopback,
  first frame time, 20 clicks forwarded in order, and click-to-screen latency
  through the real app (target p50 ≤ 80 ms on one PC).
- Owner test: multiple Windows PCs on a LAN, then on different networks through the
  VPS; record direct versus TURN and p50/p95 from the viewer stats overlay.

## Not in this phase

Unattended access, elevated or secure desktop control, audio, multi-monitor
streaming at once, Mac/Linux/mobile, and retiring v1 from the service.
