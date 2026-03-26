# HomelabCountdown

A creative Blazor Server countdown to **August 5th, 2026** — running on a Proxmox LXC behind Cloudflare Tunnel.

Every day the app finds a real world holiday, commissions a bespoke **Bob Ross-style animated SVG painting** from Claude, and uses it as a full-screen living background. Trees sway, water shimmers, clouds drift. The countdown card, progress bar colors, and weather overlay all adapt to that day's theme.

---

## Features

- **Daily AI art** — Claude (`claude-sonnet-4-6`) generates an oil-painting-style animated SVG each day themed around a real holiday from [timeanddate.com](https://www.timeanddate.com/holidays/us/)
- **Living animations** — every SVG element is animated: trees sway, grass waves, water shimmers, clouds drift, birds glide
- **Weather overlay** — Claude picks a weather effect (rain, snow, aurora, wind, etc.) tied to a real-world weather fact; particles animate over the art
- **Viewer tracking sidebar** — real-time geolocation of visitors (country flags, US state flags + abbreviations, visit counts) stored in SQLite
- **Art gallery** — `/gallery` shows all past days with view counts; click any card to open a detail modal or view the full day's page
- **Historical day view** — `/?date=YYYY-MM-DD` replays any past day exactly: art, weather, progress state, and viewer counts as they were
- **Discord notifications** — deploy started/succeeded/failed events + daily art embeds posted to a webhook
- **Self-hosted CI/CD** — GitHub Actions runner inside the LXC; push to `master` → build → publish → swap → health check

---

## Architecture

```
HomelabCountdown/
├── Components/Pages/
│   ├── Home.razor          # Countdown + art bg + weather + viewer sidebar
│   │                       # Accepts ?date=YYYY-MM-DD for historical replay
│   └── Gallery.razor       # Grid of all past art + modal + view counts
├── Models/
│   ├── DailyArt.cs         # Art metadata (serialised to JSON per day)
│   ├── HolidayInfo.cs      # Holiday DTO
│   ├── ViewerEntry.cs      # Geo visitor entry (key, country, state, count)
│   └── WeatherEffect.cs    # Weather type, color, date
├── Services/
│   ├── TimeAndDateHolidayService.cs  # Scrapes timeanddate.com once/year, scores & picks best holiday
│   ├── HolidayService.cs             # Seasonal/astronomical fallback (solstices, moon names, seasons)
│   ├── ArtCacheService.cs            # Read/write SVG+PNG+JSON to art-cache dir (persistent)
│   ├── ArtGenerationService.cs       # Agentic loop: generate → screenshot → score → refine (≤3 attempts)
│   ├── DailyArtHostedService.cs      # BackgroundService: fires on startup + daily at midnight
│   ├── WeatherService.cs             # Claude picks weather effect per day
│   ├── GeoLocationService.cs         # ip-api.com geolocation with in-memory cache
│   ├── ViewerDbService.cs            # SQLite: upsert visits, query by date
│   ├── ViewerTrackingService.cs      # Orchestrates geo + DB + in-memory cache + SignalR event
│   ├── PlaywrightScreenshotService.cs # Renders SVG → 900×600 PNG via headless Chromium
│   └── DiscordNotificationService.cs  # Posts art + CI/CD events to Discord webhook
├── wwwroot/app.css          # All custom styles: dark glass, progress bar heat waves, sparks, sidebar
└── Program.cs               # DI, tracking middleware, persistent art-cache static files
```

---

## Agentic Art Loop

```
DailyArtHostedService  (startup + daily midnight)
  └─► TimeAndDateHolidayService  →  picks best scored holiday from timeanddate.com
        (fallback: HolidayService seasonal/astronomical context)
  └─► ArtGenerationService.GenerateAndCacheAsync(date, holiday)
        ├─ [1–3 attempts, max_tokens=12000, HttpClient timeout=10min]
        │   ├─ Claude: generate Bob Ross-style animated SVG
        │   │          (dreamy sky, mountain silhouettes, happy little trees,
        │   │           reflective water, god-rays, per-element keyframe animations)
        │   ├─ Claude: code-review SVG (checks animations, viewBox, no JS)
        │   ├─ Playwright: render SVG → 900×600 PNG
        │   └─ Claude vision: score PNG (0–10) + critique + palette extraction
        └─ Pass threshold ≥ 8.0 → cache winner, else keep best after 3 attempts
           ├─ Files: art-cache/{date}.svg, .png, .json + weather-{date}.json
           ├─ ArtCacheService.OnArtGenerated → Home.razor live update via SignalR
           └─ DiscordNotificationService → embed with screenshot

  └─► WeatherService  →  Claude picks weather effect + real-world weather fact
```

---

## Holiday Scoring

Holidays from timeanddate.com are ranked by category:

| Score | Categories |
|---|---|
| 5 | Religious observances |
| 4 | Independence days, national holidays, seasonal/astronomical events |
| 3 | Nature, cultural, traditional |
| 1 | Awareness days |

Astronomical/seasonal fallback (when no holiday found): solstices, equinoxes, named moon phases, season labels.

---

## Stack

| Layer | Technology |
|---|---|
| Framework | .NET 9, Blazor Server (InteractiveServer render mode) |
| UI | MudBlazor 9, custom CSS |
| Holidays | timeanddate.com (HTML scrape, cached per year) |
| AI | Anthropic.SDK → `claude-sonnet-4-6` |
| Screenshots | Microsoft.Playwright → headless Chromium |
| Geolocation | ip-api.com (free, HTTP, cached in-memory) |
| Database | SQLite via Microsoft.Data.Sqlite (visitor counts) |
| Flags | flagcdn.com (country + US state subdivision flags) |
| Notifications | Discord webhooks |
| CI/CD | GitHub Actions (self-hosted runner on LXC 503) |
| Infra | Proxmox LXC, Cloudflare Tunnel |

---

## Configuration

All secrets are written to `/etc/homecountdown.env` by the CI/CD pipeline. For local dev:

```bash
# Option A — environment variables
export ANTHROPIC_API_KEY=sk-ant-...
export DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
export ArtCache__Path=/path/to/art-cache

# Option B — .NET User Secrets
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
```

---

## Running Locally

```bash
# 1. Install Playwright browsers (first run only)
dotnet build
pwsh bin/Debug/net9.0/playwright.ps1 install chromium

# 2. Set API key
export ANTHROPIC_API_KEY=sk-ant-...

# 3. Run
dotnet run
# → http://localhost:5000
```

---

## Proxmox LXC Deployment

The app deploys automatically via GitHub Actions on every push to `master`. A self-hosted runner lives inside LXC 503.

### CI/CD Pipeline (`.github/workflows/deploy.yml`)

```
push to master
  → Discord "deploy started" embed
  → checkout + dotnet restore + dotnet build
  → dotnet publish → /opt/homecountdown/next
  → write secrets to /etc/homecountdown.env
  → systemctl stop → swap next→current → systemctl start
  → health check (curl localhost:5000, 12 attempts × 5s)
  → Discord success/failure embed with service logs
```

### Persistent storage

Art files survive deploys — only `/opt/homecountdown/current` is swapped:

```
/var/lib/homecountdown/art-cache/
  ├── 2026-03-25.svg          # Animated SVG (source of truth)
  ├── 2026-03-25.png          # 900×600 screenshot (for gallery + video)
  ├── 2026-03-25.json         # Metadata (holiday, score, colors, critique)
  ├── weather-2026-03-25.json # Weather effect for the day
  └── countdown.db            # SQLite — visitor geo counts by date
```

### systemd service

```ini
# /etc/systemd/system/homecountdown.service
[Unit]
Description=HomelabCountdown Blazor App
After=network.target

[Service]
WorkingDirectory=/opt/homecountdown/current
ExecStart=/opt/homecountdown/current/HomelabCountdown
Restart=always
EnvironmentFile=/etc/homecountdown.env
User=runner

[Install]
WantedBy=multi-user.target
```

### Viewer tracking middleware

Fires on every real browser `GET /` request (skips bots/API calls by checking `Accept: text/html`). Reads client IP from `CF-Connecting-IP` (Cloudflare) → `X-Forwarded-For` → socket. Geo-lookup via ip-api.com, upserted into SQLite, reflected live in the sidebar via SignalR.
