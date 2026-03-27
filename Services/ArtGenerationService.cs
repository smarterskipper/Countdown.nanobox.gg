using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

public partial class ArtGenerationService
{
    private const int MaxAttempts = 3;
    private const double PassScore = 7.5;

    private readonly AnthropicClient _claude;
    private readonly PlaywrightScreenshotService _playwright;
    private readonly ArtCacheService _cache;
    private readonly GenerationStatusService _status;
    private readonly ILogger<ArtGenerationService> _logger;

    public ArtGenerationService(
        AnthropicClient claude,
        PlaywrightScreenshotService playwright,
        ArtCacheService cache,
        GenerationStatusService status,
        ILogger<ArtGenerationService> logger)
    {
        _claude = claude;
        _playwright = playwright;
        _cache = cache;
        _status = status;
        _logger = logger;
    }

    public async Task<DailyArt> GenerateAndCacheAsync(DateOnly date, HolidayInfo holiday, WeatherEffect? weather = null)
    {
        var existing = _cache.GetArtForDate(date);
        if (existing is not null)
        {
            _logger.LogInformation("Art already cached for {Date}", date);
            return existing;
        }

        _logger.LogInformation("Generating art for {Date}: {Holiday} ({Country})",
            date, holiday.Name, holiday.CountryName);

        string? svg = null;
        byte[]? screenshot = null;
        ArtScore? score = null;
        ArtScore? bestScore = null;
        string? bestSvg = null;
        byte[]? bestScreenshot = null;
        string critique = "";
        int attempt = 0;

        while (attempt < MaxAttempts)
        {
            attempt++;
            _logger.LogInformation("Art generation attempt {Attempt}/{Max}", attempt, MaxAttempts);
            _status.Update($"Painting attempt {attempt} of {MaxAttempts}…", attempt, MaxAttempts, bestScore?.Score);

            svg = await GenerateSvgAsync(holiday, critique, attempt, weather);

            // ── Code review (advisory only — always render) ───────────────
            _status.Update($"Reviewing composition… (attempt {attempt})", attempt, MaxAttempts, bestScore?.Score);
            var review = await ReviewSvgCodeAsync(svg);
            _logger.LogInformation("Code review — pass: {Pass}, issues: {Issues}", review.Pass, review.CriticalIssues);

            // Always render — code review issues just feed into next critique
            _status.Update($"Rendering… (attempt {attempt})", attempt, MaxAttempts, bestScore?.Score);
            screenshot = await _playwright.ScreenshotSvgAsync(svg);

            _status.Update($"Scoring… (attempt {attempt})", attempt, MaxAttempts, bestScore?.Score);
            score = await ScoreScreenshotAsync(screenshot, holiday);

            _logger.LogInformation("Attempt {Attempt} score: {Score}/10 — {Critique}", attempt, score.Score, score.Critique);

            // Track best result in case we exhaust attempts
            if (bestScore is null || score.Score > bestScore.Score)
            {
                bestScore = score;
                bestSvg = svg;
                bestScreenshot = screenshot;
            }

            // Build critique for next attempt from both visual and code feedback
            critique = score.Critique;
            if (!review.Pass && !string.IsNullOrEmpty(review.CriticalIssues))
                critique += $" Also fix: {review.CriticalIssues}";

            if (score.Score >= PassScore)
            {
                _logger.LogInformation("Art passed on attempt {Attempt} with score {Score}", attempt, score.Score);
                break;
            }

            if (attempt >= MaxAttempts)
                _logger.LogInformation("Max attempts reached, using best result (score {Score})", bestScore.Score);
        }

        // Use best result if final attempt wasn't the best
        if (bestScore is not null && (score is null || bestScore.Score > score.Score))
        {
            svg = bestSvg;
            screenshot = bestScreenshot;
            score = bestScore;
        }

        var art = new DailyArt
        {
            Date = date,
            HolidayName = holiday.Name,
            HolidayLocalName = holiday.LocalName,
            CountryCode = holiday.CountryCode,
            CountryName = holiday.CountryName,
            PrimaryColor = score?.PrimaryColor ?? "#6366f1",
            SecondaryColor = score?.SecondaryColor ?? "#8b5cf6",
            AccentColor = score?.AccentColor ?? "#f59e0b",
            Theme = score?.Theme ?? holiday.Name,
            AttemptCount = attempt,
            FinalScore = score?.Score ?? 0,
            FinalCritique = critique
        };

        _status.Update("Saving…", attempt, MaxAttempts, score?.Score);
        await _cache.SaveArtAsync(art, svg!, screenshot!);
        _status.Clear();
        return art;
    }

