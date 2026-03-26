using System.Text.Json;

namespace HomelabCountdown.Services;

public class GeoLocationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GeoLocationService> _logger;

    // IP → GeoInfo cache (null = private/unknown IP)
    private readonly Dictionary<string, GeoInfo?> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GeoLocationService(IHttpClientFactory httpFactory, ILogger<GeoLocationService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<GeoInfo?> LookupAsync(string ip)
    {
        if (IsPrivate(ip)) return null;

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(ip, out var cached)) return cached;
            var result = await FetchAsync(ip);
            _cache[ip] = result;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<GeoInfo?> FetchAsync(string ip)
    {
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(4);

            // ip-api.com free tier: HTTP only, up to 45 req/min, no key needed
            var url = $"http://ip-api.com/json/{ip}?fields=status,countryCode,country,region,regionName";
            var json = await client.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var s) || s.GetString() != "success")
                return null;

            var cc = root.TryGetProperty("countryCode", out var p1) ? p1.GetString() ?? "" : "";
            var cn = root.TryGetProperty("country", out var p2) ? p2.GetString() ?? "" : "";
            var sc = root.TryGetProperty("region", out var p3) ? p3.GetString() : null;
            var sn = root.TryGetProperty("regionName", out var p4) ? p4.GetString() : null;

            // Only keep state for US
            if (cc != "US") { sc = null; sn = null; }

            return new GeoInfo(cc, cn, sc, sn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geo lookup failed for {IP}", ip);
            return null;
        }
    }

    private static bool IsPrivate(string ip)
    {
        if (ip is "::1" or "127.0.0.1" or "::ffff:127.0.0.1") return true;
        if (ip.StartsWith("10.")) return true;
        if (ip.StartsWith("192.168.")) return true;
        if (ip.StartsWith("172."))
        {
            var parts = ip.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var b) && b is >= 16 and <= 31)
                return true;
        }
        return false;
    }
}

public record GeoInfo(
    string CountryCode,
    string CountryName,
    string? StateCode,
    string? StateName);
