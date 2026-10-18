# OpenDesk 0.3 verification

## Responsiveness revision

- Core suite: 42/42 passed, including coalesced motion/click order, bounded video
  with control still flowing, legacy clients without ACKs, immediate cached first
  frame for a new viewer and transport delivery above the old 10-fps ceiling.
- Windows layout sweep: 800x600, 1024x640, 1280x800 with 100/125/150/200% geometry
  and font scaling; zero clipping/horizontal-scroll failures. Four other pages
  checked at compact width; approval/settings/chat rendered at all four scales.
  This simulates DPI/font changes, not a physical mixed-DPI monitor test.
- Baseline captured sidebar overflow. Capture+encode baseline p50 62.4 ms,
  p95 81.3 ms at 1600 px/JPEG65. Revised moving-content probe p50 33.4 ms,
  p95 48.7 ms at Balanced 1280 px/JPEG65. Different resolution and interpolation
  are deliberate tradeoffs; Sharp restores 1600 px at JPEG80. Measurements vary.
- Full two-window numeric-ID/real Allow control test: 20/20 viewer-surface clicks
  arrived in order at the host; input active without an extra checkbox; first
  frame 191 ms in the final run; JPEG decode 8.4 ms on the reader worker rather
  than the UI thread. Earlier run: 292 ms / 6.0 ms.
- Actual Windows SendInput to an isolated temporary target, with the narrowed
  consent filter active: local 20/20 clicks and 5/5 characters, p50 32.1 ms,
  p95 47.3 ms. Public VPS path: 8/8 clicks and 5/5 characters, p50 296.2 ms,
  p95/max 912.7 ms. Earlier public run p50 269.2/p95 287.6 ms. One combined probe
  timed out waiting for a target click; subsequent isolated and final probes
  passed. That earlier timeout is not attributed to a proven cause.
- Cursor and target process were restored/closed. The public test is a real
  round trip from this PC through the VPS back to this PC; two independent
  Windows PCs and long-duration/WAN packet-loss behavior still need manual tests.
- Native pointer move and click/wheel now use one SendInput batch. Approval and
  permission/trust controls remain protected; ordinary controls accept input.

The 30-fps capture and sub-150-ms public-control aspirations were not fully met.
The change reduces capture work and UI blocking and bounds queued video. It does
not remove physical network delay, guarantee instant clicks or provide hardware
video encoding. Existing server deployment needs no change for these client fixes.

The following sections retain the earlier 0.2 release evidence and limitations.

Final package: 0.3.0.0 self-contained Windows x64 app, packaged startup passed.
Installed copy matches the release hash, launched successfully, retained the
existing device ID 253185141 and public-service trust settings. Previous 0.2
executables were preserved in the installed program's timestamped backup folder.

## Executed checks

- Release solution build: zero warnings/errors, with warnings treated as errors.
- Core executable suite: **37/37 passed**. Real TCP, TLS, HTTPS and relay sockets;
  simulated capture/input for most unit/integration cases.
- Numeric IDs: distinct keys get distinct IDs; the same key retains its ID after
  registry reload; stale online sessions are not restored; signed challenges,
  replay/tamper rejection, room ownership, certificate mismatch and offline lookup.
- Session authorization: no frames before approval, decline, multiple viewers,
  capacity, bounded pending approvals, control transfer, stale epochs, raw
  unauthorized input and clipboard/file attempts, blocked Windows input behavior.
- Transport lifecycle: independent viewers/relay rooms, slow-reader isolation,
  cancellation/stop, invalid/truncated/oversized messages, room ownership and expiry.
- Session tools: actual chat/clipboard messages through TLS; 160,000 random file
  bytes transferred to disk and compared; file size/path checks, corrupt digest
  rejection, incomplete file cleanup, permission preservation across video frames.

## Whole Windows application test