    private async Task<string> GenerateSvgAsync(HolidayInfo holiday, string previousCritique, int attempt, WeatherEffect? weather = null)
    {
        var critiqueSection = attempt > 1 && !string.IsNullOrEmpty(previousCritique)
            ? $"\n\nIMPROVEMENT REQUIRED — previous critique: {previousCritique}\nAddress every point in this critique."
            : "";

        var weatherSection = weather is not null
            ? $"""

              REAL WEATHER IN LEHI, UTAH RIGHT NOW:
              Condition: {weather.Type} — {weather.Description}
              Intensity: {weather.Intensity:F2} (0=calm, 1=extreme)
              Color palette hint: {weather.Color}

              Incorporate this weather VISUALLY into the painting as an extra atmospheric layer:
              - "sun" / "heat": Strong sun disk, long golden god-rays, heat shimmer on ground
              - "rain": Heavy rain streaks (60+ thin angled lines), dark clouds, wet ground reflections
              - "snow": 50+ falling snowflakes, snow-capped trees, white ground cover
              - "storm": Dramatic dark clouds, lightning bolt path, heavy rain, turbulent tree sway
              - "fog": Dense fog layers (opacity 0.15–0.30), muted colors, reduced visibility
              - "wind": Bent trees (increased sway animation), streaking horizontal cloud wisps
              - "cloud": Overcast sky, soft diffused light, grey cloud dominance
              - "aurora": Northern lights curtains (multi-color gradient rects, animated shimmer)
              The weather must be unmistakably visible in the painting.
              """
            : "";


        // Bob Ross color palette mapped to hex
        const string palette = """
            BOB ROSS MASTER PALETTE — use these exact colors:
            • Titanium White   #F4F1E8   (highlights, snow, clouds, foam)
            • Phthalo Blue     #0D1B40   (deep sky, shadows, water depth)
            • Prussian Blue    #1C3A5E   (sky mid-tones, distant mountains)
            • Midnight Black   #111118   (deepest darks, tree trunks)
            • Van Dyke Brown   #3E1A04   (earth, bark, dark foreground)
            • Dark Sienna      #6B2C0A   (warm earth, rocks, shadows)
            • Indian Yellow    #E08B0C   (warm light, autumn leaves, golden hour)
            • Cadmium Yellow   #F5C400   (sun, bright flowers, light accents)
            • Bright Red       #C42200   (vibrant accents, flowers, berries)
            • Alizarin Crimson #8B0F14   (deep warm tones, sunset, flowers)
            • Sap Green        #2C5A1C   (foliage, meadows, moss)
            • Phthalo Green    #0B3B1C   (dark evergreen trees, deep forest)
            • Viridian Green   #1A5C3A   (middle foliage, fresh leaves)
            • Yellow Ochre     #C8901A   (dry grass, sandy ground, warm light)
            Mix these using gradients and opacity — never use pure fills.
            """;

        var prompt = $$"""
            You are painting a museum-quality Bob Ross "Joy of Painting" SVG landscape.
            This must be the most detailed SVG painting ever created — extraordinarily rich,
            deeply layered, with photographic complexity achieved through pure SVG shapes.

            Holiday: {{holiday.Name}}
            Local Name: {{holiday.LocalName}}
            Country: {{holiday.CountryName}}
            Date: {{holiday.Date:MMMM d, yyyy}}
            {{critiqueSection}}
            {{weatherSection}}

            {{palette}}

            ═══ ABSOLUTE MINIMUM: 1,400 SVG ELEMENTS ═══
            Count every <ellipse>, <rect>, <path>, <circle>, <polygon>, <line>, <text>.
            If your painting has fewer than 1,400 elements it is INCOMPLETE. Keep painting.
            Every square pixel of canvas must be covered by richly layered shapes.
            Sparse areas = failure. Stack 8–15 overlapping shapes per region minimum.

            ═══ FILTERS — define ALL in <defs> ═══
            <filter id="soft-bg" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="7"/></filter>
            <filter id="soft-mid" x="-10%" y="-10%" width="120%" height="120%"><feGaussianBlur stdDeviation="3.5"/></filter>
            <filter id="soft-fg" x="-5%" y="-5%" width="110%" height="110%"><feGaussianBlur stdDeviation="1.8"/></filter>
            <filter id="soft-xfg" x="-5%" y="-5%" width="110%" height="110%"><feGaussianBlur stdDeviation="0.8"/></filter>
            <filter id="glow-lg"><feGaussianBlur stdDeviation="12" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
            <filter id="glow"><feGaussianBlur stdDeviation="7" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
            <filter id="glow-sm"><feGaussianBlur stdDeviation="3" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
            <filter id="haze"><feGaussianBlur stdDeviation="18"/></filter>

            ═══ LAYER A — DEEP SKY ATMOSPHERE (280+ elements) ═══

            Sub-layer A1 — Sky gradient base (8 elements):
            Full-canvas rect with 8-stop linearGradient (y1=0% y2=100%):
            stops: deep indigo #0D1B40 0%, Prussian Blue #1C3A5E 20%, slate blue #2A4A6E 38%,
            soft blue-grey #4A6A8E 52%, warm peach-grey #8A7060 68%, golden #C8801A 82%,
            warm amber #E08B0C 92%, pale gold #F5C400 100%.
            Layer 3 more gradient rects at opacity 0.12–0.25 for atmospheric bands.

            Sub-layer A2 — Cirrus clouds (60 elements):
            Long wispy streaks: 20 thin ellipses (rx 80–180, ry 4–12) near top of sky.
            Color: Titanium White #F4F1E8 opacity 0.06–0.18. Slightly rotated (±8deg).
            Group in <g class="cloud" filter="url(#soft-bg)"> with staggered delays.
            Add 40 more tiny cirrus wisps (rx 20–60, ry 2–6) at opacity 0.04–0.12.

            Sub-layer A3 — Cumulus cloud banks (120 elements):
            Build 4 major cloud formations, each = 30 overlapping ellipses:
            Formation 1 (top-left): ellipses scattered cx 50–250, cy 60–130.
              Core: 8 large ellipses rx 60–120, ry 35–65, White opacity 0.18–0.30.
              Mid: 12 medium rx 30–70, ry 20–40 opacity 0.10–0.20.
              Wisp edges: 10 small rx 10–30, ry 8–18 opacity 0.05–0.12.
            Formation 2 (top-right): cx 650–850, cy 40–110. Same 30-ellipse structure.
            Formation 3 (mid-left): cx 100–350, cy 120–170. Flatter, more horizontal.
            Formation 4 (mid-right): cx 550–800, cy 100–160. Flatter formation.
            All cloud groups: filter="url(#soft-bg)" class="cloud".
            Add Prussian Blue shadow ellipses on underside of each cloud, opacity 0.08–0.15.

            Sub-layer A4 — Light source (40 elements):
            Sun or Moon based on holiday mood — centered at roughly cx=680, cy=90:
            Outer corona: 6 concentric circles radii 120→60, radialGradient transparent→color, opacity 0.04→0.12.
            Inner glow: circle r=45, radialGradient core color→transparent, filter="url(#glow-lg)".
            Bright core: circle r=22, solid color, filter="url(#glow)".
            God-rays: 14 thin wedge paths (polygon) radiating out, length 180–280px,
              width 8–25px at base tapering to 0, opacity 0.03–0.09, class="sun-glow".
            Atmospheric ring: 3 ellipses rx 200–350 around sun at opacity 0.04–0.08.
            If night: add 60 stars — mix of plain circles r 0.8–2 and glowing circles
              with radialGradient, scattered across sky, class="flicker", filter="url(#glow-sm)".

            Sub-layer A5 — Sky depth layers (52 elements):
            6 large gradient rects spanning full width at different sky heights:
              Each at opacity 0.03–0.07, warm/cool tones for atmospheric perspective.
            Horizon glow band: 4 wide rects near y=290–340, warm peach/gold, opacity 0.08–0.15,
              filter="url(#haze)".
            8 more scattered cloud wisps at horizon, compressed flat, opacity 0.06–0.14.

            ═══ LAYER B — MOUNTAIN RANGES (280+ elements) ═══
            Seven distinct ridgelines from farthest to nearest. Each deeper = darker + more detail.

            Range 1 — Ultraviolet far range (y peaks 140–180):
            Cubic bezier path with 18 control points. Color: linearGradient #E8EAF0 op 0.18 → #C8D0D8 op 0.
            4 snow cap ellipses at tallest peaks, white, opacity 0.35. filter="url(#soft-bg)".

            Range 2 — Pale haze range (y peaks 155–200):
            22-point bezier. Prussian Blue + 40% white mix at top → transparent at base.
            6 snow ellipses. 8 tiny shadow triangles on steep faces. filter="url(#soft-bg)".

            Range 3 — Blue-grey mid-far (y peaks 170–230):
            25-point bezier. #1C3A5E → #2A4A6E gradient. Opacity 0.55.
            10 snow/ice ellipses. 5 avalanche-scar pale streaks (thin paths). filter="url(#soft-mid)".

            Range 4 — Slate mid range (y peaks 190–260):
            28-point bezier. Prussian Blue → Dark Sienna blend. Opacity 0.72.
            12 snow ellipses, 8 rock-face shadow paths, 4 mist wisps at base. filter="url(#soft-mid)".

            Range 5 — Dark mid-near (y peaks 210–290):
            30-point bezier. Van Dyke Brown → Phthalo Green → Midnight Black gradient. Opacity 0.82.
            8 exposed cliff face paths (lighter rocky grey). 15 small tree-silhouette bumps along ridge.
            6 shadow valley fills. filter="url(#soft-mid)".

            Range 6 — Near hills left & right flanking (2 separate paths, y 260–340):
            Each = 20-point bezier, Phthalo Green → Van Dyke Brown. Opacity 0.88.
            Each hill: 12 tree silhouettes along crest, 6 rock shadows, 4 bright highlight paths.
            filter="url(#soft-mid)".

            Range 7 — Immediate background ridge (y 290–360):
            Richest detail. 35-point bezier, near-black at peaks → brown-green at base.
            20 individual tree silhouette ellipses along the skyline. 10 rock formations.
            8 highlight paths on sun-facing slopes. filter="url(#soft-fg)".

            Between each range pair: 2 atmospheric mist rects, opacity 0.04–0.09, filter="url(#haze)".

            ═══ LAYER C — DEEP FOREST TREELINE (280+ elements) ═══
            Build 35 individual Bob Ross trees across the mid-ground. Each tree is:

            SMALL BACKGROUND TREES (10 trees at y 300–360, scale 0.4–0.6):
            Each = 6-ellipse stack:
              Base ellipse: rx 14 ry 18, Phthalo Green #0B3B1C
              Layer 2: rx 10 ry 14, mix Phthalo + Sap Green
              Layer 3: rx 7 ry 10, Sap Green #2C5A1C
              Layer 4: rx 4 ry 7, lighter Sap Green
              Highlight: rx 3 ry 8 offset left, Viridian #1A5C3A opacity 0.4
              Shadow: rx 3 ry 8 offset right, Midnight Black opacity 0.35
            Trunk: rect w=2 h=8, Van Dyke Brown. Group: filter="url(#soft-mid)".

            MID TREES (15 trees at y 320–400, scale 0.7–1.0):
            Each = 9-ellipse stack:
              Layer 1: rx 26 ry 34, #0B3B1C
              Layer 2: rx 20 ry 26, blend #0B3B1C + #2C5A1C
              Layer 3: rx 15 ry 20, #2C5A1C
              Layer 4: rx 11 ry 15, lighter #2C5A1C
              Layer 5: rx 7 ry 11, Viridian
              Layer 6: rx 4 ry 7, tip Viridian + Yellow
              Left highlight: rx 4 ry 20 shifted -8, Sap Green opacity 0.45
              Right shadow: rx 4 ry 20 shifted +8, Midnight Black opacity 0.45
              Snow/frost optional: rx 8 ry 3 at top, white opacity 0.2
            Trunk: rect w=3 h=20, Van Dyke Brown + thin shadow rect.
            Group: class="tree", filter="url(#soft-fg)".

            FOREGROUND LARGE TREES (10 trees at y 360–480, scale 1.2–1.8):
            Each = 12-ellipse stack for maximum detail:
              Base: rx 38 ry 50, Phthalo Green
              Layers 2–8: progressively smaller, lighter, spanning full height
              Left light face: 2 thin ellipses, Viridian → Sap Green, opacity 0.4–0.5
              Right dark face: 2 thin ellipses, Midnight Black opacity 0.4–0.5
              Bark texture: 3 thin vertical rects on trunk, varying brown shades
              Branch stubs: 4 small horizontal ellipses emerging from trunk sides
            Trunk: rect w=6 h=40 + shadow rect w=2 h=40. filter="url(#soft-xfg)".

            Between all trees: fill gaps with:
            - 20 bush/shrub clusters (3–4 small ellipses each, Sap Green)
            - 15 low ground-cover ellipses (very flat, Phthalo Green, opacity 0.5–0.8)
            - 10 fallen branch lines (thin paths, Van Dyke Brown)

            ═══ LAYER D — WATER BODY (140+ elements) ═══
            A lake, river, bay, or ocean fills the lower-center of the painting.

            Sub-layer D1 — Water base (20 elements):
            Base rect with 6-stop linearGradient (sky reflection): deep Phthalo Blue → Prussian Blue → grey-blue.
            4 large irregular path overlays for water color variation, opacity 0.15–0.30.

            Sub-layer D2 — Shimmer strips (70 elements):
            70 horizontal rects stacked y=380–520, each h=2–5px, full width or partial:
            Odd strips: Titanium White opacity 0.05–0.18, class="shimmer" staggered.
            Even strips: Phthalo Blue opacity 0.08–0.20.
            Every 5th strip: slightly wider, Indian Yellow tint for golden reflections.

            Sub-layer D3 — Mountain reflections (25 elements):
            Mirror the mountain ridgelines: 7 blurred inverted mountain-shape paths,
            each progressively more distorted and horizontal-stretched.
            Opacity 0.12–0.28, filter="url(#soft-mid)". class="shimmer".

            Sub-layer D4 — Tree reflections (15 elements):
            15 blurred vertical ellipses beneath each tree cluster,
            Phthalo Green tones, stretched 2× vertically, opacity 0.15–0.22.
            filter="url(#soft-mid)".

            Sub-layer D5 — Shore & foam (10 elements):
            2 irregular shore-line paths, Yellow Ochre → Van Dyke Brown.
            5 foam/wave edge paths, Titanium White opacity 0.3–0.5, filter="url(#soft-fg)".
            3 wet-sand reflection rects near shore, pale gold, opacity 0.15.

            ═══ LAYER E — CULTURAL FOCAL ELEMENT (100+ elements) ═══
            The painting's unique centerpiece celebrating {{holiday.Name}} in {{holiday.CountryName}}.
            This must be RICHLY detailed — not a simple shape. Build it from 100+ sub-elements.
            Position as the visual focal point (center or golden-ratio offset).

            Examples of how to build with 100+ elements:
            CHERRY BLOSSOMS: 1 tree trunk (8 branch paths) + 80 petal ellipses in clusters
              of 5–8 per branch node, 4 colors (pink shades), class="float", glow filter.
              Plus 20 fallen petals on ground drifting.
            LANTERNS: 5 lanterns each = 12 shapes (body, top cap, bottom cap, 4 glow rings,
              2 tassel segments, 2 string segments, glow circle). Plus 40 scattered spark particles.
            TEMPLE/PAGODA: Multi-path silhouette with 5 roof tiers (each = 4 paths),
              walls, windows, gate, stone steps, garden elements = 80+ paths.
              Plus 20 surrounding elements (stone lanterns, bonsai, petals).
            NORTHERN LIGHTS (Aurora): 8 curtain shapes each = 12 gradient rects stacked,
              plus 30 star specks, 20 ground reflection ripples.
            DESERT/DUNES: 12 dune layers × 6 overlapping bezier paths each = 72 paths,
              plus 15 cactus shapes, 10 scattered rocks, 8 sand texture ellipses.

            Whatever you build, use the full Bob Ross palette with glow effects on light sources.

            ═══ LAYER F — FOREGROUND TERRAIN (140+ elements) ═══

            Sub-layer F1 — Ground base (15 elements):
            3 large irregular ground-plane paths spanning full width, layered:
            Deep base: Van Dyke Brown → Midnight Black.
            Mid: Dark Sienna → Van Dyke Brown, opacity 0.85.
            Top surface: Yellow Ochre → Sap Green, opacity 0.7.
            4 soil-texture paths, bumpy top edge, irregular colors.

            Sub-layer F2 — Grass (60 elements):
            60 individual grass cluster groups, each = 4–5 thin ellipses:
            Each ellipse: rx 1–3, ry 6–18, slightly tilted ±20deg, Sap Green.
            Vary opacity 0.4–0.95, vary height. class="grass" with staggered delays.
            Mix in 10 taller grass clumps (ry 25–40) as accent. Add Yellow Ochre dry grass.
            Transform-origin at bottom of each cluster.

            Sub-layer F3 — Ground detail (35 elements):
            12 rocks: smooth rounded ellipse clusters (3 ellipses each), grey/brown tones.
            filter="url(#soft-fg)". Some partially buried (clipped by ground rect).
            8 root/log shapes: thin curved paths, Van Dyke Brown.
            15 wildflowers: circle + 5 petal ellipses each, Bright Red / Cadmium Yellow.
              Each flower: center circle r=3, 5 surrounding petal ellipses, class="float".

            Sub-layer F4 — Foreground shadow & depth (30 elements):
            Large dark shadow rect at very bottom, Midnight Black opacity 0.4 (depth anchor).
            10 shadow pools under trees: irregular dark ellipses, opacity 0.25–0.45.
            8 light dapple patches: pale yellow ellipses on ground, opacity 0.08–0.15,
              filter="url(#glow-sm)".
            12 small pebble/dirt circles scattered on ground surface.

            ═══ LAYER G — ATMOSPHERIC PARTICLES (140+ elements) ═══

            Sub-layer G1 — Floating seasonal particles (80 elements):
            80 particles appropriate to the holiday/season:
            Distribute across full canvas (not clustered). Each has unique position.
            Types by holiday mood:
              Petals/leaves: small 5-point or ellipse shapes, holiday accent color
              Snow/ice: tiny circles r=1–4, white, some with glow
              Embers/sparks: orange-yellow dots with glow-sm filter
              Fireflies: tiny circles with large glow-lg halos
              Dust motes: pale grey ellipses near ground
            ALL: class="float", staggered animation-delay 0s–12s, unique duration 4s–16s.
            filter="url(#glow-sm)" on luminous particles.

            Sub-layer G2 — Mist and fog layers (30 elements):
            8 wide horizontal gradient rects spanning full canvas width at different heights:
            Heights: y=280, 310, 330, 350, 370, 400, 430, 460.
            Each: linearGradient transparent→white tint→transparent (horizontal).
            Opacity 0.03–0.10. filter="url(#haze)". class="shimmer".

            Sub-layer G3 — Light scatter (30 elements):
            15 light shaft paths from sun/moon position downward:
            Each = narrow wedge path opacity 0.02–0.06, Indian Yellow → transparent.
            filter="url(#soft-bg)". class="sun-glow".
            15 specular highlight ellipses on water/wet surfaces: Titanium White opacity 0.1–0.2.

            ═══ ANIMATION ═══
            @keyframes sway { 0%,100%{transform:rotate(-1.5deg)}50%{transform:rotate(1.5deg)} }
            @keyframes sway-sm { 0%,100%{transform:rotate(-0.7deg)}50%{transform:rotate(0.7deg)} }
            @keyframes grass-wave { 0%,100%{transform:skewX(-4deg) scaleY(0.96)}50%{transform:skewX(4deg) scaleY(1.04)} }
            @keyframes shimmer { 0%,100%{opacity:0.12}50%{opacity:0.42} }
            @keyframes shimmer-fast { 0%,100%{opacity:0.08}50%{opacity:0.35} }
            @keyframes cloud-drift { 0%,100%{transform:translateX(-8px)}50%{transform:translateX(8px)} }
            @keyframes cloud-drift-slow { 0%,100%{transform:translateX(-3px)}50%{transform:translateX(3px)} }
            @keyframes flicker { 0%,100%{opacity:0.4}33%{opacity:1.0}66%{opacity:0.65} }
            @keyframes float { 0%{transform:translateY(0) translateX(0);opacity:0.85} 50%{transform:translateY(-22px) translateX(8px);opacity:1} 100%{transform:translateY(-44px) translateX(-5px);opacity:0} }
            @keyframes float-slow { 0%{transform:translateY(0);opacity:0.7}50%{transform:translateY(-12px);opacity:0.9}100%{transform:translateY(-24px);opacity:0} }
            @keyframes sun-pulse { 0%,100%{opacity:0.8}50%{opacity:1.0} }
            @keyframes water-glow { 0%,100%{opacity:0.15}50%{opacity:0.45} }
            @keyframes rise { 0%{transform:scaleY(0.95)}50%{transform:scaleY(1.05)}100%{transform:scaleY(0.95)} }

            .tree{animation:sway 6s ease-in-out infinite}
            .tree-sm{animation:sway-sm 8s ease-in-out infinite}
            .grass{animation:grass-wave 3s ease-in-out infinite;transform-origin:bottom center}
            .shimmer{animation:shimmer 4s ease-in-out infinite}
            .shimmer-fast{animation:shimmer-fast 2s ease-in-out infinite}
            .cloud{animation:cloud-drift 18s ease-in-out infinite}
            .cloud-slow{animation:cloud-drift-slow 30s ease-in-out infinite}
            .flicker{animation:flicker 2s ease-in-out infinite}
            .float{animation:float 8s ease-in-out infinite}
            .float-slow{animation:float-slow 14s ease-in-out infinite}
            .sun-glow{animation:sun-pulse 4s ease-in-out infinite}
            .water-glow{animation:water-glow 3s ease-in-out infinite}

            Every single animated element MUST have a unique animation-delay (0s–14s) and
            a unique animation-duration that varies ±25% from the class default — add these
            as inline style="animation-delay:Xs;animation-duration:Ys" on every element.

            ═══ HOLIDAY TEXT ═══
            <text x="450" y="562" text-anchor="middle" font-family="Georgia, 'Times New Roman', serif"
              font-size="34" font-weight="bold" letter-spacing="4"
              fill="url(#textGrad)" filter="url(#glow)"
              style="animation:shimmer 3s ease-in-out infinite;animation-delay:0.5s">{{holiday.Name}}</text>
            Shadow text behind it: same text, fill="#111118" opacity="0.5" y="564" no filter.
            textGrad: linearGradient Titanium White → Cadmium Yellow → Indian Yellow → Titanium White.

            ═══ TECHNICAL ═══
            - viewBox="0 0 900 600" width="100%" height="100%"
            - Output ONLY valid SVG starting with <svg — zero markdown, zero code fences
            - All <defs> (gradients, filters, patterns) in ONE <defs> block at the top
            - Every gradient, filter, pattern referenced must be defined in <defs>
            - BEFORE </svg> embed exactly:
              <!-- ARTMETA: {"primaryColor":"#XXXXXX","secondaryColor":"#XXXXXX","accentColor":"#XXXXXX","theme":"2-4 word theme"} -->

            This is a masterpiece. Paint every layer. Do not stop until 1,400+ elements are placed.
            Make Bob Ross weep with joy.
            """;

        var messages = new List<Message> { new(RoleType.User, prompt) };
        var parameters = new MessageParameters
        {
            Messages = messages,
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 32000,
            Stream = false,
            Temperature = 1.0m
        };

        var response = await _claude.Messages.GetClaudeMessageAsync(parameters);
        var raw = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";

        return ExtractSvg(raw);
    }

