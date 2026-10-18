# AnyDesk-like Windows experience

## Goal beyond the goal
Let a person open OpenDesk, tell someone their device number, and receive help
through a familiar, coherent desktop interface without exchanging technical invitations.

## Types
- Altitude: improve the complete connection/support experience rather than an isolated feature.
- Depth: work from the installed binary, official UI/feature documentation, rendered Windows forms and real protocol tests.
- Range skipped: the core gap is already clear; spend effort implementing and measuring instead of introducing additional disciplines.

## Altitude ladder
1. Stable numeric device addresses and a familiar home screen, with approved encrypted sessions.
2. A coherent Windows product: sessions, recent/favorite devices, settings, monitor selection, chat, screenshots, recording and explicit data-sharing permissions.
3. Operated internet service and broader parity: public directory deployment, cross-network verification, efficient video/audio, elevated/secure-desktop support, managed deployments and other-platform clients.

Rungs 1–2 are local implementation targets. Rung 3 needs infrastructure/devices
not supplied; do not invent a public endpoint or claim a successful two-PC test.

## Depth
1. A remote-desktop engineer would test identity ownership, offline/expiry semantics,
   independent viewers and permission enforcement before calling the workflow done.
2. Raw material: installed AnyDesk 6.0.5 executable, its recovered payload/imports,
   actual OpenDesk source and Windows captures; official documentation supplies
   reference behavior rather than proving shipped binary execution.
3. Instruments: REA/Ghidra evidence already retained; .NET build/compiler, TCP/TLS
   integration tests, persistent-registry restart tests and rendered UI smoke tests.
4. Outside judge: executable tests and measured rendering/connection outcomes.

Hard targets: stable IDs across client/registry restart; zero wrong-device routes;
no frames/input before approval; all displayed product controls have behavior;
target first frame within one second of simulated approval on loopback, measured
separately from human response time. Report misses honestly.

## Access
- Already available: Windows build/runtime, installed reference artifact, source,
  local TCP/TLS, native capture and test rendering.
- Missing: a public service host/domain, two independently reachable Windows PCs,
  signing certificate and Windows privileged-driver infrastructure.
- Build a configurable self-hosted service and local test path; leave public deployment explicit.

## Rewritten prompt
```
Build OpenDesk into a convincing Windows remote-desktop app with the core
AnyDesk workflow and a closely comparable interface, while keeping OpenDesk's
own branding. Give every registered device a persistent numeric address and
let viewers connect by that number. Preserve individual host approval,
multiple viewers and one active controller. Add working session tools and
contacts rather than placeholder buttons. Use authenticated service TLS,
protected device credentials and a persistent directory. Package both the app
and self-hosted service. Judge the result with executable identity/routing,
permission and multi-viewer tests, measured real-frame UI smoke checks, and
rendered interface comparison. Pass means the ID workflow works end to end,
all tests pass and every claimed feature was exercised; clearly mark feature
gaps and public/two-device verification that requires missing access.
```

## Next
Implement and verify rungs 1–2 with local access; no paid infrastructure or
public deployment. Preserve attended access previously selected by the user.

## Reference behavior
- https://support.anydesk.com/docs/using-anydesk — ID/alias-based connection.
- https://support.anydesk.com/docs/session-settings — session toolbar behavior.
- https://support.anydesk.com/docs/file-manager-and-file-transfer — file workflows.
- https://support.anydesk.com/docs/session-recording — recording/playback.
- https://support.anydesk.com/docs/settings — settings categories.

This is a parity target, not a claim of complete implementation. Feature status
and actual verification must be updated as work completes.
