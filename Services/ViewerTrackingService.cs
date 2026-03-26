using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

/// <summary>
/// Singleton that orchestrates geo-lookup, DB writes, and in-memory caching
/// of today's viewer counts. Fires OnViewersUpdated so the sidebar can refresh.
/// </summary>
public class ViewerTrackingService
{
    public event Action? OnViewersUpdated;

    private readonly GeoLocationService _geo;
    private readonly ViewerDbService _db;
    private readonly ILogger<ViewerTrackingService> _logger;

    private List<ViewerEntry> _cache = [];
    private int _totalCache;
    private DateOnly _cacheDate;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ViewerTrackingService(
        GeoLocationService geo,
        ViewerDbService db,
        ILogger<ViewerTrackingService> logger)
    {
        _geo = geo;
        _db = db;
        _logger = logger;
    }

    /// <summary>Called from the tracking middleware on every real page load.</summary>
    public async Task RecordVisitAsync(string ip)
    {
        var geo = await _geo.LookupAsync(ip);
        if (geo is null) return; // private IP or lookup failed

        var date = DateOnly.FromDateTime(DateTime.Today);
        var key = geo.CountryCode == "US" && geo.StateCode is not null
            ? $"US-{geo.StateCode}"
            : geo.CountryCode;

        var entry = new ViewerEntry
        {
            Key         = key,
            CountryCode = geo.CountryCode,
            CountryName = geo.CountryName,
            StateCode   = geo.StateCode,
            StateName   = geo.StateName,
        };

        try
        {
            await _db.UpsertVisitAsync(date, entry);
            await RefreshCacheAsync(date);
            OnViewersUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record visit from {IP}", ip);
        }
    }

    /// <summary>Returns the in-memory cache (fast, no DB hit).</summary>
    public (List<ViewerEntry> Viewers, int Total) GetCached()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        // If cache is stale (day rolled over), return empty — next RecordVisit will refresh
        return _cacheDate == today ? (_cache, _totalCache) : ([], 0);
    }

    /// <summary>Loads fresh data from DB — call on first render of the sidebar.</summary>
    public async Task<(List<ViewerEntry> Viewers, int Total)> LoadTodayAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await RefreshCacheAsync(today);
        return (_cache, _totalCache);
    }

    private async Task RefreshCacheAsync(DateOnly date)
    {
        await _lock.WaitAsync();
        try
        {
            _cache = await _db.GetViewersForDateAsync(date);
            _totalCache = _cache.Sum(v => v.Count);
            _cacheDate = date;
        }
        finally
        {
            _lock.Release();
        }
    }
}