    private async Task<ArtScore> ScoreScreenshotAsync(byte[] screenshotPng, HolidayInfo holiday)
    {
        var base64 = Convert.ToBase64String(screenshotPng);

        var prompt = $$"""
            You are an art critic evaluating an AI-generated SVG artwork for this holiday:
            Holiday: {{holiday.Name}} ({{holiday.CountryName}})

            Score the image 1–10 based on:
            - Visual appeal and beauty (3 pts)
            - Cultural relevance and symbolism (3 pts)
            - Composition and balance (2 pts)
            - Color harmony (2 pts)

            Respond ONLY with valid JSON on a single line, no other text:
            {"score":8.5,"critique":"specific improvement notes or empty string if perfect","primaryColor":"#rrggbb","secondaryColor":"#rrggbb","accentColor":"#rrggbb","theme":"2-4 word theme description"}
            """;

        var messages = new List<Message>
        {
            new()
            {
                Role = RoleType.User,
                Content = new List<ContentBase>
                {
                    new ImageContent
                    {
                        Source = new ImageSource { MediaType = "image/png", Data = base64 }
                    },
                    new TextContent { Text = prompt }
                }
            }
        };

        var parameters = new MessageParameters
        {
            Messages = messages,
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 512,
            Stream = false,
            Temperature = 0m
        };

        var response = await _claude.Messages.GetClaudeMessageAsync(parameters);
        var json = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";

        return ParseScore(json);
    }

