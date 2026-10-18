# Responsive UI and low-latency sessions

Goal beyond the goal: make remote support feel direct, with readable controls
and dependable clicks instead of requiring users to understand input plumbing.

Altitude: (1) reproduce clipping and missed control, (2) correct the layout/input/
frame pipeline and measure it, (3) keep these properties checked in repeatable
Windows layout, input and transport probes. Finish rung 3 with available access.
Depth: inspect actual controls, captured frames, TLS writes and input events;
use rendered size/DPI sweeps, Windows input probes, timed capture/encode and
socket tests rather than appearance claims. Range is skipped: these native UI
and transport methods directly address the report without expanding features.

Raw material: supplied screenshot, current WinForms tree, source, running local
app and owner's reachable VPS. Missing: authenticated access to the second
Windows PC, a controlled WAN link and elevated/secure-desktop capability.

Hard targets: no clipped primary controls at supported sizes/100-200% DPI;
all test clicks arrive in order; local UI work stays below 16 ms per update where
possible; moving-screen throughput targets 30 fps on loopback; stale video is
bounded on slow links. Report actual p50/p95 measurements and missed targets.

Design audit: preserve Segoe UI, red accent, light gray canvas, square controls,
number-first connection flow, six navigation entries and attended permissions.
Retire fixed-position address card, narrow fixed sidebar, fixed header width,
wrapped labels, UI-thread image decode/recording, hidden extra control activation,
unbounded mouse-move history and excessive repeated full-screen traffic.
Taste settings: preserve redesign, restrained variance 2/10, motion 1/10, density
5/10. Native WinForms layout, not a web/marketing framework. Web-only preflight
items (hero, SEO, images, GSAP, CSS, package choices) do not apply.

Rewritten prompt:
```text
Fix OpenDesk so remote support feels immediate. Preserve its familiar identity
and consent policy. Reproduce clipping across real Windows sizes and DPI,
trace clicks from the viewer through TLS to Windows input, remove avoidable
video/UI work and prevent stale frames accumulating on slow links. Keep old
clients compatible, then rebuild and install the verified app. Pass only when
layout sweeps, input regressions and actual capture/transport measurements
support the result; report the measured latency and remaining WAN limits.
```

Next: implement and run the native layout/input/performance checks sequentially.

## Result

Shipped as 0.3. Baseline sidebar overflow reproduced, then size/font sweeps and
approval/settings/chat renders passed. Core suite 42/42. Complete two-home UI
flow passed with host-approved control immediately active and 20/20 surface
clicks delivered in order. Final run: first frame 191 ms; reader-thread decode
8.4 ms. Capture/encode moving-content p50 fell from 62.4 to about 33 ms with Balanced
1280 px rather than the old 1600 px default. Fast and Sharp expose the tradeoff.

Outside Windows input probes observed 20/20 local clicks (p50 32.1/p95 47.3 ms)
and 8/8 via the real VPS (p50 296.2/p95 912.7 ms), plus 5/5 characters each.
One earlier combined input run timed out; isolated and final runs passed. No
proven cause is assigned to that timeout. The aspiration of consistently
sub-150-ms public input was missed. A second independent Windows PC, controlled
WAN tests and sustained sharing remain outside the access available here.

Native pointer/click injection is now batched atomically. Ordinary host controls
are no longer suppressed by the broad input guard; approval, trust and permission
controls remain protected. Version negotiation preserves old peers and the
existing relay. Update both endpoints for the bounded-video path.

Official technical references used:
- https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
- https://learn.microsoft.com/dotnet/desktop/winforms/forms/autoscale
- https://learn.microsoft.com/dotnet/desktop/winforms/controls/autosize-behavior-in-the-tablelayoutpanel-control
