# Cross-platform OpenDesk: architecture plan

Date: 2026-10-19. Status: proposal, not started.

## Goal

OpenDesk runs on Windows, macOS, Linux, Android and iOS. Every platform can
view and control others. Every platform except iOS can also be controlled.
iOS can share its screen view-only, because Apple allows no remote input.
Attended access stays: the person at the host approves each viewer, and one
viewer at most holds control.

## Decisions

| Decision | Choice | Why |
| --- | --- | --- |
| Transport | WebRTC (ICE, DTLS, SCTP data channels), TURN as fallback | Direct paths through NAT; available on every platform |
| Video | H.264 Annex B over a data channel, not WebRTC media tracks | Measured 2x lower latency than WebRTC video (below) |
| Language | .NET 10, reusing `OpenDesk.Core` | Existing code, tests and team knowledge |
| UI | Avalonia, one shared UI for desktop and mobile heads | Covers Windows, macOS, Linux, Android, iOS (MAUI lacks Linux) |
| Order | Viewer everywhere first, then hosts: macOS, Android, Linux; iOS view-only | Viewers are cheap and prove the protocol against the existing Windows host |

## Measured basis

From the `OpenDesk/WebRtcProbe` prototype on one Windows laptop (1920x1080 desktop,
loopback network, 40 clicks per run, click-to-screen through capture, encode,
data channel, decode and draw):

| Pipeline | p50 | p95 |
| --- | --- | --- |
| WebRTC screen share (Chromium, VP8/H.264, eight runs) | 109-182 ms | 175-280 ms |
| Desktop Duplication, Media Foundation H.264 (Intel), 1080p | 74 ms | 111 ms |
| **Desktop Duplication, Media Foundation H.264 (Intel), 720p** | **55 ms** | **67 ms** |
| Data channel round trip alone | 2-3 ms | 7 ms |

What made the difference: no fixed 30 fps capture cap, no jitter buffer, no
frame pacing, and an encoder input that keeps only the newest frame. WebCodecs
encoding in the page fell behind at high capture rates (87-145 ms encodes);
encoding natively per platform is required. NVENC was not reachable from the
Intel-driven display process on this Optimus laptop and is untested.

These are single-PC numbers. Over the internet the network path adds its own
latency (about 250 ms per leg to the current VPS from Nepal), which is why direct
ICE paths and a nearby TURN server matter as much as the pipeline.

## Architecture

```
            Host (Windows/macOS/Linux/Android; iOS view-only)          Viewer (all five)
 ┌───────────────────────────────────────────────────────┐   ┌──────────────────────────────┐
 │ IScreenCapture ─▶ newest-frame slot ─▶ IVideoEncoder ─┼─┐ │ IVideoDecoder ─▶ Avalonia view│
 │ IInputInjector ◀─ HostCoordinator (one controller)  ◀─┼┐│ │ input capture ─▶ InputEvent v2│
 │ IDeviceKeyStore    approval UI (Avalonia)             ││└▶│ "video" channel               │
 └──────────────────────────┬────────────────────────────┘└──│ "control"/"input"/"files"     │
                            │ WebRTC peer connection (direct, else TURN)   └──────────────────┘
                            ▼
        OpenDesk.Service: HTTPS directory + signaling WebSocket + coturn (STUN/TURN)
```

### Protocol v2

**Signaling.** The host keeps one outbound WebSocket (WSS) to the directory instead of
today's relay registration socket. A viewer looks up a numeric ID, then posts
its SDP offer to the directory, which forwards it over the host's WebSocket.
The host answers the same way. ICE candidates are exchanged the same way, or
bundled into the SDP.

**Peer identity.** The host signs its SDP answer (including the DTLS
fingerprint) with its device key. The viewer checks the signature against the
public key the directory returned for that ID, then pins the DTLS fingerprint.
This replaces today's pinned TLS certificate and invitation secret
(`SecureSession`). TURN credentials are short-lived and issued by the
directory per connection.

**Data channels**

| Channel | Mode | Carries |
| --- | --- | --- |
| `control` | reliable, ordered | Hello, Approval, SessionStatus, chat, clipboard, keyframe requests (JSON, existing records) |
| `input` | reliable, ordered | InputEvent v2 |
| `video` | reliable, ordered | Encoded frames: 1 byte flags, 8 byte timestamp, 4 byte length, data, split into 60 KB messages (as in the probe) |
| `files` | reliable, ordered | FileOffer / chunks / FileAck, separate so files never delay video |

**Video rules**
- H.264, no B-frames, low-latency mode, infinite GOP with keyframes on request.
- 720p default, 1080p option (the existing Fast/Balanced/Sharp setting maps here).
- One encoder per host. Each viewer gets the same frames.
- Before sending to a viewer, check its `video` channel `bufferedAmount`. A
  viewer that falls behind skips frames until the next keyframe, then asks for one.
