# Windows feature comparison · OpenDesk 0.3

Reference behavior comes from the official AnyDesk documentation linked in
AMBITION.md. Installed-build evidence is in ../INVESTIGATION.md. Those are
different sources; no claim is made that every current documented feature was
observed executing in installed AnyDesk 6.0.5.

| Area | OpenDesk status | Boundary |
| --- | --- | --- |
| Numeric device address | Implemented | Persistent nine-digit ID on your configured service; Windows profile credential proves ownership |
| Enter number → connect | Implemented | HTTPS lookup, relay, remote approval and pinned session TLS; service setup is separate |
| Familiar UI | Implemented | Red/white/gray address-first shell, navigation, recent devices, favorites, address book, settings; OpenDesk branding |
| Attended approval | Implemented | Separate approval for each viewer; self-reported display name; optional data permissions |
| Multiple viewers | Implemented | Default five, configurable 1–10; one controller; remove one or stop all |
| Remote keyboard/mouse | Implemented | Windows input pipeline, control epochs and cleanup; actual hardware behavior still needs two-PC manual testing |
| Direct / relay | Implemented | Relay is the numeric-ID path; direct protocol retained in core tests; no automatic NAT traversal |
| Encryption / identity | Implemented | Configured HTTPS trust for service; challenge-signed device registration; session TLS pinned to resolved host identity |
| Chat | Implemented | Bidirectional, bounded text messages; per-viewer host chat |
| Clipboard | Partial | Explicit text send, 2,000-character cap, host permission; no automatic sync or bitmap/file clipboard |
| File transfer | Implemented subset | User-selected send/save in either direction, 100 MiB cap, SHA-256, partial cleanup; no remote filesystem browser |
| Monitor support | Partial | Host selects/switches shared monitor live for all viewers; independent per-viewer monitors not implemented |
| Fullscreen / fit view | Implemented | Local viewer fullscreen and fit-to-window rendering |
| Screenshots | Implemented | Viewer saves the currently displayed screen as PNG |
| Recording | Implemented | Local MJPEG AVI, no audio; FFmpeg successfully decoded real-frame test output |
| Recent devices / favorites / address book | Implemented | Local contacts and aliases, favorite toggle, rename/remove, one-click connection |
| Session permissions | Partial | View/control grant/transfer/revoke live; clipboard/files chosen at approval |
| Settings | Implemented subset | Device service URL/trust, display name, viewer limit, monitor; persistent local config |
| Public device network | Owner's VPS deployed | Public HTTPS fingerprint verified and relay TCP reachable; fresh clients use it by default; independent two-PC WAN session still unverified |
| Efficient video / DeskRT parity | Partial | Reused capture buffers, unchanged-frame suppression, negotiated two-frame window, up-to-30-fps pacing and off-UI decode; still GDI/JPEG rather than hardware video/DeskRT |
| Audio / remote printing | Not implemented | Requires additional native transport/device integration |
| Unattended access / startup service | Outside selected policy | User previously selected attended access; broad parity request does not silently change that decision |
| Privileged/secure desktop, remote reboot, privacy blanking, drivers | Not implemented | Requires separate Windows privileged lifecycle, integration and verification |
| Wake-on-LAN / VPN / TCP tunnels / reverse roles | Not implemented | Not represented by placeholder controls |
| Cloud accounts, managed clients, enterprise admin | Not implemented | No fake login/subscription/admin surfaces |
| Other OS clients | Not implemented | Windows first, as requested |

## Evidence and honest target

Core tests exercise actual TLS and sockets, persistent registry IDs, wrong pins,
proof replay/tampering, approval, multiple viewers, input authorization, relay
isolation, capability gating, chat/clipboard/file bytes and recording structure.
The Windows smoke test exercises CNG key reopen, HTTPS numeric lookup, relay,
simulated approval, actual capture and actual ViewerForm rendering.

Measured first rendered frame in the latest full-application local run: **237 ms after
simulated approval**, against the **<1000 ms** target. This is a one-PC loopback
measurement, not a WAN benchmark. The test used two actual home windows,
the Remote address field, Connect button, and real approval dialog. Real input
was not injected. A premature Online-state check was found and corrected by
this test before it passed.

FFprobe recognized a 1600×900 MJPEG stream with three real captured frames;
FFmpeg decoded all frames without an error. Main-window rendering was inspected
and a clipped Connect button was corrected before packaging.

This is a substantial development release, not complete AnyDesk feature parity.
