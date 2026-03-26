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
    private const double PassScore = 8.0;

    private readonly AnthropicClient _claude;
    private readonly PlaywrightScreenshotService _playwright;
    private readonly ArtCacheService _cache;
    private readonly ILogger<ArtGenerationService> _logger;

    public ArtGenerationService(
        AnthropicClient claude,
        PlaywrightScreenshotService playwright,
        ArtCacheService cache,
        ILogger<ArtGenerationService> logger)
    {
        _claude = claude;
        _playwright = playwright;
        _cache = cache;
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
        string critique = "";
        int attempt = 0;

        while (attempt < MaxAttempts)
        {
            attempt++;
            _logger.LogInformation("Art generation attempt {Attempt}/{Max}", attempt, MaxAttempts);

            svg = await GenerateSvgAsync(holiday, critique, attempt);

            // ── Code review before rendering ──────────────────────────────
            var review = await ReviewSvgCodeAsync(svg);
            _logger.LogInformation("Code review — pass: {Pass}, animations: {Anim}, issues: {Issues}",
                review.Pass, review.AnimationCount, review.CriticalIssues);

            if (!review.Pass && attempt < MaxAttempts)
            {
                critique = $"CODE REVIEW FAILED — fix these before anything else: {review.CriticalIssues}. {review.Suggestions}";
                _logger.LogInformation("Skipping render, regenerating due to code issues");
                continue;
            }

            // ── Visual score ──────────────────────────────────────────────
            screenshot = await _playwright.ScreenshotSvgAsync(svg);
            score = await ScoreScreenshotAsync(screenshot, holiday);

            _logger.LogInformation("Attempt {Attempt} visual score: {Score}/10 — {Critique}",
                attempt, score.Score, score.Critique);

            // Combine visual critique with any code suggestions
            critique = score.Critique;
            if (!string.IsNullOrEmpty(review.Suggestions))
                critique += $" Code: {review.Suggestions}";

            if (score.Score >= PassScore && review.Pass)
            {
                _logger.LogInformation("Art passed all checks on attempt {Attempt}", attempt);
                break;
            }

            if (attempt >= MaxAttempts)
                _logger.LogInformation("Reached max attempts, keeping best result (score {Score})", score.Score);
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

        await _cache.SaveArtAsync(art, svg!, screenshot!);
        return art;
    }

    private async Task<string> GenerateSvgAsync(HolidayInfo holiday, string previousCritique, int attempt)
    {
        var critiqueSection = attempt > 1 && !string.IsNullOrEmpty(previousCritique)
            ? $"\n\nIMPROVEMENT REQUIRED — previous critique: {previousCritique}\nAddress every point in this critique."
            : "";

        var prompt = $$"""
            You are a world-class SVG artist creating a living, animated full-screen artwork celebrating:

            Holiday: {{holiday.Name}}
            Local Name: {{holiday.LocalName}}
            Country: {{holiday.CountryName}}
            Date: {{holiday.Date:MMMM d, yyyy}}
            {{critiqueSection}}

            ═══ COMPOSITION (must have all three layers) ═══
            BACKGROUND: Atmospheric sky, landscape, or environment using rich multi-stop gradients.
              Include stars, clouds, aurora, or weather tied to the culture/season.
            MIDGROUND: At least ONE recognizable cultural element — a famous landmark, architectural
              silhouette, mountain, ocean, forest, or traditional pattern specific to {{holiday.CountryName}}.
            FOREGROUND: Detailed symbolic objects, flora, or figures. A particle system of 10–20 small
              elements (petals, sparks, snowflakes, lanterns, stars, fireflies) scattered across canvas.

            ═══ ANIMATION — EVERY ELEMENT MUST FEEL ALIVE ═══
            This is a living painting. Every natural object gets its own animation.
            Apply animations via CSS classes with staggered animation-delay on each element
            so nothing moves in sync. All animations: infinite, ease-in-out.

            REQUIRED keyframes (define ALL of these in the <style> block):

            @keyframes sway — trees/plants rocking gently in wind
              transform: rotate(-1.5deg) → rotate(1.5deg), transform-origin at base (50% 100%)
              Duration: 3–5s per tree. Each tree gets a different animation-delay (0s, 0.4s, 0.9s…)

            @keyframes grass-wave — grass blades rippling like a wave
              transform: skewX(-4deg) scaleY(0.97) → skewX(4deg) scaleY(1.03)
              transform-origin: bottom. Each blade/cluster offset by 0.1–0.3s delay.

            @keyframes water-shimmer — horizontal shimmer bands on lakes/rivers
              opacity: 0.3 → 0.7 → 0.3, subtle scaleX(1) → scaleX(1.02) → scaleX(1)
              Duration: 2–4s. Multiple bands with different delays for ripple effect.

            @keyframes cloud-drift — clouds slowly drifting across sky
              transform: translateX(-8px) → translateX(8px). Duration: 18–30s.
              Each cloud at a different speed and delay.

            @keyframes flicker — fire, lanterns, stars, fireflies pulsing
              opacity: 0.6 → 1.0 → 0.7 → 1.0. Duration: 0.8–2s. Rapid, organic.

            @keyframes float — particles, petals, embers rising gently
              transform: translateY(0) translateX(0) → translateY(-15px) translateX(5px)
              opacity fade in/out. Duration: 4–8s.

            @keyframes sun-pulse — sun/moon breathing with a soft glow
              filter: blur(2px) brightness(1) → blur(4px) brightness(1.15). Duration: 4–6s.

            @keyframes bird-glide — birds or leaves arcing across sky (optional)
              transform: translateX(-60px) translateY(0) → translateX(60px) translateY(-10px)
              Duration: 8–14s.

            IMPLEMENTATION RULES:
            - Every tree element: class="tree" + inline style="animation-delay: Xs"
            - Every grass element: class="grass" + unique delay
            - Every water band: class="shimmer" + unique delay
            - Every cloud: class="cloud" + unique delay and duration via inline style
            - Particles/petals/embers: class="float" + unique delay
            - Stars/fireflies: class="flicker" + unique delay
            - Apply transform-origin correctly so trees sway from their base, not center
            - Use 0.5–1.5deg rotation for subtle realism; avoid large rotations that look mechanical

            ═══ VISUAL STYLE — BOB ROSS OIL PAINTING ═══
            This must look like a Bob Ross "Joy of Painting" landscape — loose, expressive,
            warm oil-painting aesthetic rendered in SVG. Specific techniques to emulate:

            SKIES: Dreamy gradient skies with soft blended cloud masses built from many
              overlapping ellipses at varying opacity (0.1–0.4). Never a flat solid sky.
              Warm peachy/golden near horizon, deeper blues/purples high up.

            MOUNTAINS & HILLS: Soft silhouetted ridgelines using irregular paths. Layer 2–3
              mountain ranges with progressively lighter values (atmospheric perspective).
              Snow caps on peaks where appropriate — soft white with blue shadows.

            TREES: Bob Ross "happy little trees" — dark evergreen shapes built from stacked
              triangular or teardrop paths, slightly irregular. Cluster in groups of 3–7.
              Add highlight strokes (lighter green) on one side for a lit edge.

            WATER & REFLECTIONS: Lakes, rivers, or streams with horizontal gradient bands
              reflecting the sky colors. Add subtle shimmer highlight strokes.

            LIGHT: One warm light source (sun or moon). God-rays/crepuscular rays as thin
              radial gradient shapes fanning out from the light source. Golden hour or
              magic-hour color temperature preferred.

            TEXTURE: Simulate paint texture with many overlapping semi-transparent shapes,
              slight randomness in paths. Never flat fills — always gradients or texture layers.

            Avoid: hard geometric edges, icons, clipart, flat fills, pure white, black outlines.
            Every element should feel soft, organic, and hand-painted.

            ═══ TECHNICAL ═══
            - viewBox="0 0 900 600", width="100%", height="100%"
            - Use SVG <filter> for at least one glow effect:
              <filter id="glow"><feGaussianBlur stdDeviation="4" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
            - Rich palette: minimum 5 gradient stops across multiple gradients
            - Holiday name rendered as large, decorative SVG <text> with a custom font-family,
              letter-spacing, and the shimmer animation applied
            - Output ONLY valid SVG starting with <svg — no markdown, no explanation
            - Before </svg> embed EXACTLY:
              <!-- ARTMETA: {"primaryColor":"#XXXXXX","secondaryColor":"#XXXXXX","accentColor":"#XXXXXX","theme":"2-4 word theme"} -->

            Output only the SVG XML.
            """;

        var messages = new List<Message> { new(RoleType.User, prompt) };
        var parameters = new MessageParameters
        {
            Messages = messages,
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 12000,
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