- A new viewer or a decoder error triggers `control: {"keyframe": true}`.
- Later, if WAN tests show stalls, add a partially reliable `video` channel
  (`maxRetransmits: 0`) with keyframe recovery. Start reliable because it is simpler.

**InputEvent v2.** Today `Code` is a Windows virtual-key code (1-255).
v2 sends the W3C `KeyboardEvent.code` string ("KeyA", "ShiftLeft") plus
optional text for IME/character input. Each host has a mapping table to its
native key codes. Pointer events keep normalized 0..1 coordinates
(`ViewerCoordinates`) and add touch/scroll deltas for mobile viewers.

**Compatibility.** The directory advertises protocol versions per device.
v1 (TCP/TLS, JPEG) stays on Windows until all users run v2 clients. New
platforms speak only v2.

### WebRTC implementation for .NET

Video travels over data channels, so we need ICE, DTLS and SCTP only, not
WebRTC's media stack. That makes two options realistic:

1. **SIPSorcery**: pure C#, one implementation on all five platforms. Risk: its
   SCTP throughput and latency are unproven at 10-20 Mbit/s.
2. **libdatachannel**: small C/C++ library, built per platform and called
   through P/Invoke. It is proven fast, but adds native builds for 5 platforms x 2-3 architectures.

Phase 0 decides between them with a measurement, not opinion.

### Projects

| Project | Target | Contents |
| --- | --- | --- |
| `OpenDesk.Core` | net10.0 | Protocol records, session logic, directory client, file transfer (see reuse table) |
| `OpenDesk.Transport` | net10.0 | WebRTC peer, channel framing, signaling client |
| `OpenDesk.Platform.Windows` | net10.0-windows | Desktop Duplication, Media Foundation encoder/decoder, SendInput, CNG keys |
| `OpenDesk.Platform.MacOS` | net10.0-macos | ScreenCaptureKit, VideoToolbox, CGEvent, Keychain |
| `OpenDesk.Platform.Linux` | net10.0 | PipeWire/portal or X11 capture, VA-API/FFmpeg, portal/uinput input, libsecret |
| `OpenDesk.Platform.Android` | net10.0-android | MediaProjection, MediaCodec, AccessibilityService, Android Keystore |
| `OpenDesk.Platform.iOS` | net10.0-ios | VideoToolbox decode, Keychain; ReplayKit broadcast extension (phase 6) |
| `OpenDesk.UI` | net10.0 | Avalonia views and view models shared by all heads |
| `OpenDesk.Desktop`, `.Android`, `.iOS` | per head | Avalonia entry points, packaging |
| `OpenDesk.Service` | net10.0 | Directory + signaling WebSocket; coturn runs beside it |

Platform interfaces live in Core so session logic stays platform-free:
`IScreenCapture`, `IVideoEncoder`, `IVideoDecoder`, `IInputInjector`,
`IDeviceKeyStore`, `IClipboard`. `IScreenSource`/`IInputSink` evolve into these.

### Reuse from Core

`OpenDesk.Core` is already platform-neutral: its only Windows reference is a
notice string in `HostService.cs`.

| File | Plan |
| --- | --- |
| `SessionMessages.cs` (Approval, SessionStatus, ViewerInfo, InputEvent, ViewerCoordinates) | Keep. InputEvent gains v2 key fields. `ScreenFrame`/`TileUpdate` stay for v1 only |
| `HostCoordinator.cs` | Keep: single-controller policy, epochs, approval flow |
| `InputQueue.cs`, `TextBudget.cs` | Keep |
| `FileTransfer.cs` | Keep, sent over the `files` channel |
| `DeviceDirectory.cs` (`DeviceProof`, `DeviceRegistry`, ID format) | Keep. Registration signs a signaling endpoint instead of a relay invitation |
| `DirectoryClient.cs` | Keep. Add offer/answer and TURN credential calls |
| `Invitation.cs` | Replace by signed SDP plus device ID; keep for v1 |
| `SecureSession.cs`, `Wire.cs`, `RelayProtocol.cs`, `ViewerSession.cs`, `HostService*.cs` | v1 only. v2 equivalents live in `OpenDesk.Transport` and reuse the session state machine |
| `FramePacer.cs` | Replaced by the newest-frame slot plus `bufferedAmount` checks |
| `MjpegRecording.cs`, `JpegDimensions.cs` | v1 only. v2 recording writes H.264 into MP4 |
| `App/ClientSettings.LoadDeviceKey` (CNG) | Moves behind `IDeviceKeyStore`; P-256 ECDSA stays the key type on all platforms |
| `App/DesktopDuplication.cs`, `WebRtcProbe/MfEncoder.cs` | Become the Windows capture and encoder implementations |

## Per-platform host notes

