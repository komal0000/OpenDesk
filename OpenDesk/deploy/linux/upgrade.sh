#!/usr/bin/env bash
# Upgrades an existing OpenDesk server (installed by install.sh) in place. Device IDs (devices.json)
# and the service certificate (service-identity.pfx) are kept, so client pins stay valid.
set -euo pipefail
cd -- "$(dirname -- "$0")"
if [ "$(id -u)" -ne 0 ]; then echo 'Run with sudo bash ./upgrade.sh' >&2; exit 1; fi
if [ "$(uname -m)" != x86_64 ]; then echo 'This package requires x86_64 Linux.' >&2; exit 1; fi
if [ ! -x /opt/opendesk/OpenDesk.Service ] || [ ! -d /var/lib/opendesk ]; then
  echo 'No existing installation found; use install.sh for a new server.' >&2; exit 1
fi
test -f OpenDesk.Service
test -f opendesk-server.service
stamp=$(date +%Y%m%d-%H%M%S)
systemctl stop opendesk
cp -a /opt/opendesk/OpenDesk.Service "/opt/opendesk/OpenDesk.Service.before-$stamp"
cp -a /etc/systemd/system/opendesk.service "/etc/systemd/system/opendesk.service.before-$stamp"
if [ -f /var/lib/opendesk/devices.json ]; then cp -a /var/lib/opendesk/devices.json "/var/lib/opendesk/devices.json.before-$stamp"; fi
install -m 0755 OpenDesk.Service /opt/opendesk/OpenDesk.Service
install -m 0644 opendesk-server.service /etc/systemd/system/opendesk.service
systemctl daemon-reload
systemctl start opendesk
sleep 2
systemctl --no-pager --lines=5 status opendesk
echo
echo "Upgraded. Previous binary: /opt/opendesk/OpenDesk.Service.before-$stamp"
echo 'Rollback: stop opendesk, copy the .before files back, daemon-reload, start opendesk.'
echo 'For sessions across different networks, run: sudo bash ./setup-turn.sh <this-server-public-IP>'
