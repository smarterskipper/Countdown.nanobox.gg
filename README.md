# HomelabCountdown

A creative Blazor Server countdown to **August 5th, 2026** — running on a Proxmox LXC behind nginx + Cloudflare Tunnel.

Every day the app finds a world holiday, commissions a bespoke SVG art piece from Claude, and uses it as a full-screen background. The countdown card, progress bar, and accent colors all adapt to that day's theme.

---

## Architecture

```
HomelabCountdown/
├── Components/
│   ├── App.razor               # HTML shell, MudBlazor CSS/JS
│   ├── Layout/MainLayout.razor # MudBlazor theme provider (no sidebar)
│   └── Pages/
│       ├── Home.razor          # Countdown + art background + WindowSwap PiP
│       └── Gallery.razor       # Grid of all past daily art pieces
├── Models/
│   ├── DailyArt.cs             # Metadata model (serialised to JSON cache)
│   └── HolidayInfo.cs          # Holiday DTO
├── Services/
│   ├── HolidayService.cs       # Nager.Date — finds today's world holiday
│   ├── ArtCacheService.cs      # Read/write art to wwwroot/art-cache/
│   ├── PlaywrightScreenshotService.cs  # Renders SVG → PNG via headless Chromium
│   ├── ArtGenerationService.cs # Agentic loop: generate → screenshot → score → refine
│   └── DailyArtHostedService.cs        # BackgroundService: runs on startup + daily at midnight
├── wwwroot/
│   ├── app.css                 # All custom styles (dark, glassmorphism)
│   └── art-cache/              # RUNTIME ONLY — git-ignored
│       ├── YYYY-MM-DD.svg      # Winning SVG art
│       ├── YYYY-MM-DD.png      # Playwright screenshot
│       └── YYYY-MM-DD.json     # DailyArt metadata
└── Program.cs                  # DI, MudBlazor, WindowSwap proxy endpoint
```

---

## Agentic Art Loop

```
DailyArtHostedService (startup + midnight)
  └─► ArtGenerationService.GenerateAndCacheAsync(date, holiday)
        ├─ [1–3 attempts]
        │   ├─ Claude claude-sonnet-4-6: generate SVG (system prompt + holiday context + prior critique)
        │   ├─ PlaywrightScreenshotService: render SVG → 900×600 PNG
        │   └─ Claude claude-sonnet-4-6 vision: score screenshot (JSON: score, critique, palette)
        └─ Cache winner → wwwroot/art-cache/{date}.{svg,png,json}
           └─ ArtCacheService.OnArtGenerated fires → Home.razor updates via SignalR
```

Pass threshold: **score ≥ 8/10**. If max attempts (3) reached, keeps the best result regardless.

---

## Stack

| Layer | Technology |
|---|---|
| Framework | .NET 9, Blazor Server (Interactive Server render mode) |
| UI | MudBlazor 9 |
| Holidays | Nager.Date 2.x (offline, no API key) |
| AI | Anthropic.SDK 5 → `claude-sonnet-4-6` |
| Screenshots | Microsoft.Playwright 1.x → headless Chromium |
| Proxy | Built-in minimal API endpoint (`/api/windowswap`) |
| Reverse proxy | nginx on LXC |
| Tunnel | Cloudflare Tunnel (no port-forwarding) |
| Version control | GitHub |

---

## Configuration

Set your Anthropic API key — **never commit it**:

### Option A — environment variable (recommended for production)
```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

### Option B — .NET User Secrets (development)
```bash
cd HomelabCountdown
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
```

### Option C — `appsettings.json` (not recommended)
Set `Anthropic.ApiKey` in the file — but **do not commit** the key.

---

## Running locally

```bash
# 1. Install Playwright browsers (first run only)
dotnet build
pwsh bin/Debug/net9.0/playwright.ps1 install chromium

# 2. Set API key
export ANTHROPIC_API_KEY=sk-ant-...

# 3. Run
dotnet run
# → https://localhost:5001
```

---

## Proxmox LXC Deployment

### 1. Create LXC (Debian 12 recommended)

In Proxmox UI: create unprivileged LXC, 2 CPU, 2 GB RAM, 10 GB disk.

### 2. Install .NET 9 runtime

```bash
wget https://dot.net/v1/dotnet-install.sh
bash dotnet-install.sh --runtime aspnetcore --version 9.0
echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc && source ~/.bashrc
```

### 3. Install Playwright system deps

```bash
apt-get update && apt-get install -y \
  libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 libgbm1 \
  libxcomposite1 libxdamage1 libxrandr2 libpango-1.0-0 libasound2 \
  libxshmfence1 libxfixes3 fonts-liberation libdbus-1-3
```

### 4. Publish and deploy

```bash
# On dev machine
dotnet publish -c Release -o ./publish

# Copy to LXC
rsync -av ./publish/ root@<LXC_IP>:/opt/homecountdown/

# On LXC — install browsers
cd /opt/homecountdown
ASPNETCORE_ENVIRONMENT=Production ./HomelabCountdown &
# First run auto-installs Chromium via Playwright
```

### 5. systemd service

```ini
# /etc/systemd/system/homecountdown.service
[Unit]
Description=HomelabCountdown Blazor App
After=network.target

[Service]
WorkingDirectory=/opt/homecountdown
ExecStart=/opt/homecountdown/HomelabCountdown
Restart=always
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ANTHROPIC_API_KEY=sk-ant-...
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000

[Install]
WantedBy=multi-user.target
```

```bash
systemctl enable --now homecountdown
```

### 6. nginx reverse proxy

```nginx
# /etc/nginx/sites-available/homecountdown
server {
    listen 80;
    server_name countdown.yourdomain.com;

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection "upgrade";
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
    }
}
```

```bash
ln -s /etc/nginx/sites-available/homecountdown /etc/nginx/sites-enabled/
nginx -t && systemctl reload nginx
```

### 7. Cloudflare Tunnel

```bash
# Install cloudflared on LXC
curl -L https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64 \
  -o /usr/local/bin/cloudflared && chmod +x /usr/local/bin/cloudflared

cloudflared tunnel login
cloudflared tunnel create homecountdown
cloudflared tunnel route dns homecountdown countdown.yourdomain.com

# Config: ~/.cloudflared/config.yml
# tunnel: <tunnel-id>
# credentials-file: /root/.cloudflared/<tunnel-id>.json
# ingress:
#   - hostname: countdown.yourdomain.com
#     service: http://localhost:80
#   - service: http_status:404

cloudflared service install
systemctl enable --now cloudflared
```

---

## Commit History (feature-by-feature)

| # | Commit | Contents |
|---|---|---|
| 1 | `scaffold: Blazor Server + MudBlazor + packages` | dotnet new, NuGet adds |
| 2 | `feat: models and holiday service` | DailyArt, HolidayInfo, HolidayService |
| 3 | `feat: art cache service` | ArtCacheService, wwwroot/art-cache dir |
| 4 | `feat: Playwright screenshot service` | PlaywrightScreenshotService |
| 5 | `feat: agentic art generation loop` | ArtGenerationService, DailyArtHostedService |
| 6 | `feat: home page countdown UI` | Home.razor, full-screen art background |
| 7 | `feat: gallery page` | Gallery.razor |
| 8 | `feat: WindowSwap proxy` | /api/windowswap endpoint |
| 9 | `style: dark glassmorphism CSS` | app.css |
| 10 | `docs: README + deployment guide` | README.md |
