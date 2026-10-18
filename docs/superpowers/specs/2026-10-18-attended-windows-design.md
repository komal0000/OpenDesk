# Windows attended remote desktop

## Confirmed requirements

- Build an independent remote-desktop application inspired by AnyDesk.
- Windows first.
- Attended access: the person at the host approves each session.
- Support multiple simultaneous viewers of the host's selected monitor.
- Preserve the existing reverse-engineering artifacts separately from product code.

## Proposed first release

One Windows x64 executable, named OpenDesk, offers Host and Viewer screens.
It runs in the signed-in desktop session. The host explicitly starts sharing,
selects one monitor, and copies an invitation. The viewer pastes it and supplies
a display name. A host approval dialog identifies that name as self-reported.
The host chooses view-only or grants keyboard/mouse control. An active-session
banner and Stop button remain visible. Stopping cancels capture and networking
and releases input state. Restarting hosting generates a new invitation.

Multiple viewers may connect concurrently, with separate approval and disconnect
controls for each viewer. Propose a default capacity of five approved viewers
and a bounded pending-approval queue; make capacity explicit in the host UI.
The host can remove one viewer or stop all sessions.

**Approved control policy:** all approved viewers may watch,
but only one viewer holds keyboard/mouse control at a time. The host grants,
revokes or transfers control explicitly. Transfer releases the previous
controller's held keys/buttons before enabling the next controller. Local host
input remains available. Simultaneous input from multiple remote viewers is an
alternative product policy and is outside this release.

Invitations expire after 15 minutes for starting a new
session; an approved active session can continue until either person disconnects.
Every reconnection requires approval. No service, startup persistence, unattended
password, clipboard, file transfer, audio, printing, or secure-desktop control in
this release. Elevated application input can be blocked by Windows integrity
levels; the app will explain this limitation rather than elevate itself.

## Implementation choices

C# / .NET 10 Windows Forms. Source lives in `OpenDesk/`. Use framework libraries
where possible. The first capture implementation uses Windows GDI via
`Graphics.CopyFromScreen`, JPEG frames, and a bounded frame rate (10 fps target,
maximum frame width 1600 pixels). This prioritizes a working complete session;
DXGI capture and a video codec are later performance work. No AnyDesk code,
icons, branding, codec, or embedded library is reused.

Use TLS with an ephemeral self-signed host certificate and an invitation containing
the certificate SHA-256 pin, a random 256-bit session secret, protocol version,
expiry, endpoint and connection mode. Validate invitations before connecting;
compare secret and certificate hashes in constant time. Invitations are sensitive
capabilities and must never be written to logs. They still require host approval.
Maintain bounded message sizes, handshake deadlines, input validation and
cancellation. Reject unsupported versions and unauthenticated input. No permanent
certificate private key is written to disk.

Two connection modes share the same encrypted protocol:

- Direct: host listens on a user-selected port; viewer connects over LAN/VPN or
  an explicitly configured reachable address. No automatic firewall changes.
- Relay: both clients connect outbound to a separately run .NET relay executable.
  An expiring random room supports multiple independently paired host/viewer
  tunnels, each with a unique channel identity. Host/viewer then negotiate
  the same pinned TLS connection through the relay. The relay forwards ciphertext
  and does not terminate session TLS. Bound pending rooms, connection count,
  pre-pairing headers, wait time and idle time. Do not deploy a public relay as
  part of local implementation.

The relay is a connectivity option, not a claim of AnyDesk protocol compatibility.
The first release uses TCP; it does not claim AnyDesk-equivalent latency or
adaptive performance.

## Components and lifecycle

- Core: invitation validation, framed protocol, TLS session establishment,
  direct/relay connection handling and cancellation.
- Host coordinator: track viewers, capacity, approval queue and controller lease;
  capture and encode once, then fan out frames with independent bounded queues.
- Host peer session: authenticate invitation secret, request UI approval, stream
  selected monitor, enforce the current controller lease and stop lifecycle.
  A slow or disconnected viewer must not block capture or other viewers; retain
  only its newest unsent frame and disconnect a peer whose writes time out.
- Viewer session: verify pinned host certificate, await approval, display frames,
  map input coordinates and release held keys/buttons on focus loss/disconnect.
- Windows input: bounded keyboard and pointer messages translated to SendInput;
  reject malformed input and never accept it in view-only mode.
- Relay: bounded pairing and byte forwarding, with cleanup on either disconnect.
  Clean up one channel without dropping the other channels in the room. Host
  disconnect closes all its channels and the room's registration.

Protocol messages are length-prefixed with a maximum 8 MiB payload. Handshake
and control JSON are limited to 16 KiB. Only approved sessions receive frames.
Received images are bounded by advertised dimensions and frame limits before
display. Coordinate mapping accounts for letterboxing and the host's selected
monitor, including negative desktop origins. Bound key/button state and release
it in a finally path. Never accumulate an unbounded frame or input queue.

## Verification and release

Automate invitation rejection, expiry/version checks, truncated/oversized frames,
certificate pin mismatch, invalid secret, host rejection, view-only input denial,
revocation/disconnect cleanup, and relay pairing isolation. Use loopback TLS
tests with multiple viewers to prove individual approval/removal, exclusive
control transfer, slow-peer isolation, capacity limits and stop-all cleanup.
Use loopback TLS
integration tests for the protocol, with test approval delegates and simulated
capture/input. Test capture on the local desktop only when explicitly exercised;
do not run an unattended interactive control session against the user's desktop.

Publish a Windows x64 executable and companion relay with run instructions.
State which runtime dependencies are included. Confirm GUI startup with a bounded
launch check. A real two-PC LAN/WAN session, visual quality, DPI/keyboards and
latency remain manual validation unless actually exercised on two devices.

## Sources

- https://learn.microsoft.com/en-us/dotnet/desktop/winforms/overview/
- https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput

Status: approved by the user after the multi-viewer revision; implementation in progress under `OpenDesk/`.
