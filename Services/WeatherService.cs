using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

public class WeatherService
{
    private static readonly DateOnly Target = new(2026, 8, 5);

    // Lehi, UT coordinates
    private const double LeahiLat  = 40.3916;
    private const double LeahiLon  = -111.8507;

    private readonly AnthropicClient _claude;
    private readonly ArtCacheService _cache;
    private readonly HttpClient _http;
    private readonly ILogger<WeatherService> _logger;

    public WeatherService(AnthropicClient claude, ArtCacheService cache, IHttpClientFactory httpFactory, ILogger<WeatherService> logger)
    {
        _claude = claude;
        _cache  = cache;
        _http   = httpFactory.CreateClient();
        _logger = logger;
    }

    public WeatherEffect? GetWeatherForDate(DateOnly date) => _cache.GetWeatherForDate(date);

    public async Task<WeatherEffect> GenerateAndCacheAsync(DateOnly date)
    {
        var existing = _cache.GetWeatherForDate(date);
        if (existing is not null) return existing;

        var daysRemaining = Math.Max(0, Target.DayNumber - date.DayNumber);

        // For today, use real weather from Open-Meteo (free, no key required)
        if (date == DateOnly.FromDateTime(DateTime.Today))
        {
            var real = await FetchRealWeatherAsync(date, daysRemaining);
            if (real is not null)
            {
                await _cache.SaveWeatherAsync(real);
                _logger.LogInformation("Real weather for {Date}: {Type} in Lehi, UT", date, real.Type);
                return real;
            }
        }

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

    // ── Real weather from Open-Meteo (Lehi, UT) ────────────────────────────

    private async Task<WeatherEffect?> FetchRealWeatherAsync(DateOnly date, int daysRemaining)
    {
        try
        {
            var url = $"https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={LeahiLat}&longitude={LeahiLon}" +
                      $"&current=weather_code,temperature_2m,wind_speed_10m" +
                      $"&timezone=America%2FDenver&forecast_days=1";

            var json = await _http.GetStringAsync(url);
            var doc  = JsonDocument.Parse(json);
            var cur  = doc.RootElement.GetProperty("current");

            var code  = cur.GetProperty("weather_code").GetInt32();
            var tempC = cur.GetProperty("temperature_2m").GetDouble();
            var wind  = cur.GetProperty("wind_speed_10m").GetDouble();
            var tempF = tempC * 9.0 / 5.0 + 32.0;

            var (type, color) = MapCode(code, tempC, wind);
            var desc = $"{WeatherLabel(code, tempC, wind)} in Lehi, UT · {tempF:F0}°F ({tempC:F0}°C)";

            return new WeatherEffect
            {
                Date          = date,
                DaysRemaining = daysRemaining,
                Type          = type,
                Location      = "Lehi, UT",
                Connection    = desc,
                Description   = desc,
                Intensity     = 0.65,
                Color         = color,
                GeneratedAt   = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open-Meteo fetch failed, falling back to AI weather");
            return null;
        }
    }

    private static (string type, string color) MapCode(int code, double tempC, double wind)
    {
        if (tempC >= 35) return ("heat",  "#f97316");
        return code switch
        {
            0            => ("sun",   "#fbbf24"),
            1 or 2       => ("sun",   "#fcd34d"),
            3            => ("cloud", "#94a3b8"),
            45 or 48     => ("fog",   "#9ca3af"),
            51 or 53 or 55 or 61 or 63 or 80 or 81 => ("rain", "#60a5fa"),
            65 or 82     => ("rain",  "#2563eb"),
            71 or 73 or 85 => ("snow", "#e2e8f0"),
            75 or 77 or 86 => ("snow", "#f1f5f9"),
            95 or 96 or 99 => ("storm","#7c3aed"),
            _ when wind >= 40 => ("wind", "#64748b"),
            _            => ("cloud", "#94a3b8")
        };
    }

    private static string WeatherLabel(int code, double tempC, double wind) => code switch
    {
        0            => "Clear skies",
        1 or 2       => "Mostly clear",
        3            => "Overcast",
        45 or 48     => "Foggy",
        51 or 53     => "Light drizzle",
        55           => "Drizzle",
        61 or 63     => "Rain",
        65           => "Heavy rain",
        71 or 73     => "Light snow",
        75 or 77     => "Heavy snow",
        80 or 81     => "Rain showers",
        82           => "Violent showers",
        85 or 86     => "Snow showers",
        95           => "Thunderstorm",
        96 or 99     => "Severe thunderstorm",
        _ when tempC >= 35 => "Scorching heat",
        _ when wind >= 40  => $"Windy ({wind:F0} km/h)",
        _            => "Partly cloudy"
    };
}
