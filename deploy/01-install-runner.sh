#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# HomelabCountdown — GitHub Actions self-hosted runner setup
#
# Run this ONCE on your Proxmox LXC after creating it.
#
# What it does:
#   1. Creates a dedicated 'runner' user (never runs as root)
#   2. Downloads and configures the GitHub Actions runner agent
#   3. Registers the runner with your GitHub repo
#   4. Installs it as a systemd service (starts on boot)
#   5. Grants the runner permission to restart the app service
#
# Usage:
#   bash deploy/01-install-runner.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# ── CONFIG — fill these in ────────────────────────────────────────────────────
GITHUB_REPO="smarterskipper/Countdown.nanobox.gg"   # owner/repo
RUNNER_TOKEN=""          # paste from: GitHub repo → Settings → Actions →
                         # Runners → New self-hosted runner → "Configure" step
RUNNER_NAME="homelab-lxc"
RUNNER_LABELS="self-hosted,homelab"
RUNNER_VERSION="2.321.0"  # check latest at github.com/actions/runner/releases
# ─────────────────────────────────────────────────────────────────────────────

if [[ -z "$RUNNER_TOKEN" ]]; then
  echo ""
  echo "  ERROR: RUNNER_TOKEN is empty."
  echo ""
  echo "  Get it from:"
  echo "  https://github.com/${GITHUB_REPO}/settings/actions/runners/new"
  echo ""
  echo "  It looks like: AXXXXXXXXXXXXXXXXXXXXXXXXX"
  echo "  (valid for 1 hour, just used during registration)"
  echo ""
  exit 1
fi

echo "==> Creating runner user"
id runner &>/dev/null || useradd -m -s /bin/bash runner

echo "==> Installing runner agent"
RUNNER_HOME="/home/runner/actions-runner"
mkdir -p "$RUNNER_HOME"
chown runner:runner "$RUNNER_HOME"

ARCH=$(uname -m)
case "$ARCH" in
  x86_64)  RUNNER_ARCH="x64"   ;;
  aarch64) RUNNER_ARCH="arm64" ;;
  *)       echo "Unsupported arch: $ARCH"; exit 1 ;;
esac

TARBALL="actions-runner-linux-${RUNNER_ARCH}-${RUNNER_VERSION}.tar.gz"
URL="https://github.com/actions/runner/releases/download/v${RUNNER_VERSION}/${TARBALL}"

curl -fsSL -o "/tmp/${TARBALL}" "$URL"
tar xzf "/tmp/${TARBALL}" -C "$RUNNER_HOME"
chown -R runner:runner "$RUNNER_HOME"
rm "/tmp/${TARBALL}"

echo "==> Registering runner with GitHub"
sudo -u runner bash -c "
  cd $RUNNER_HOME
  ./config.sh \
    --url https://github.com/${GITHUB_REPO} \
    --token ${RUNNER_TOKEN} \
    --name ${RUNNER_NAME} \
    --labels ${RUNNER_LABELS} \
    --unattended \
    --replace
"

echo "==> Installing runner as systemd service"
cd "$RUNNER_HOME"
./svc.sh install runner
./svc.sh start

echo "==> Granting runner permission to manage the app service (passwordless sudo)"
cat > /etc/sudoers.d/runner-homecountdown << 'EOF'
runner ALL=(ALL) NOPASSWD: /bin/systemctl start homecountdown
runner ALL=(ALL) NOPASSWD: /bin/systemctl stop homecountdown
runner ALL=(ALL) NOPASSWD: /bin/systemctl restart homecountdown
runner ALL=(ALL) NOPASSWD: /bin/rm -rf /opt/homecountdown/current
runner ALL=(ALL) NOPASSWD: /bin/mv /opt/homecountdown/next /opt/homecountdown/current
EOF
chmod 440 /etc/sudoers.d/runner-homecountdown

echo ""
echo "✓ Runner installed and running."
echo ""
echo "  Check status:   sudo systemctl status actions.runner.*"
echo "  View in GitHub: https://github.com/${GITHUB_REPO}/settings/actions/runners"
echo ""
echo "Next: run deploy/02-bootstrap-lxc.sh to install .NET, nginx, and the app service."