    private async Task<CodeReview> ReviewSvgCodeAsync(string svg)
    {
        var svgPreview = svg[..Math.Min(svg.Length, 6000)];
        var prompt = $"""
            You are an SVG code reviewer. Inspect this SVG and check for technical correctness.

            REQUIRED checks (all must pass):
            1. Has <svg with viewBox="0 0 900 600", width="100%", height="100%"
            2. Has a <style> block with at least 6 distinct @keyframes defined
            3. Every @keyframes name referenced in animation: properties actually exists in the <style> block
            4. Every class name used in animation: actually appears on at least one SVG element (e.g. class="sway")
            5. SVG <filter> elements: if present, all result= attributes referenced in later filter primitives exist
            6. Gradient IDs referenced in fill="url(#...)" or stroke="url(#...)" are defined in <defs>
            7. Contains ARTMETA comment with valid JSON before </svg>
            8. Has meaningful layered content — not just a plain gradient rect (must have shapes/paths/text/symbols)
            9. Individual natural elements (trees, grass, clouds, water) each have animation classes applied,
               not just one global animation — check that multiple elements have class= attributes with animation

            If any check fails, set pass=false and list the specific failing checks in critical_issues.
            Respond ONLY with valid JSON on one line, fields: pass, critical_issues, suggestions, animation_count

            SVG to review (first 6000 chars):
            {svgPreview}
            """;

        try
        {
            var response = await _claude.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Messages = [new Message(RoleType.User, prompt)],
                Model = AnthropicModels.Claude46Sonnet,
                MaxTokens = 512,
                Stream = false,
                Temperature = 0m
            });

