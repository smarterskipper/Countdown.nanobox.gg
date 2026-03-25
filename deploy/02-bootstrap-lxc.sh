#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# HomelabCountdown — LXC bootstrap
#
# Run this ONCE on a fresh Debian 12 LXC (as root).
# Installs: .NET 9, Playwright system deps, nginx, systemd service.
#
# API keys are NOT set here — they live in GitHub Secrets and are injected
# into /etc/homecountdown.env by the CI/CD workflow on every deploy.
#
# Usage:
#   bash deploy/02-bootstrap-lxc.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# ── CONFIG ────────────────────────────────────────────────────────────────────
APP_DOMAIN=""    # e.g. countdown.yourdomain.com — leave blank for IP-only access
# ─────────────────────────────────────────────────────────────────────────────

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

echo "==> Creating app and secrets directories"
mkdir -p /opt/homecountdown/current
mkdir -p /opt/homecountdown/next

# Create the env file with placeholder — CI/CD will overwrite it on first deploy
touch /etc/homecountdown.env
chmod 600 /etc/homecountdown.env
chown root:root /etc/homecountdown.env

echo "==> Writing systemd service"
# Note: secrets come from EnvironmentFile, NOT from this file.
# /etc/homecountdown.env is written by the GitHub Actions workflow on every
# deploy using the value stored in GitHub Secrets — never committed to git.
cat > /etc/systemd/system/homecountdown.service << 'EOF'
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

# Static environment
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=HOME=/home/runner

# Secrets injected here by CI/CD — file is chmod 600, never in git
EnvironmentFile=/etc/homecountdown.env

StandardOutput=journal
StandardError=journal
SyslogIdentifier=homecountdown

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable homecountdown
echo "   (service enabled — will start automatically after first deploy)"

echo "==> Writing nginx config"
NGINX_SERVER_NAME="${APP_DOMAIN:-_}"

cat > /etc/nginx/sites-available/homecountdown << EOF
server {
    listen 80;
    server_name ${NGINX_SERVER_NAME};

    # Pass WebSocket upgrades for Blazor SignalR
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
echo "✓ Bootstrap complete. Secrets are managed via GitHub — not this machine."
echo ""
echo "  To add your Anthropic API key:"
echo "    gh secret set ANTHROPIC_API_KEY --repo smarterskipper/Countdown.nanobox.gg"
echo "  Or: github.com/smarterskipper/Countdown.nanobox.gg/settings/secrets/actions"
echo ""
echo "  Next steps:"
echo "  1. Run deploy/01-install-runner.sh  (register GitHub Actions runner)"
echo "  2. Set ANTHROPIC_API_KEY in GitHub Secrets (above)"
echo "  3. Push to master — CI/CD deploys the app and injects the key"
echo "  4. (Optional) deploy/03-cloudflare-tunnel.sh"
echo ""