`App.Smoke` starts an isolated HTTPS service/relay and **two actual MainForm app
windows**. Each uses a separate protected Windows CNG test key/profile. It checks
key reopen, waits for real ID registration, types the remote ID into the actual
address textbox, invokes the actual Connect button, presses Allow viewing in the
real approval dialog, and waits for actual ViewerForm rendering from Windows GDI
capture. It also verifies the recent-session entry was saved. It does not inject
OS keyboard/mouse input. Test keys are deleted; normal user settings are untouched.

Latest successful full-application run: **seven frames rendered; first frame
237 ms after approval**, target **<1000 ms**. Previous full-application run: 312 ms.
These are one-PC loopback observations, not WAN latency or sustained benchmarks.

The first full-UI run caught a premature Online flag before a numeric ID had
been assigned. It failed, the flag was corrected, and the test then passed.
Rendered UI inspection also caught and corrected a clipped top Connect button.
`home-online-smoke.png` shows a real temporary test ID while that local test was
online. Its service was closed afterward; the screenshot ID is not a public address.

## Recording outside judge

An AVI was produced from three actual 1600×900 JPEG desktop frames.
FFprobe identified `mjpeg`, width 1600, height 900, three frames. FFmpeg decoded
the entire recording with no errors. Those tools are verification instruments,
not product dependencies. Container/header/index structure also has a core test.
The temporary desktop test recording is not included in release archives.

## Package checks

Windows x64 single-file, self-contained app, device service and legacy relay are
built by publish.ps1. Packaged GUI/render startup and HTTPS/relay startup passed.
The packaged service also passed an isolated start, graceful stop, restart and
second stop, retaining the same certificate fingerprint across restarts.
The current-user control pipe and generated HTTPS configuration were exercised.
The full app ZIP includes exactly OpenDesk.exe, OpenDesk.Service.exe and README.md.
SHA-256 hashes are retained in artifacts/SHA256SUMS.txt. App file version: 0.2.0.0.

Linux service cross-publish (linux-x64, self-contained single-file) passed with
zero warnings/errors. The tar.gz includes the ELF executable, systemd unit,
installer and guide. SSH to the supplied VPS reached authentication but failed
with the locally available keys; the user uses password login. No VPS state was
changed directly by the agent. The user then installed it in their password SSH
terminal and supplied systemd logs showing successful HTTPS/relay startup.
After the user opened UFW ports, external checks returned HTTP 200 for /v1/info
with the exact supplied SHA-256 certificate pin, and relay TCP 47832 connected.
Independent Windows-PC-to-Windows-PC WAN sharing still needs verification.
Verified the packaged executable is ELF64 x86-64 and checked installer syntax
with Bash. A Windows case-insensitive filename collision between the binary
and unit file was caught by binary-header validation and fixed by naming the
packaged unit opendesk-server.service; the rebuilt archive has separate entries.

## Review and limits

Sequential author review followed the user's AGENTS.md; no independent agent or
external security audit is claimed. Review covered approval ordering, pinning,
proof binding, persistent identity, bounded messages/queues, reliable tool messages,
partial cleanup, clipboard validation, control/display epochs, UI dispatch and
recording structure. No original AnyDesk code/assets are used.

Still requires manual verification: independent Windows PCs over LAN/WAN, firewall
and DNS configuration, prolonged sharing, actual keyboard/mouse layouts, every
monitor/DPI configuration, user file-dialog behavior and real elevated-app input
blocking. No publicly trusted certificate deployment, audio, privileged
Windows driver/service, printing or complete AnyDesk parity is claimed.

The user previously chose attended access. That policy is preserved rather than
silently enabling unattended access or persistence. See FEATURE-PARITY.md for the
implemented/partial/missing matrix and README.md for service setup.

## Public service defaults

Fresh profiles now use https://139.99.135.69:47834 with its exact SSH-provided
certificate fingerprint. Empty service profiles adopt that default and clear a
stale cached device ID; existing explicit service URL/pin settings remain intact.
These three cases passed in App.Smoke, followed by the complete two-window local
connection/approval/render test (seven frames; 235 ms first frame). Release build
passed with zero warnings/errors. No user profile was modified by these tests.