| Platform | Capture | Encode | Input | Permissions and limits |
| --- | --- | --- | --- | --- |
| Windows | Desktop Duplication | Media Foundation hardware H.264, keeping only the newest frame | SendInput | No elevated/secure desktop (unchanged) |
| macOS | ScreenCaptureKit | VideoToolbox, real-time mode, no frame reordering | CGEvent posting | Screen Recording and Accessibility permissions; notarized, hardened runtime |
| Linux | xdg-desktop-portal ScreenCast + PipeWire (Wayland); XShm/XDamage (X11) | VA-API via FFmpeg, x264 fallback | Portal RemoteDesktop (Wayland); XTest (X11) | Wayland asks the user per session unless the portal remembers consent |
| Android | MediaProjection | MediaCodec, low-latency where supported | AccessibilityService gestures | Consent every session; Google Play reviews AccessibilityService use, so a policy declaration is needed |
| iOS | ReplayKit broadcast extension | VideoToolbox inside the extension | None (Apple forbids it) | View-only; the extension has a memory limit of about 50 MB |

## Phases

Each phase ends with a measured result, using the probe's marker method
ported to each platform.

**Phase 0: Protocol and transport spike (S/M)**
- Write protocol v2 in detail: signaling messages, channel framing, InputEvent v2 key map.
- Benchmark SIPSorcery against libdatachannel on Windows and Android:
  data-channel round trip, 15 Mbit/s throughput, latency while files transfer.
- Exit: transport chosen; data-channel round trip p50 under 5 ms on a LAN;
  15 Mbit/s sustained without video stalls.

**Phase 1: Windows host speaks v2 (M)**
- Productize the probe path: Desktop Duplication, `MfEncoder`, newest-frame slot, fan-out to viewers.
- Directory signaling WebSocket, signed answers, coturn deployed on a nearby VPS (Mumbai candidate).
- Keep v1 alongside.
- Exit: a v2 test viewer controls the Windows host. Two-PC LAN click-to-screen
  p50 ≤ 80 ms. Two Nepal networks: record how often ICE connects directly versus through TURN, plus p50/p95.

**Phase 2: Avalonia viewer on all five platforms (L)**
- Shared Avalonia UI: address-first home, recent/favorites, approval states, chat, files.
- Decoders: Media Foundation (Windows), VideoToolbox (macOS, iOS), MediaCodec (Android), FFmpeg/VA-API (Linux).
- Touch input mapping for mobile: tap = click, two-finger scroll, on-screen keyboard to InputEvent v2.
- Exit: each platform's viewer controls the Windows host with LAN p50 ≤ 80 ms.
  Decode-to-display ≤ 10 ms per platform.

**Phase 3: macOS host (M)**
- ScreenCaptureKit, VideoToolbox, CGEvent, Keychain device key, permission onboarding.
- Exit: every viewer controls a Mac; LAN p50 ≤ 80 ms; permissions flow tested on a fresh account.

**Phase 4: Android host (M/L)**
- MediaProjection, MediaCodec, AccessibilityService input, foreground service, Keystore key.
- Exit: every viewer controls a phone; Play policy declaration drafted; tested on at least two OEMs.

**Phase 5: Linux host (M)**
- X11 path first, then Wayland portal path (GNOME and KDE).
- Exit: viewers control X11 and one Wayland desktop.

**Phase 6: iOS view-only sharing (M)**
- ReplayKit broadcast extension encoding with VideoToolbox, signaling through the app group.
- Exit: iPhone screen visible on all viewers; extension stays within its memory limit for 30 minutes.

**Phase 7: v1 retirement and hardening (S/M)**
- Remove v1 after the v2 Windows client has shipped and old clients are gone.
- Security review of signaling, TURN credentials and key stores.
- Store and signing pipeline: Windows code signing, macOS notarization, Play, App Store.

Sizes are relative (S/M/L), not estimates.

## Risks and open questions

1. **Data-channel performance in .NET.** If neither option meets the phase 0
   targets, fall back to WebRTC in a WebView, as in the probe.
2. **Android remote control policy.** Google Play may restrict
   AccessibilityService-based control. Outside the Play Store (direct APK) avoids this
   but limits reach.
3. **Linux Wayland consent.** Unattended reconnects may be impossible on some
   desktops; attended access matches the product model anyway.
4. **Decoder variety.** Four decoder backends. FFmpeg everywhere is an
   alternative that trades simplicity for LGPL obligations and app size.
5. **NAT reality.** Carrier-grade NAT may push many sessions to TURN, so the
   TURN server's location and bandwidth are product decisions, not afterthoughts.
6. **Hardware encoder selection on hybrid laptops.** NVENC failed to activate
   in the probe. Prefer the encoder on the display adapter; measure others.
7. **Open:** keep the numeric-ID directory on the same VPS, or split the
   signaling and TURN servers? Decide after phase 1 load measurements.
