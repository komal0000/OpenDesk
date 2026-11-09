#!/usr/bin/env bash
# Installs and configures coturn so OpenDesk 0.4 sessions can relay when a direct path fails,
# and tells OpenDesk.Service to issue TURN credentials. Safe to run again (keeps the secret).
# Usage: sudo bash ./setup-turn.sh 203.0.113.10
set -euo pipefail
cd -- "$(dirname -- "$0")"
if [ "$(id -u)" -ne 0 ]; then echo 'Run with sudo bash ./setup-turn.sh <public-IP>' >&2; exit 1; fi
ip="${1:-}"
if ! [[ "$ip" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then echo 'Give this server public IPv4 address as the first argument.' >&2; exit 1; fi
test -f turnserver.conf
if [ ! -d /var/lib/opendesk ]; then echo 'Install OpenDesk first (install.sh).' >&2; exit 1; fi
if ! command -v turnserver >/dev/null; then apt-get update && apt-get install -y coturn; fi

secret=/var/lib/opendesk/turn-secret
if [ ! -s "$secret" ]; then
  ( umask 077; head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$secret" )
  chown opendesk:opendesk "$secret"; chmod 0600 "$secret"
fi
value=$(cat "$secret")
if [ -f /etc/turnserver.conf ] && ! grep -q 'OpenDesk' /etc/turnserver.conf; then cp -a /etc/turnserver.conf "/etc/turnserver.conf.before-opendesk"; fi
sed -e "s/__SECRET__/$value/g" -e "s/__PUBLIC_IP__/$ip/g" turnserver.conf > /etc/turnserver.conf
chown root:turnserver /etc/turnserver.conf 2>/dev/null || true
chmod 0640 /etc/turnserver.conf
# Older Debian/Ubuntu packages only start coturn when this is set.
if [ -f /etc/default/coturn ]; then sed -i 's/^#\?TURNSERVER_ENABLED=.*/TURNSERVER_ENABLED=1/' /etc/default/coturn; fi
printf 'OPENDESK_ARGS=--turn-url %s:3478 --turn-secret-file %s\n' "$ip" "$secret" > /etc/opendesk.env
chmod 0644 /etc/opendesk.env
systemctl enable coturn >/dev/null
systemctl restart coturn
systemctl restart opendesk
sleep 2
systemctl --no-pager --lines=3 status coturn
journalctl -u opendesk -n 5 --no-pager | grep -E 'TURN|OpenDesk device service' || true
echo
echo 'Open in the VPS provider firewall (and UFW if active):'
echo '  TCP 47834 (HTTPS + signaling)   TCP 47832 (0.3 relay, until old clients are retired)'
echo '  UDP 3478 and TCP 3478 (TURN)    UDP 49160-49200 (TURN relay ports)'