            var json = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
            var clean = JsonFenceRegex().Replace(json, "").Trim();
            var doc = JsonDocument.Parse(clean);
            var root = doc.RootElement;

            return new CodeReview
            {
                Pass = root.TryGetProperty("pass", out var p) && p.GetBoolean(),
                CriticalIssues = root.TryGetProperty("critical_issues", out var ci) ? ci.GetString() ?? "" : "",
                Suggestions = root.TryGetProperty("suggestions", out var s) ? s.GetString() ?? "" : "",
                AnimationCount = root.TryGetProperty("animation_count", out var ac) ? ac.GetInt32() : 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Code review parse failed, treating as pass");
            return new CodeReview { Pass = true };
        }
    }

    private static string ExtractSvg(string raw)
    {
        var start = raw.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        var end = raw.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);

        if (start >= 0 && end > start)
            return raw[start..(end + 6)];

        // If Claude wrapped in code fence but still valid SVG-ish, return as-is
        return raw.Trim();
    }

    private static ArtScore ParseScore(string json)
    {
        try
        {
            // Strip code fences if present
            var clean = JsonFenceRegex().Replace(json, "").Trim();
            var doc = JsonDocument.Parse(clean);
            var root = doc.RootElement;

            return new ArtScore
            {
                Score = root.TryGetProperty("score", out var s) ? s.GetDouble() : 5.0,
                Critique = root.TryGetProperty("critique", out var c) ? c.GetString() ?? "" : "",
                PrimaryColor = root.TryGetProperty("primaryColor", out var p) ? p.GetString() ?? "#6366f1" : "#6366f1",
                SecondaryColor = root.TryGetProperty("secondaryColor", out var sec) ? sec.GetString() ?? "#8b5cf6" : "#8b5cf6",
                AccentColor = root.TryGetProperty("accentColor", out var a) ? a.GetString() ?? "#f59e0b" : "#f59e0b",
                Theme = root.TryGetProperty("theme", out var t) ? t.GetString() ?? "" : ""
            };
        }
        catch
        {
            return new ArtScore { Score = 5.0, Critique = "Could not parse score response" };
        }
    }

    [GeneratedRegex(@"```[a-z]*\n?|```")]
    private static partial Regex JsonFenceRegex();

    private sealed class CodeReview
    {
        public bool Pass { get; set; }
        public string CriticalIssues { get; set; } = "";
        public string Suggestions { get; set; } = "";
        public int AnimationCount { get; set; }
    }

    private sealed class ArtScore
    {
        public double Score { get; set; }
        public string Critique { get; set; } = "";
        public string PrimaryColor { get; set; } = "#6366f1";
        public string SecondaryColor { get; set; } = "#8b5cf6";
        public string AccentColor { get; set; } = "#f59e0b";
        public string Theme { get; set; } = "";
    }
}
