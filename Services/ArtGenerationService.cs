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

    public async Task<DailyArt> GenerateAndCacheAsync(DateOnly date, HolidayInfo holiday)
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

            svg = await GenerateSvgAsync(holiday, critique, attempt);

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

    private async Task<string> GenerateSvgAsync(HolidayInfo holiday, string previousCritique, int attempt)
    {
        var critiqueSection = attempt > 1 && !string.IsNullOrEmpty(previousCritique)
            ? $"\n\nIMPROVEMENT REQUIRED — previous critique: {previousCritique}\nAddress every point in this critique."
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
            You are painting a Bob Ross "Joy of Painting" style SVG landscape for:

            Holiday: {{holiday.Name}}
            Local Name: {{holiday.LocalName}}
            Country: {{holiday.CountryName}}
            Date: {{holiday.Date:MMMM d, yyyy}}
            {{critiqueSection}}

            {{palette}}

            ═══ CORE REQUIREMENT: DENSITY & DETAIL ═══
            This painting MUST contain AT LEAST 200 individual SVG elements (paths, rects,
            ellipses, polygons, text). Count every shape. A cluster of trees = 15–25 shapes.
            A sky = 30+ overlapping cloud ellipses. A mountain range = 8–12 path segments.
            DO NOT produce sparse art. Every region of the canvas must be richly layered.

            ═══ GAUSSIAN BLUR IS MANDATORY ═══
            Bob Ross oil painting = soft, blended, zero hard edges. Achieve this with:

            Define these filters in <defs>:
            <filter id="soft-bg"><feGaussianBlur stdDeviation="6"/></filter>
            <filter id="soft-mid"><feGaussianBlur stdDeviation="3"/></filter>
            <filter id="soft-fg"><feGaussianBlur stdDeviation="1.5"/></filter>
            <filter id="glow"><feGaussianBlur stdDeviation="8" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
            <filter id="glow-sm"><feGaussianBlur stdDeviation="3" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>

            Apply filter rules:
            • Sky gradients, distant haze: filter="url(#soft-bg)"
            • All mountains/hills: filter="url(#soft-mid)"
            • All tree groups: filter="url(#soft-fg)"
            • Sun/moon/light source: filter="url(#glow)"
            • Stars, particles, fireflies: filter="url(#glow-sm)"
            • Water reflections: filter="url(#soft-mid)"

            ═══ LAYER 1 — SKY (40+ elements) ═══
            Build sky from scratch using ONLY gradients and overlapping shapes — no flat fill:

            Base sky: tall linearGradient, 5+ color stops from deep blue/purple at top
            to warm golden/peach at horizon. Apply to full-canvas rect.

            Cloud masses (25–35 overlapping ellipses per cloud formation):
            Each cloud = a cluster of rx 30–120, ry 20–60 ellipses at opacity 0.08–0.35.
            Use Titanium White, with occasional pale Prussian Blue for shadow undersides.
            Stagger across entire sky width. Apply filter="url(#soft-bg)" to each group.

            Sun OR moon (choose one based on holiday mood):
            Sun: radial gradient center (Cadmium Yellow → Indian Yellow → transparent),
            large outer glow circle at opacity 0.15, medium at 0.3, bright core.
            Moon: similar but Titanium White → cool Prussian Blue glow.
            Add god-rays: 6–10 thin wedge/line shapes radiating outward at opacity 0.06–0.12.

            Stars (if night): 20–30 small circles, radius 0.8–2.5, Titanium White,
            some with tiny radialGradient glow. class="flicker" with staggered delays.

            ═══ LAYER 2 — DISTANT MOUNTAINS (30+ elements) ═══
            Three distinct ridgelines at different depths:

            Far range (lightest, highest): irregular polygon path with gentle peaks,
            fill: linearGradient (Prussian Blue tint → fog white at base), opacity 0.5–0.6.
            filter="url(#soft-bg)". Snow caps: white ellipses at peak tips.

            Mid range: richer Prussian Blue → Dark Sienna gradient, opacity 0.75.
            filter="url(#soft-mid)". More dramatic peaks. Shadow sides darker.

            Near range: Van Dyke Brown → Phthalo Green gradient, opacity 0.9.
            filter="url(#soft-mid)". Visible texture. Tree-line silhouette at base.

            Each range = irregular cubic-bezier path with 8–15 control points.
            Overlap ranges so near obscures far at edges.

            ═══ LAYER 3 — TREES (60+ elements, the Bob Ross signature) ═══
            Bob Ross evergreen trees are built like this in SVG:

            ONE TREE = stacked teardrop/diamond shapes, largest at bottom, smallest at top:
            <ellipse cx="X" cy="Y+40" rx="22" ry="28" fill="Phthalo Green blend"/>
            <ellipse cx="X" cy="Y+20" rx="16" ry="22" fill="slightly lighter green"/>
            <ellipse cx="X" cy="Y"    rx="11" ry="16" fill="lighter still"/>
            <ellipse cx="X" cy="Y-15" rx="7"  ry="11" fill="tip, lightest"/>
            Highlight side: one thin ellipse at 30% width, opacity 0.35, Sap Green or Viridian.
            Dark side: one thin ellipse at 70% width, opacity 0.4, Midnight Black.

            Build 8–12 individual trees this way, varying heights (80–200px tall).
            Group each tree in <g class="tree" style="animation-delay:Xs transform-origin:centerX bottomY">
            Cluster trees in groups: 3 left side, 4 center-left, 5 center-right, 3 right.
            Apply filter="url(#soft-fg)" to each tree group.
            Trunk: thin brown rect under each tree, Van Dyke Brown.

            ═══ LAYER 4 — WATER (if applicable, 20+ elements) ═══
            Lake, river, or ocean using horizontal gradient bands:
            Base: linearGradient reflecting sky colors (Phthalo Blue → Prussian Blue → sky tones).
            20–25 thin horizontal rect strips (height 2–6px) at varying opacity 0.1–0.45,
            alternating between highlight (Titanium White tint) and shadow (Phthalo Blue).
            class="shimmer" on each strip with staggered animation-delay 0s–3s.
            Reflections: blurred vertical smear versions of trees/mountains, opacity 0.25–0.4.
            filter="url(#soft-mid)" on reflection group.
            Shore line: curved path, Yellow Ochre → Van Dyke Brown gradient.

            ═══ LAYER 5 — CULTURAL ELEMENTS ═══
            ONE prominent feature specific to {{holiday.CountryName}} and {{holiday.Name}}:
            This is what makes the painting unique. Examples:
            - Cherry blossoms: 40+ small petal shapes (class="float")
            - Lanterns: glowing rounded rectangles with glow filter
            - Traditional architecture silhouette: complex path
            - Desert dunes: layered smooth curves
            - Tropical palm trees: fan-shaped fronds
            Make this element the visual focal point, centered or slightly off-center.
            Use bright accent colors from the palette to make it pop.

            ═══ LAYER 6 — FOREGROUND GROUND (20+ elements) ═══
            Rich ground plane built from overlapping shapes:
            Base ground: large irregular path, Dark Sienna → Van Dyke Brown gradient.
            Grass: 15–20 thin blade clusters, Sap Green varying opacity 0.5–0.9.
              Each cluster = 3–5 thin ellipses tilted ±15deg. class="grass" staggered delays.
            Ground texture: 8–10 small irregular patches, Yellow Ochre / Van Dyke Brown.
            Rocks (optional): smooth grey ellipses at ground line, filter="url(#soft-fg)".
            Wildflowers: 10–15 tiny colored dots/circles (Bright Red, Cadmium Yellow).

            ═══ LAYER 7 — PARTICLES & ATMOSPHERE (20+ elements) ═══
            Floating particles appropriate to holiday/season:
            • 15–20 small shapes (petals, embers, fireflies, snowflakes, sparks)
            • Distributed across full canvas, not just one area
            • Each with class="float" and unique animation-delay (0s–8s)
            • filter="url(#glow-sm)" on warm-colored particles
            • Vary sizes: radius 1.5–6px

            Atmospheric haze: 3–4 large semi-transparent horizontal gradient rects at
            opacity 0.04–0.08 across sky-land boundary for depth of field.

            ═══ ANIMATION (define ALL in <style>) ═══
            @keyframes sway {
              0%,100% { transform: rotate(-1.2deg); } 50% { transform: rotate(1.2deg); }
            }
            @keyframes grass-wave {
              0%,100% { transform: skewX(-3deg) scaleY(0.97); } 50% { transform: skewX(3deg) scaleY(1.03); }
            }
            @keyframes shimmer {
              0%,100% { opacity: 0.15; } 50% { opacity: 0.45; }
            }
            @keyframes cloud-drift {
              0%,100% { transform: translateX(-6px); } 50% { transform: translateX(6px); }
            }
            @keyframes flicker {
              0%,100% { opacity: 0.5; } 33% { opacity: 1.0; } 66% { opacity: 0.7; }
            }
            @keyframes float {
              0% { transform: translateY(0) translateX(0); opacity: 0.8; }
              50% { transform: translateY(-18px) translateX(6px); opacity: 1; }
              100% { transform: translateY(-35px) translateX(-4px); opacity: 0; }
            }
            @keyframes sun-pulse {
              0%,100% { opacity: 0.85; filter: blur(6px); }
              50% { opacity: 1.0; filter: blur(9px); }
            }
            @keyframes water-glow {
              0%,100% { opacity: 0.2; } 50% { opacity: 0.5; }
            }

            .tree { animation: sway ease-in-out infinite; }
            .grass { animation: grass-wave ease-in-out infinite; transform-origin: bottom; }
            .shimmer { animation: shimmer ease-in-out infinite; }
            .cloud { animation: cloud-drift ease-in-out infinite; }
            .flicker { animation: flicker ease-in-out infinite; }
            .float { animation: float ease-in-out infinite; }
            .sun-glow { animation: sun-pulse ease-in-out infinite; }

            Apply unique animation-delay (0s to 6s) as inline style on EVERY animated element.
            Apply unique animation-duration variation (±20%) as inline style for organic feel.

            ═══ HOLIDAY TEXT ═══
            Large decorative text near bottom, the holiday name:
            <text x="450" y="555" text-anchor="middle" font-family="Georgia, serif"
              font-size="32" font-weight="bold" letter-spacing="3"
              fill="url(#textGrad)" filter="url(#glow-sm)"
              style="animation: shimmer 3s ease-in-out infinite">{{holiday.Name}}</text>
            Define textGrad as linearGradient using Titanium White → Cadmium Yellow → Titanium White.

            ═══ TECHNICAL ═══
            - viewBox="0 0 900 600" width="100%" height="100%"
            - Output ONLY valid SVG starting with <svg — zero markdown, zero explanation
            - All <defs> (gradients, filters) inside a single <defs> block at the top
            - BEFORE </svg> embed EXACTLY ONE LINE:
              <!-- ARTMETA: {"primaryColor":"#XXXXXX","secondaryColor":"#XXXXXX","accentColor":"#XXXXXX","theme":"2-4 word theme"} -->
              Replace #XXXXXX with actual dominant colors from your painting.

            Paint a masterpiece. Make Bob Ross proud.
            """;

        var messages = new List<Message> { new(RoleType.User, prompt) };
        var parameters = new MessageParameters
        {
            Messages = messages,
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 16000,
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
