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
    private readonly ReplicateImageService _replicate;
    private readonly ArtCacheService _cache;
    private readonly GenerationStatusService _status;
    private readonly ILogger<ArtGenerationService> _logger;

    public ArtGenerationService(
        AnthropicClient claude,
        ReplicateImageService replicate,
        ArtCacheService cache,
        GenerationStatusService status,
        ILogger<ArtGenerationService> logger)
    {
        _claude = claude;
        _replicate = replicate;
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

        byte[]? image = null;
        ArtScore? score = null;
        ArtScore? bestScore = null;
        byte[]? bestImage = null;
        string critique = "";
        int attempt = 0;

        while (attempt < MaxAttempts)
        {
            attempt++;
            _logger.LogInformation("Art generation attempt {Attempt}/{Max}", attempt, MaxAttempts);
            _status.Update($"Building prompt… (attempt {attempt} of {MaxAttempts})", attempt, MaxAttempts, bestScore?.Score);

            // Step 1: Claude writes a rich image prompt
            var imagePrompt = await BuildImagePromptAsync(holiday, weather, critique, attempt);

            // Step 2: Replicate Flux generates the image
            _status.Update($"Painting with Flux… (attempt {attempt})", attempt, MaxAttempts, bestScore?.Score);
            image = await _replicate.GenerateImageAsync(imagePrompt);

            // Step 3: Claude scores the result
            _status.Update($"Scoring… (attempt {attempt})", attempt, MaxAttempts, bestScore?.Score);
            score = await ScoreImageAsync(image, holiday);

            _logger.LogInformation("Attempt {Attempt} score: {Score}/10 — {Critique}", attempt, score.Score, score.Critique);

            if (bestScore is null || score.Score > bestScore.Score)
            {
                bestScore = score;
                bestImage = image;
            }

            critique = score.Critique;

            if (score.Score >= PassScore)
            {
                _logger.LogInformation("Art passed on attempt {Attempt} with score {Score}", attempt, score.Score);
                break;
            }

            if (attempt >= MaxAttempts)
                _logger.LogInformation("Max attempts reached, using best result (score {Score})", bestScore!.Score);
        }

        // Use best result if final attempt wasn't the best
        if (bestScore is not null && (score is null || bestScore.Score > score.Score))
        {
            image = bestImage;
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
        await _cache.SaveArtAsync(art, image!);
        _status.Clear();
        return art;
    }

    // ── Prompt builder ────────────────────────────────────────────────────────

    private async Task<string> BuildImagePromptAsync(
        HolidayInfo holiday, WeatherEffect? weather, string previousCritique, int attempt)
    {
        var weatherDesc = weather is not null
            ? $"Current weather in Lehi, Utah: {weather.Description} (intensity {weather.Intensity:F2})"
            : "";

        var critiqueNote = attempt > 1 && !string.IsNullOrEmpty(previousCritique)
            ? $"Previous attempt critique (fix these issues): {previousCritique}"
            : "";

        var metaPrompt = $"""
            You are an expert AI art director writing a detailed image generation prompt for Flux 1.1 Pro.
            Your prompt must produce a breathtaking, museum-quality oil painting in the style of Bob Ross
            that is OVERFLOWING with animals — both real and fantastical. Animals are the STAR of this painting.

            Painting subject:
            Holiday: {holiday.Name}
            Local name: {holiday.LocalName}
            Country: {holiday.CountryName}
            Date: {holiday.Date:MMMM d, yyyy}
            {weatherDesc}
            {critiqueNote}

            Write a single dense image generation prompt (300–400 words) structured in this exact order:

            1. STYLE: "Bob Ross 'Joy of Painting' oil painting style, thick impasto brushstrokes,
               painterly, rich textured canvas, warm and atmospheric, PBS television aesthetic"

            2. SCENE COMPOSITION (describe all of these):
               - Sky: specific cloud formations, lighting conditions, time of day, sun/moon position
               - Background: 2-3 mountain ridgelines receding into atmospheric haze
               - Midground: dense evergreen forest with Bob Ross happy little trees
               - Water: if applicable (lake, river, ocean) with reflections
               - Foreground: rich ground detail — grass, wildflowers, rocks, fallen logs
               - Cultural focal element: ONE iconic visual symbol from {holiday.CountryName}
                 specific to {holiday.Name} (be specific and culturally accurate)

            3. WILDLIFE (REQUIRED — woven naturally into the scene):
               Include 3–5 real animals that belong naturally in this landscape. They should feel like
               you spotted them on a nature walk — subtle and believable, not cartoon or posed.
               Examples: a deer grazing at the forest edge, a hawk circling overhead, a fox slipping
               between trees, an owl perched half-hidden in a branch, fish visible under clear water,
               a rabbit at the meadow's edge. Place each one specifically in the scene and describe
               what it is doing. The animals should feel DISCOVERED, not announced.

               Hidden in the scene — nearly invisible unless you look closely — is ONE mythical creature
               subtly camouflaged into a natural element: perhaps a dragon's silhouette mistaken for
               a storm cloud, a unicorn half-hidden in mist at the forest edge, a sea serpent whose
               back ripples look like waves, or a phoenix whose tail feathers blend into sunset colors.
               Do NOT call it out — just place it naturally so a careful viewer might notice it.

            4. WEATHER INTEGRATION: {(weather is not null ? $"Current weather: {weather.Description} — show this visually in the scene." : "Describe natural seasonal atmosphere.")}

            5. COLOR PALETTE: Specify actual Bob Ross colors by name — Phthalo Blue, Titanium White,
               Sap Green, Van Dyke Brown, Cadmium Yellow, Indian Yellow, Alizarin Crimson, etc.

            6. LIGHTING: Describe the specific light quality — golden hour, overcast diffused,
               moonlit, stormy dramatic, etc.

            7. QUALITY TAGS at the end: "masterpiece, highly detailed, photorealistic painting,
               8K resolution, professional artwork, no text, no watermarks, no signatures,
               award winning landscape painting"

            Output ONLY the image prompt text — no explanation, no title, no quotes around it.
            """;

        var response = await _claude.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Messages = [new Message(RoleType.User, metaPrompt)],
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 1536,
            Stream = false,
            Temperature = 0.9m
        });

        var imagePrompt = response.Content.OfType<TextContent>().FirstOrDefault()?.Text?.Trim() ?? holiday.Name;

        _logger.LogInformation("=== GENERATED FLUX PROMPT (attempt {Attempt}) ===\n{Prompt}\n=== END PROMPT ===",
            attempt, imagePrompt);

        return imagePrompt;
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private async Task<ArtScore> ScoreImageAsync(byte[] imagePng, HolidayInfo holiday)
    {
        var base64 = Convert.ToBase64String(imagePng);

        var prompt = $$"""
            You are an art critic evaluating an AI-generated painting for this holiday:
            Holiday: {{holiday.Name}} ({{holiday.CountryName}})

            Score the image 1–10 based on:
            - Visual beauty and painterly quality (3 pts)
            - Cultural relevance and symbolism for this holiday (3 pts)
            - Composition and depth (2 pts)
            - Color harmony and atmosphere (2 pts)

            Also extract the 3 dominant colors and a 2–4 word theme.

            Respond ONLY with valid JSON on a single line:
            {"score":8.5,"critique":"specific notes or empty if perfect","primaryColor":"#rrggbb","secondaryColor":"#rrggbb","accentColor":"#rrggbb","theme":"2-4 word theme"}
            """;

        var messages = new List<Message>
        {
            new()
            {
                Role = RoleType.User,
                Content = new List<ContentBase>
                {
                    new ImageContent { Source = new ImageSource { MediaType = "image/png", Data = base64 } },
                    new TextContent { Text = prompt }
                }
            }
        };

        var response = await _claude.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Messages = messages,
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 512,
            Stream = false,
            Temperature = 0m
        });

        var json = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
        _logger.LogInformation("=== SCORE RESPONSE ===\n{Json}\n=== END SCORE ===", json);
        return ParseScore(json);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArtScore ParseScore(string json)
    {
        try
        {
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
