# OpenDesk Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking. Execute sequentially in the main session, per the user's AGENTS.md instructions.

**Goal:** Deliver a runnable Windows attended remote-desktop app with host approval, optional control and direct/relay connectivity.

**Architecture:** A shared core owns invitations, pinned TLS and framed messages. A Windows Forms app owns capture, input and host/viewer UI. A separate relay pairs outbound connections and forwards encrypted streams without terminating TLS.

**Tech Stack:** C#, .NET 10, Windows Forms, System.Drawing, SslStream, TcpClient/TcpListener, Windows SendInput; framework libraries by default.

**Spec:** `docs/superpowers/specs/2026-10-18-attended-windows-design.md`.

## Global constraints

- Windows x64; attended access only; multiple simultaneous viewers.
- Proposed defaults: five approved viewers, bounded approval queue, one active remote controller. Capacity and control policy remain design recommendations for review.
- Product source under `OpenDesk/`; preserve analysis files.
- Invitation: 256-bit random secret, certificate SHA-256 pin, version, endpoint, mode, expiry; 15-minute join lifetime.
- Maximum message payload 8 MiB; handshake/control JSON 16 KiB.
- Capture target 10 fps, maximum frame width 1600 pixels, one selected monitor.
- Approval before frames; explicit separate input permission; visible Stop control.
- No permanent credentials, startup persistence, automatic firewall changes or public deployment.
- No AnyDesk assets, proprietary code or codec reuse.

## Review focus

- Malformed or modified invitations must fail before connecting or authorizing.
- View-only, rejected and disconnected sessions must never inject input.
- Socket stalls and partial frames must cancel promptly without unbounded memory.
- Letterboxed images and negative monitor coordinates must map input correctly.
- Relay rooms must remain isolated and expire/clean up when either peer leaves.
- A slow viewer must not delay others; controller transfer must release old input state.

## Task 1: Secure session core

**Files:** `OpenDesk/Core/Invitation.cs`, `Wire.cs`, `SecureSession.cs`, `OpenDesk/Core.Tests/`.

**Interfaces:** `Invitation.Parse(string, DateTimeOffset)` returns a validated immutable invitation; `Wire.ReadAsync(Stream, CancellationToken)` returns a typed bounded message; `Wire.WriteAsync(Stream, Message, CancellationToken)` serializes one message; `SecureSession` establishes pinned TLS and authenticates the invitation secret before exposing application messages.

- [x] Add failing tests for invalid version/endpoint/pin/secret, expired invitations and malformed encoding.
- [x] Add framing tests: negative/oversized lengths, truncated header/body, cancellation and 16 KiB control limits.
- [x] Implement invitation generation/validation, framed messages, ephemeral certificate creation and TLS.
- [x] Prove loopback TLS success, wrong pin rejection and wrong secret rejection.

## Task 2: Attended host and viewer sessions

**Files:** `OpenDesk/Core/HostCoordinator.cs`, `HostSession.cs`, `ViewerSession.cs`, `SessionMessages.cs`, corresponding core tests.

**Interfaces:** HostCoordinator owns peer identities, approvals, capacity and an exclusive controller lease. Each HostSession accepts an asynchronous approval delegate, a frame subscription and an input sink gated by that lease. Viewer returns approval/control state and frames; input is accepted only from the current controller.

- [x] Add failing tests that no capture occurs before approval, rejection closes the session, view-only messages cannot reach the input sink and reconnect requests fresh approval.
- [x] Implement a state machine per viewer and cancellation propagated through handshake/read/write/capture; provide separate remove-viewer and stop-all operations.
- [x] Capture/encode once and fan out to independent latest-frame queues with write deadlines.
- [x] Test multiple viewers, independent approvals, capacity overflow, slow-peer isolation, removal of one viewer and stop-all.
- [x] Test exclusive control transfer and rejection of input queued by a previous controller after revocation; release held input before transfer.
- [x] Bound image dimensions and bytes; validate input enums, coordinates and key/button state.
- [x] Test remote disconnect and local stop, including held-input cleanup and a stalled peer.

## Task 3: Windows desktop app

**Files:** `OpenDesk/App/Program.cs`, `MainForm.cs`, `ViewerForm.cs`, `ScreenCapture.cs`, `WindowsInput.cs`, `ViewerCoordinates.cs`.

**Interfaces:** Screen capture produces JPEG frames and monitor metadata; input sink maps validated normalized coordinates onto selected monitor bounds; viewer coordinate mapping returns no input outside the displayed image.

- [x] Add coordinate tests for letterboxing, boundaries, negative monitor origin and scaling.
- [x] Implement capture and input with deterministic disposal and key/button release.
- [x] Build Host and Join screens, invitation copy/paste, per-viewer approval prompt, connected-viewer list, grant/transfer/revoke control, remove-viewer, active banner, Stop All button, viewer disconnect and error messages.
- [x] Wire background session work to UI dispatch; retain at most the current/pending displayed frame.
- [x] Build with compiler warnings checked; exercise bounded GUI startup and local capture encoding without injecting real user input.

## Task 4: Optional relay connectivity

**Files:** `OpenDesk/Core/ConnectionTransport.cs`, `OpenDesk/Relay/Program.cs`, `PairingServer.cs`, relay integration tests.

**Interfaces:** `ConnectionTransport.ConnectAsync(...)` returns a duplex stream for direct or relay mode. A relay room supports multiple independent channel identities, each pairing one host-side stream and viewer-side stream carrying its own end-to-end TLS session.

- [x] Add tests for distinct-room/channel isolation, duplicate roles within a channel, invalid/oversized pairing headers, expiry, per-channel cleanup and host-registration disconnect cleanup.
- [x] Implement bounded pending rooms/connections, pairing timeout and bidirectional forwarding with cancellation.
- [x] Expose relay address and room creation through the app; use the same host approval and certificate pinning as direct mode.
- [x] Prove multiple viewer sessions through one local relay room; verify removing one preserves others and the relay cannot substitute another host certificate.

## Task 5: Package, review and document

**Files:** `OpenDesk/README.md`, solution/project files, `.gitignore`, `OpenDesk/publish.ps1`, publish output under `OpenDesk/artifacts/`.

- [x] Run focused automated tests for core security boundaries, sessions, coordinate mapping and relay integration.
- [x] Review authentication-before-approval, permission enforcement, TLS pinning, bounded allocation and resource cleanup against the spec.
- [x] Publish Windows x64 app and relay; verify output executables exist and GUI starts/exits in a bounded smoke check.
- [x] Document direct LAN/VPN setup, optional relay deployment, invitation handling, Stop/control behavior and runtime requirements.
- [x] Record actual verification and remaining two-device/manual checks; update the investigation report to link the new app.

No commit or remote publication is part of this request. Source and artifacts remain local for review.

Status: approved and implemented locally as OpenDesk 0.1.0 development MVP. See OpenDesk/VERIFICATION.md for actual checks and outstanding two-PC/manual validation.
