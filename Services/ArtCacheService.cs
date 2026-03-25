using System.Text.Json;
using HomelabCountdown.Models;
using Microsoft.AspNetCore.Hosting;

namespace HomelabCountdown.Services;

public class ArtCacheService
{
    private readonly string _cacheDir;
    private readonly ILogger<ArtCacheService> _logger;

    public event Action<DailyArt>? OnArtGenerated;

    public ArtCacheService(IWebHostEnvironment env, ILogger<ArtCacheService> logger)
    {
        _logger = logger;
        _cacheDir = Path.Combine(env.WebRootPath, "art-cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public DailyArt? GetArtForDate(DateOnly date)
    {
        var metaPath = MetaPath(date);
        if (!File.Exists(metaPath))
            return null;

        try
        {
            var json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<DailyArt>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cached art for {Date}", date);
            return null;
        }
    }

    public async Task SaveArtAsync(DailyArt art, string svgContent, byte[] screenshotPng)
    {
        var svgFile = $"{art.Date:yyyy-MM-dd}.svg";
        var pngFile = $"{art.Date:yyyy-MM-dd}.png";

        art.SvgFileName = svgFile;
        art.ScreenshotFileName = pngFile;
        art.GeneratedAt = DateTime.UtcNow;

        await File.WriteAllTextAsync(Path.Combine(_cacheDir, svgFile), svgContent);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDir, pngFile), screenshotPng);
        await File.WriteAllTextAsync(MetaPath(art.Date),
            JsonSerializer.Serialize(art, new JsonSerializerOptions { WriteIndented = true }));

        _logger.LogInformation("Cached art for {Date}: {Holiday} ({Country}), score {Score}",
            art.Date, art.HolidayName, art.CountryCode, art.FinalScore);

        OnArtGenerated?.Invoke(art);
    }

    public string? ReadSvgContent(DateOnly date)
    {
        var meta = GetArtForDate(date);
        if (meta is null) return null;
        var path = Path.Combine(_cacheDir, meta.SvgFileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public List<DailyArt> GetAllCachedArt()
    {
        var result = new List<DailyArt>();
        foreach (var file in Directory.GetFiles(_cacheDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var art = JsonSerializer.Deserialize<DailyArt>(json);
                if (art is not null)
                    result.Add(art);
            }
            catch { /* skip corrupt files */ }
        }
        return result.OrderByDescending(a => a.Date).ToList();
    }

    private string MetaPath(DateOnly date) =>
        Path.Combine(_cacheDir, $"{date:yyyy-MM-dd}.json");
}
