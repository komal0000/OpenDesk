# Public OpenDesk service on Linux

This package is for a supported glibc-based Linux x86-64 VPS with systemd.
The .NET runtime is bundled; OS native dependencies are still required.
Check `uname -m` (must say x86_64) and `cat /etc/os-release` first.
It is not an ARM64 or Alpine/musl package. Linux execution has not yet been
verified on the user's VPS: SSH password authentication must be performed locally.

## Transfer and install

On your Windows PC, from the folder containing the archive, run:

```powershell
scp .\OpenDesk-Service-linux-x64.tar.gz ubuntu@139.99.135.69:~/
ssh ubuntu@139.99.135.69
```

Enter passwords only in your own terminal. On the VPS:

```bash
uname -m
cat /etc/os-release
ss -ltn | grep -E ':(47834|47832)\b' || true
mkdir -p ~/opendesk-upload
tar -xzf ~/OpenDesk-Service-linux-x64.tar.gz -C ~/opendesk-upload
cd ~/opendesk-upload
sudo bash ./install.sh
sudo systemctl status opendesk --no-pager
sudo cat /var/lib/opendesk/service.json
```

The installer refuses to replace an existing OpenDesk installation or occupied
ports. It creates a dedicated unprivileged account and private state directory,
installs the binary and unit, then enables startup and starts this server service.
This is server availability; Windows clients still require attended approval.

Allow inbound TCP 47834 (HTTPS directory) and TCP 47832 (raw TCP relay) in the
VPS provider firewall. If UFW is already active, add these rules:

```bash
sudo ufw status
sudo ufw allow 47834/tcp
sudo ufw allow 47832/tcp
```

Do not enable UFW blindly over SSH or remove existing rules. The installer does
not change any firewall. These custom ports must also be allowed outbound from
both client networks. The relay is raw TCP; an ordinary HTTP/CDN proxy cannot
forward it. This release does not tunnel everything over HTTPS port 443.

## Configure both Windows clients

In OpenDesk Settings, use `https://139.99.135.69:47834` and the Pin value from
service.json, obtained through your trusted SSH session. The generated URL in
that file says localhost; replace it with the public IP for remote clients.
No domain or purchased certificate is required for this explicitly pinned mode.
Keep the fingerprint check enabled. Start local service is unnecessary on clients.
Save settings, go online, enter the other PC's numeric address and approve there.
Test between two separate networks, such as home internet and a phone hotspot.

## Operation

```bash
sudo journalctl -u opendesk -n 50 --no-pager
sudo systemctl restart opendesk
sudo systemctl stop opendesk
sudo systemctl disable opendesk
```

The server keeps device IDs in /var/lib/opendesk/devices.json and its private
certificate in service-identity.pfx. Keep these private and preserve both across
upgrades. Stop the service before taking a consistent backup. Deleting the
registry changes assigned IDs; replacing the certificate requires new client pins.
Sessions disconnect on restart; clients may need Stop All then Go online.

The current service is a small-pilot implementation with bounded connections,
rooms and queues, not a large public fleet. Public registration has no operator
enrollment/invite restriction yet. Monitor resource use and abuse before wider
distribution. Relay bandwidth grows with each viewer: 1 Mbit/s to one viewer is
about 0.45 GB of outbound traffic per hour. Actual JPEG bitrate varies with
screen content and must be measured. No public/WAN test is claimed yet.

Architecture: clients register numeric IDs through HTTPS, and both connect
outward to this relay. Screen/input payloads use host-pinned TLS through the relay;
the directory is a trusted source of host pins. Automatic direct/P2P selection
and NAT hole punching are not implemented. AnyDesk documents direct connections
and server relaying at https://support.anydesk.com/docs/settings.

systemd deployment reference:
https://learn.microsoft.com/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0
