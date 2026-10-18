# Relay route diagnosis and next step

Both clients normally use different networks in Nepal. All current numeric-ID
sessions pass through the owner's VPS. No infrastructure was purchased or moved.

## Measurements from this Windows PC, 2026-10-18

| Target | Measurement | Result |
| --- | --- | --- |
| Current VPS 139.99.135.69 | Eight ICMP round trips | 254-255 ms, zero loss in eight probes |
| Current VPS relay port | Five TCP connects | min 241.7 ms, median 247.3 ms |
| Mumbai regional AWS EC2 API | Five TCP connects | min 45.4 ms, median 45.8 ms |
| Singapore regional AWS EC2 API | Five TCP connects | min 71.6 ms, median 76.2 ms |
| Sydney regional AWS EC2 API | Five TCP connects | min 313.8 ms, median 323.8 ms |

Reference endpoints are not OpenDesk relays and are not the same provider as the
existing VPS. These small samples are regional route comparisons, not guarantees
for a purchased VPS, proof of the existing VPS location or measurements from the
second PC. Endpoint regions: https://docs.aws.amazon.com/ec2/latest/devguide/ec2-endpoints.html

The reusable PowerShell probe was also executed: Mumbai median 45.3 ms and
Singapore median 77.9 ms, both 3/3 successful. The current VPS had 2/3 successful
TCP connects (260-281.6 ms) and one timeout. This reinforces the need for longer
loaded testing; eight clean ICMP samples do not prove an always-stable TCP path.

The approximately 250 ms network RTT explains much of the approximately 300 ms
application input observation. The prior application probe leaves this PC,
traverses the VPS and returns to this PC; it is not a two-independent-PC test.
The 900 ms application spike is not proved to be a geographic or network fault
by these eight pings. Congestion, scheduling and host processing still require
longer loaded measurements to distinguish. Changing region does not guarantee
that every spike disappears.

## Highest-impact change

Benchmark an actual Mumbai candidate from both client networks, then migrate
the relay if those results remain substantially better. Singapore is a second
candidate. Aim for below 80 ms median TCP setup from each client, with low spread;
measure actual clicks/screens under load after migration. These are acceptance
targets, not promises. No extra CPU or JPEG setting can remove the measured
network RTT to the current VPS.

The supplied probe uses built-in PowerShell/.NET:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe-latency.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe-latency.ps1 -CandidateHost NEW_SERVER_IP
```

The second command requires an actual listening relay on the candidate port.
Run the first command on both client PCs before selecting a hosting region.

## Concrete migration sequence

1. Supply a reachable Mumbai Linux VPS with SSH/sudo access. Keep the existing
   service running while checking the candidate. Provisioning/spending requires
   the owner's choice; no provider account or purchase authority is supplied.
2. Install the compatible Linux service bundle on the candidate with its private
   state directory and open only its required TCP 47834/47832 service ports.
   Existing deployment instructions are in deploy/linux/README.md.
3. At cutover, briefly stop the old directory and privately transfer devices.json
   and service-identity.pfx from /var/lib/opendesk to the candidate's stopped
   service state directory. Keep the private file owned by opendesk with mode
   0600, inside its 0700 directory. Do not post private certificate contents.
   Preserve the old copies for rollback. Restart the old service if rolling back.
4. Start the candidate, verify its HTTPS fingerprint through trusted SSH, and
   confirm the fingerprint matches the preserved certificate. Replacing the
   generated candidate certificate with the preserved certificate also requires
   restarting its service; service.json is then regenerated correctly.
5. Update both clients' service URL to the candidate IP/domain and retain the
   verified pin. With the preserved registry and existing Windows device keys,
   normal registrations retain device IDs. Active sessions need reconnecting.
6. Verify distinct IDs, approval, input, files and sharing across the two Nepal
   networks; record p50/p95 control and frame latency during motion/transfers.
   Update packaged defaults after the actual endpoint and pin are verified.

A domain name can make future endpoint moves easier without changing clients'
URL, while certificate pinning still checks server identity. The present app's
Settings can already change its service URL; no protocol rewrite is needed for
this migration. Automatic P2P/NAT traversal remains a separate unimplemented
optimization, rather than a required first step for this measured route problem.
