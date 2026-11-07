# OpenDesk Viewer (Avalonia, cross-platform)

The first milestone of the cross-platform plan
(`docs/superpowers/specs/2026-10-19-cross-platform-architecture.md`, phase 2, viewer first):
a desktop viewer for Windows, macOS and Linux that connects to the existing Windows host
(OpenDesk 0.4, protocol v2) through the same device service.

## What it does

- Address-first home: nine-digit address, recent devices (with Forget), service settings.
- v2 signaling through the device service, with the pinned service certificate.
- Trust on first use of the host's device key, and a check of the host's signed answer, which binds
  both SDPs (and so both DTLS fingerprints) to that key. Same rules as the Windows `ViewerForm`.
- WebRTC data channels (`control`, `input`, `files`, `video`) in .NET with SIPSorcery: no browser engine.
- H.264 decode with the system FFmpeg libraries (FFmpeg 4 to 8), newest-picture display,
  skip-to-keyframe when decoding falls behind, keyframe requests after errors.
- Mouse, wheel and keyboard (W3C `KeyboardEvent.code`) while the host grants control; input waits for
  a picture of the current display generation; Ctrl+Alt+F12 and focus loss release held input.
- Status updates (control granted or revoked), stats overlay (fps, decode time, bitrate), fullscreen.

Not yet: chat, clipboard, file transfer, screenshots and recording (Core already has the logic in
`PeerViewer` and `FileTransfer`; they need views), platform-native decoders (Media Foundation,
VideoToolbox), and the Android and iOS heads.

## Projects

| Project | Contents |
| --- | --- |
| `Transport` (`OpenDesk.Transport`) | `WebRtcPeer` (SIPSorcery peer with the v2 channels), `RemoteSession` (viewer flow), `VideoReassembler` (video channel framing) |
| `Video.FFmpeg` | `FfmpegDecoder`, an `IVideoDecoder` over libavcodec and libswscale |
| `UI` (`OpenDesk.UI`) | Avalonia views shared by every head: `MainWindow`, `SessionWindow`, `RemoteScreen`, settings |
| `Desktop` (`OpenDesk.Viewer`) | Desktop entry point; `--connect <address>` and `--probe <address>` |

`IVideoDecoder` lives in Core, so heads plug in their own decoder.

## Running

FFmpeg's shared libraries must be installed:

- Linux: the distribution's FFmpeg (for example `sudo apt install ffmpeg`).
- macOS: `brew install ffmpeg`.
- Windows: put the FFmpeg shared DLLs (`avcodec-*.dll`, `avutil-*.dll`, `swscale-*.dll`) next to
  `OpenDesk.Viewer.exe`, or set `OPENDESK_FFMPEG_DIR`.

```
dotnet run --project Desktop                       # the viewer window
dotnet run --project Desktop -- --connect 123456789
dotnet run --project Desktop -- --probe 123456789 --seconds 10   # no window: connect, decode, report
```

Settings live in `viewer.json` under the user's application data folder (`%APPDATA%\OpenDesk`,
`~/.config/OpenDesk`, `~/Library/Application Support/OpenDesk`); `OPENDESK_VIEWER_SETTINGS` points
elsewhere. The default service is the same public service as the Windows app.

## Verification

- `dotnet run --project Core.Tests`: video framing, FFmpeg decode, and the .NET viewer against a
  simulated host through a real local device service (approval, signed answer, video, input with
  epochs, status changes, chat, removal; declines, changed keys and bad signatures are refused).
- `node Desktop/interop/host-page-interop.mjs`: the viewer probe against the real `App/web/host.html`
  in Chromium, through a real local service. Add `--gui shot.png` under `xvfb-run` to screenshot the
  real window instead.
- Not yet verified: a real Windows host on another PC, NAT and TURN paths, macOS and Windows heads.
