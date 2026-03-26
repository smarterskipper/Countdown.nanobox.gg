using Microsoft.AspNetCore.Components.Forms;

namespace HomelabCountdown.Services;

public class PhotoService
{
    private readonly string _photosDir;
    private readonly ILogger<PhotoService> _logger;

    private static readonly HashSet<string> AllowedExt =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic" };

    public PhotoService(IConfiguration config, ILogger<PhotoService> logger)
    {
        _logger = logger;
        var cacheDir = config["ArtCache:Path"] is { Length: > 0 } p
            ? p : Path.Combine(AppContext.BaseDirectory, "art-cache");
        _photosDir = Path.Combine(cacheDir, "photos");
        Directory.CreateDirectory(_photosDir);
    }

    public async Task<string?> SavePhotoAsync(DateOnly date, IBrowserFile file)
    {
        var ext = Path.GetExtension(file.Name);
        if (!AllowedExt.Contains(ext)) return null;

        var dateDir = Path.Combine(_photosDir, $"{date:yyyy-MM-dd}");
        Directory.CreateDirectory(dateDir);

        var name = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        var path = Path.Combine(dateDir, name);

        try
        {
            await using var fs = File.Create(path);
            await using var stream = file.OpenReadStream(maxAllowedSize: 500 * 1024 * 1024);
            await stream.CopyToAsync(fs);
            return $"/art-cache/photos/{date:yyyy-MM-dd}/{name}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save photo for {Date}", date);
            if (File.Exists(path)) File.Delete(path);
            return null;
        }
    }

    public List<string> GetPhotosForDate(DateOnly date)
    {
        var dateDir = Path.Combine(_photosDir, $"{date:yyyy-MM-dd}");
        if (!Directory.Exists(dateDir)) return [];

        return Directory.GetFiles(dateDir)
            .Where(f => AllowedExt.Contains(Path.GetExtension(f)))
            .OrderBy(File.GetCreationTimeUtc)
            .Select(f => $"/art-cache/photos/{date:yyyy-MM-dd}/{Path.GetFileName(f)}")
            .ToList();
    }

    public Dictionary<DateOnly, int> GetPhotoCountsByDate()
    {
        if (!Directory.Exists(_photosDir)) return new();
        var result = new Dictionary<DateOnly, int>();
        foreach (var dir in Directory.GetDirectories(_photosDir))
        {
            if (DateOnly.TryParse(Path.GetFileName(dir), out var date))
            {
                var count = Directory.GetFiles(dir)
                    .Count(f => AllowedExt.Contains(Path.GetExtension(f)));
                if (count > 0) result[date] = count;
            }
        }
        return result;
    }

    public void DeletePhoto(DateOnly date, string url)
    {
        var name = Path.GetFileName(url);
        // Reject traversal attempts
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\')) return;
        var path = Path.Combine(_photosDir, $"{date:yyyy-MM-dd}", name);
        if (File.Exists(path)) File.Delete(path);
    }
}
