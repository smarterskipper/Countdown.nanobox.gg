#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# HomelabCountdown — LXC bootstrap
#
# Run this ONCE on a fresh Debian 12 LXC (as root).
# Installs: .NET 9, Playwright system deps, nginx, systemd service.
#
# Usage:
#   bash deploy/02-bootstrap-lxc.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# ── CONFIG ────────────────────────────────────────────────────────────────────
ANTHROPIC_API_KEY=""          # sk-ant-...  (required)
APP_DOMAIN=""                 # e.g. countdown.yourdomain.com (for nginx server_name)
                              # leave blank to use IP-based access only
# ─────────────────────────────────────────────────────────────────────────────

if [[ -z "$ANTHROPIC_API_KEY" ]]; then
  echo "ERROR: Set ANTHROPIC_API_KEY at the top of this script."
  exit 1
fi

echo "==> System update"
apt-get update -qq && apt-get upgrade -y -qq

echo "==> Installing .NET 9 ASP.NET Core runtime"
apt-get install -y -qq wget
wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
  -O /tmp/packages-microsoft-prod.deb
dpkg -i /tmp/packages-microsoft-prod.deb
rm /tmp/packages-microsoft-prod.deb
apt-get update -qq
apt-get install -y -qq aspnetcore-runtime-9.0

echo "==> Installing Playwright system dependencies (headless Chromium)"
apt-get install -y -qq \
  libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 libgbm1 \
  libxcomposite1 libxdamage1 libxrandr2 libpango-1.0-0 libasound2 \
  libxshmfence1 libxfixes3 fonts-liberation libdbus-1-3 libx11-6 \
  libxext6 libxcb1 libxkbcommon0 ca-certificates curl unzip

echo "==> Installing nginx"
apt-get install -y -qq nginx

echo "==> Creating app directories"
mkdir -p /opt/homecountdown/current
mkdir -p /opt/homecountdown/next

echo "==> Writing systemd service"
cat > /etc/systemd/system/homecountdown.service << EOF
[Unit]
Description=HomelabCountdown Blazor App
After=network.target

[Service]
Type=simple
User=runner
WorkingDirectory=/opt/homecountdown/current
ExecStart=/opt/homecountdown/current/HomelabCountdown
Restart=on-failure
RestartSec=5

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=ANTHROPIC_API_KEY=${ANTHROPIC_API_KEY}

# Playwright needs a writable home dir for browser cache
Environment=HOME=/home/runner

StandardOutput=journal
StandardError=journal
SyslogIdentifier=homecountdown

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable homecountdown
echo "   (service enabled but not started yet — no binary deployed yet)"

echo "==> Writing nginx config"
NGINX_SERVER_NAME="${APP_DOMAIN:-_}"

cat > /etc/nginx/sites-available/homecountdown << EOF
server {
    listen 80;
    server_name ${NGINX_SERVER_NAME};

    # Pass WebSocket upgrade for Blazor SignalR
    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade \$http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host \$host;
        proxy_set_header   X-Real-IP \$remote_addr;
        proxy_cache_bypass \$http_upgrade;
        proxy_read_timeout 86400s;
    }
}
EOF

ln -sf /etc/nginx/sites-available/homecountdown /etc/nginx/sites-enabled/homecountdown
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl restart nginx

echo ""
echo "✓ Bootstrap complete."
echo ""
echo "  Next steps:"
echo "  1. Run deploy/01-install-runner.sh to register the GitHub Actions runner"
echo "  2. Push to master — GitHub Actions will build and deploy automatically"
echo "  3. (Optional) Install Cloudflare Tunnel: deploy/03-cloudflare-tunnel.sh"
echo ""
