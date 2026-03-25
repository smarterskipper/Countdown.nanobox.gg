using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

public class WeatherService
{
    private static readonly DateOnly Target = new(2026, 8, 5);

    private readonly AnthropicClient _claude;
    private readonly ArtCacheService _cache;
    private readonly ILogger<WeatherService> _logger;

    public WeatherService(AnthropicClient claude, ArtCacheService cache, ILogger<WeatherService> logger)
    {
        _claude = claude;
        _cache = cache;
        _logger = logger;
    }

    public WeatherEffect? GetWeatherForDate(DateOnly date) => _cache.GetWeatherForDate(date);

    public async Task<WeatherEffect> GenerateAndCacheAsync(DateOnly date)
    {
        var existing = _cache.GetWeatherForDate(date);
        if (existing is not null) return existing;

        var daysRemaining = Math.Max(0, Target.DayNumber - date.DayNumber);

        _logger.LogInformation("Generating weather effect for {Date} ({Days} days remaining)", date, daysRemaining);

        var prompt = $"""
            Today there are {daysRemaining} days remaining until August 5th, 2026.

            Find a creative, specific, real-world connection between the number {daysRemaining} and a weather or nature phenomenon.

            Think laterally — the connection can be:
            - A city/region with {daysRemaining}mm of rainfall, {daysRemaining}°C temperature, or {daysRemaining}% humidity
            - A place with {daysRemaining} days of sunshine or rain per year
            - A wind speed of {daysRemaining} km/h or knots producing a specific effect
            - A natural fact ({daysRemaining} species migrating, {daysRemaining} meters elevation creates alpine conditions)
            - A seasonal phenomenon measured in {daysRemaining} units

            Be specific, accurate, and poetic. Use real locations.
            Weather types available: rain, snow, sun, wind, fog, storm, heat, aurora, cloud

            Respond ONLY with valid JSON on a single line, no other text.
            Fields: type, location, connection, description, intensity (0.0-1.0), color (hex)
            """;

        try
        {
            var response = await _claude.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Messages = [new Message(RoleType.User, prompt)],
                Model = AnthropicModels.Claude46Sonnet,
                MaxTokens = 512,
                Stream = false,
                Temperature = 0.8m
            });

            var json = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
            // strip code fences if present
            json = json.Trim();
            if (json.StartsWith("```")) json = string.Join('\n', json.Split('\n').Skip(1).SkipLast(1));

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var effect = new WeatherEffect
            {
                Date = date,
                DaysRemaining = daysRemaining,
                Type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "cloud" : "cloud",
                Location = root.TryGetProperty("location", out var l) ? l.GetString() ?? "" : "",
                Connection = root.TryGetProperty("connection", out var c) ? c.GetString() ?? "" : "",
                Description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                Intensity = root.TryGetProperty("intensity", out var i) ? i.GetDouble() : 0.6,
                Color = root.TryGetProperty("color", out var col) ? col.GetString() ?? "#6699cc" : "#6699cc",
                GeneratedAt = DateTime.UtcNow
            };

            await _cache.SaveWeatherAsync(effect);
            _logger.LogInformation("Weather effect: {Type} from {Location} — {Connection}", effect.Type, effect.Location, effect.Connection);
            return effect;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Weather generation failed for {Date}", date);
            return new WeatherEffect
            {
                Date = date,
                DaysRemaining = daysRemaining,
                Type = "cloud",
                Location = "Somewhere peaceful",
                Connection = $"{daysRemaining} days to go",
                Description = $"With {daysRemaining} days remaining, the sky is calm and waiting.",
                Intensity = 0.4,
                Color = "#8899aa",
                GeneratedAt = DateTime.UtcNow
            };
        }
    }
}
