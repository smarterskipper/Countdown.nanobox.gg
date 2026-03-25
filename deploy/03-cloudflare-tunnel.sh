#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# HomelabCountdown — Cloudflare Tunnel setup
#
# Run on the LXC AFTER 02-bootstrap-lxc.sh.
# Creates a tunnel from your domain → nginx on port 80.
# No port-forwarding on your router needed.
#
# Usage:
#   bash deploy/03-cloudflare-tunnel.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# ── CONFIG ────────────────────────────────────────────────────────────────────
TUNNEL_NAME="homecountdown"
HOSTNAME=""      # e.g. countdown.yourdomain.com  — must be on Cloudflare DNS
# ─────────────────────────────────────────────────────────────────────────────

if [[ -z "$HOSTNAME" ]]; then
  echo "ERROR: Set HOSTNAME at the top of this script."
  exit 1
fi

echo "==> Installing cloudflared"
curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg \
  | gpg --dearmor -o /usr/share/keyrings/cloudflare-main.gpg
echo "deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] \
  https://pkg.cloudflare.com/cloudflared bookworm main" \
  > /etc/apt/sources.list.d/cloudflared.list
apt-get update -qq && apt-get install -y -qq cloudflared

echo "==> Log in to Cloudflare (browser will open or you'll get a URL to visit)"
cloudflared tunnel login

echo "==> Creating tunnel: ${TUNNEL_NAME}"
cloudflared tunnel create "${TUNNEL_NAME}"

TUNNEL_ID=$(cloudflared tunnel list --output json \
  | python3 -c "import sys,json; \
    tunnels=json.load(sys.stdin); \
    [print(t['id']) for t in tunnels if t['name']=='${TUNNEL_NAME}']")

echo "   Tunnel ID: ${TUNNEL_ID}"

echo "==> Routing DNS: ${HOSTNAME} → ${TUNNEL_ID}"
cloudflared tunnel route dns "${TUNNEL_NAME}" "${HOSTNAME}"

echo "==> Writing tunnel config"
mkdir -p /etc/cloudflared
cat > /etc/cloudflared/config.yml << EOF
tunnel: ${TUNNEL_ID}
credentials-file: /root/.cloudflared/${TUNNEL_ID}.json

ingress:
  - hostname: ${HOSTNAME}
    service: http://localhost:80
  - service: http_status:404
EOF

echo "==> Installing cloudflared as system service"
cloudflared service install
systemctl enable --now cloudflared

echo ""
echo "✓ Cloudflare Tunnel active."
echo "   https://${HOSTNAME} → this LXC"
echo ""
echo "   Check status: systemctl status cloudflared"
echo "   View tunnel:  cloudflared tunnel info ${TUNNEL_NAME}"
echo ""
