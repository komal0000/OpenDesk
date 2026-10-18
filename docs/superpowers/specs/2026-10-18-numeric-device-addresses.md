# Numeric device addresses

## User correction

The normal flow must resemble the requested AnyDesk interaction: every device
shows a unique number; a viewer enters the other device's number to request a
connection. Copying encoded invitations does not satisfy that requirement.
Windows first, attended approval, multiple viewers and one active controller
remain required.

## Proposed user flow

- The home screen shows **Your address**, formatted as `123 456 789`, and an
  Online/Offline status.
- The same screen has **Remote address** and **Connect**.
- Once the service is configured, opening the app registers its availability.
- Entering an online device's address sends a connection request. Its host
  approves each viewer before any frame or input is shared.
- Host-side viewer removal, control transfer and Stop All remain available.
- Invitations, IP addresses and ports leave the normal connection flow. Service
  configuration belongs in a separate setup/settings surface.

## Directory and identity

A shared directory/relay service is required. A number is an address, not a
password or a replacement for approval. Do not derive it from a MAC address or
other hardware fingerprint. Allocate a random unused nine-digit ID on the
service and persist its association with a device public key. The client keeps
a device credential protected by the Windows user key store and proves
ownership using a fresh signed challenge when registering/reconnecting.

IDs remain stable across normal app and service restarts when their credential
and registry are preserved. IDs belong to a particular service; they are not
AnyDesk IDs and do not resolve on the AnyDesk network. Losing the device key or
server registry requires an explicit recovery/reset policy rather than silently
claiming an existing ID.

## Connection and trust

Protect service registration and lookup with authenticated TLS. A deployed
service can use a publicly trusted certificate; local testing can use an
explicitly configured service certificate fingerprint. Do not silently accept
an untrusted service certificate or return joining credentials over plaintext.

The service resolves an online ID into fresh connection/session metadata.
Keep host/viewer session TLS, per-viewer approval, control epochs and bounded
queues. The service is the trust root for the initial ID-to-device association;
do not claim the number itself cryptographically authenticates a device.
Bound pending requests and rate-limit address lookups and connection requests.

## Hosting dependency

The existing relay executable has been built and tested locally. No public
relay/directory has been deployed and no public service address has been supplied.

Two deployment paths:

1. Local/LAN testing: run the shared service on a Windows PC and configure both
   apps to use it. Numeric IDs work within that reachable network.
2. Internet use: run the shared service on a reachable server with a domain/IP
   and authenticated service TLS. Clients use that endpoint through setup or
   an application distribution configured for that service.

The implementation must not display a fabricated global Online status or imply
that a public service already exists. Public deployment requires an actual host;
local development alone cannot supply one.

## Acceptance checks

- Different device credentials receive distinct IDs; one credential retains
  its ID after client and registry restart.
- Wrong ownership proof cannot register another device's ID.
- IDs are scoped to the configured service and lookups are TLS-protected.
- Entering a valid online ID reaches only its corresponding host.
- Unknown/offline addresses show a clear unavailable message.
- No frames or input before explicit approval; rejection and Stop All work.
- Multiple viewers can resolve and join the same host independently.
- No long invitation appears in the primary interface.

Status: numeric addresses and a self-hosted HTTPS directory/relay are implemented
in OpenDesk 0.2 and exercised through two actual Windows app windows. Public
hosting remains an external dependency; no public deployment is claimed. See
OpenDesk/README.md, FEATURE-PARITY.md and VERIFICATION.md.
