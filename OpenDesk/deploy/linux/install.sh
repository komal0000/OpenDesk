#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
if [ "$(id -u)" -ne 0 ]; then echo 'Run with sudo bash ./install.sh' >&2; exit 1; fi
if [ "$(uname -m)" != x86_64 ]; then echo 'This package requires x86_64 Linux.' >&2; exit 1; fi
if [ -e /opt/opendesk ] || [ -e /etc/systemd/system/opendesk.service ] || [ -e /var/lib/opendesk ]; then
  echo 'OpenDesk installation/state already exists; refusing to overwrite it.' >&2; exit 1
fi
for required in systemctl ss install getent useradd; do command -v "$required" >/dev/null; done
if ss -H -ltn | awk '{print $4}' | grep -Eq ':(47834|47832)$'; then
  echo 'An OpenDesk port is already occupied.' >&2; exit 1
fi
if getent passwd opendesk >/dev/null || getent group opendesk >/dev/null; then
  echo 'An opendesk account/group already exists; inspect it before installation.' >&2; exit 1
fi
test -f OpenDesk.Service
test -f opendesk-server.service
useradd --system --user-group --home-dir /var/lib/opendesk --shell /usr/sbin/nologin opendesk
install -d -m 0755 /opt/opendesk
install -d -m 0700 -o opendesk -g opendesk /var/lib/opendesk
install -m 0755 OpenDesk.Service /opt/opendesk/OpenDesk.Service
install -m 0644 opendesk-server.service /etc/systemd/system/opendesk.service
systemctl daemon-reload
systemctl enable --now opendesk
echo 'Installed. Check: sudo systemctl status opendesk --no-pager'
echo 'Public fingerprint: sudo cat /var/lib/opendesk/service.json'
echo 'Allow provider/host inbound TCP 47834 and 47832; firewall was not changed.'
