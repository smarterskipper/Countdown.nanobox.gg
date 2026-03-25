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
            screenshot = await _playwright.ScreenshotSvgAsync(svg);
            score = await ScoreScreenshotAsync(screenshot, holiday);

            _logger.LogInformation("Attempt {Attempt} score: {Score}/10 — {Critique}",
                attempt, score.Score, score.Critique);

            critique = score.Critique;

            if (score.Score >= PassScore)
            {
                _logger.LogInformation("Art passed score threshold on attempt {Attempt}", attempt);
                break;
            }

            if (attempt >= MaxAttempts)
            {
                _logger.LogInformation("Reached max attempts, keeping best result (score {Score})", score.Score);
            }
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

            ═══ ANIMATION (mandatory — this is a living background) ═══
            Include a <style> block with MINIMUM 6 distinct CSS @keyframes:
              1. Sky/atmosphere slow shift — gradient color change or slow pan (20–40s)
              2. Particle drift — staggered float+sway on the 10–20 small elements (4–10s, vary delay)
              3. Focal element pulse — scale + glow breathing on the main cultural symbol (3–6s)
              4. Secondary element movement — rotation, orbit, or wave on decorative shapes (6–15s)
              5. Ambient light sweep — subtle brightness ripple or shadow sweep across scene (10–25s)
              6. Text shimmer — color or opacity animation on the holiday name text (4–8s)
            Use animation-iteration-count: infinite, ease-in-out. Apply via class names.

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
            MaxTokens = 8000,
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
