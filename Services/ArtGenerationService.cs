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
            You are a world-class SVG artist. Create a breathtaking, full-viewport SVG artwork celebrating:

            Holiday: {{holiday.Name}}
            Local Name: {{holiday.LocalName}}
            Country: {{holiday.CountryName}}
            Date: {{holiday.Date:MMMM d, yyyy}}
            {{critiqueSection}}

            STRICT REQUIREMENTS:
            1. Output ONLY valid SVG — starting with <svg and ending with </svg>. No markdown, no explanation.
            2. Use viewBox="0 0 900 600" with width="100%" height="100%"
            3. Use rich gradients, patterns, and symbolic shapes tied to this holiday and culture
            4. Include the holiday name as beautiful, styled text within the SVG
            5. Use at least 3 harmonious colors in gradients/fills
            6. Make it visually striking enough to be a website full-screen background
            7. The artwork should feel culturally authentic and celebratory
            8. Embed this comment EXACTLY (with real hex values) before the closing </svg> tag:
               <!-- ARTMETA: {"primaryColor":"#XXXXXX","secondaryColor":"#XXXXXX","accentColor":"#XXXXXX","theme":"short theme description"} -->

            Output only the SVG XML.
            """;

        var messages = new List<Message> { new(RoleType.User, prompt) };
        var parameters = new MessageParameters
        {
            Messages = messages,
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 4096,
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
