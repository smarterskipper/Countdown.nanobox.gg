using System.Text.RegularExpressions;
using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

/// <summary>
/// Fetches the full year's US holiday/observance list from timeanddate.com once
/// and caches it in memory. Falls back gracefully if the site is unreachable.
/// </summary>
public partial class TimeAndDateHolidayService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TimeAndDateHolidayService> _logger;

    // Cached per year so we only hit the site once per year (or once per restart)
    private Dictionary<DateOnly, List<string>> _cache = [];
    private int _cachedYear = 0;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TimeAndDateHolidayService(IHttpClientFactory httpFactory, ILogger<TimeAndDateHolidayService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Returns holiday names for the date, or empty list on failure.</summary>
    public async Task<List<string>> GetHolidaysAsync(DateOnly date)
    {
        await EnsureYearLoadedAsync(date.Year);
        return _cache.TryGetValue(date, out var list) ? list : [];
    }

    /// <summary>Picks the best holiday from the list for art generation.</summary>
    public HolidayInfo? PickBest(List<string> holidays, DateOnly date)
    {
        if (holidays.Count == 0) return null;

        // Prefer culturally rich / visually interesting holidays; skip generic observances
        var ranked = holidays
            .OrderByDescending(h => Score(h))
            .ToList();

        var chosen = ranked[0];
        _logger.LogInformation("Picked holiday '{Name}' from {Count} options for {Date}",
            chosen, holidays.Count, date);

        return new HolidayInfo
        {
            Name = chosen,
            LocalName = chosen,
            CountryCode = "US",
            CountryName = "United States",
            Date = date
        };
    }

    private static int Score(string name)
    {
        var lower = name.ToLowerInvariant();
        // Religious / cultural celebrations
        if (lower.Contains("easter") || lower.Contains("christmas") ||
            lower.Contains("hanukkah") || lower.Contains("diwali") ||
            lower.Contains("eid") || lower.Contains("holi") ||
            lower.Contains("passover") || lower.Contains("ramadan")) return 5;
        // Independence / national days
        if (lower.Contains("independence") || lower.Contains("liberation") ||
            lower.Contains("national day")) return 4;
        // Seasonal / astronomical
        if (lower.Contains("solstice") || lower.Contains("equinox") ||
            lower.Contains("moon") || lower.Contains("eclipse")) return 4;
        // Nature / environment
        if (lower.Contains("earth") || lower.Contains("ocean") ||
            lower.Contains("arbor") || lower.Contains("wildlife")) return 3;
        // Patriotic / historical
        if (lower.Contains("memorial") || lower.Contains("veterans") ||
            lower.Contains("mlk") || lower.Contains("martin luther") ||
            lower.Contains("cesar chavez") || lower.Contains("pulaski")) return 3;
        // Cultural celebrations with visual richness
        if (lower.Contains("st. patrick") || lower.Contains("mardi gras") ||
            lower.Contains("cinco de mayo") || lower.Contains("day of the dead")) return 3;
        // Generic awareness/recognition days — not great for art
        if (lower.Contains("awareness") || lower.Contains("appreciation") ||
            lower.Contains("recognition")) return 1;
        return 2; // everything else
    }

    private async Task EnsureYearLoadedAsync(int year)
    {
        if (_cachedYear == year && _cache.Count > 0) return;

        await _lock.WaitAsync();
        try
        {
            if (_cachedYear == year && _cache.Count > 0) return;

            _logger.LogInformation("Fetching {Year} holiday list from timeanddate.com", year);
            var data = await FetchYearAsync(year);
            if (data.Count > 0)
            {
                _cache = data;
                _cachedYear = year;
                _logger.LogInformation("Loaded {Count} holiday entries for {Year}", data.Count, year);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load timeanddate.com holidays for {Year}", year);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<DateOnly, List<string>>> FetchYearAsync(int year)
    {
        var url = $"https://www.timeanddate.com/holidays/us/{year}";
        using var client = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        request.Headers.Add("Accept", "text/html,application/xhtml+xml");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        return ParseTable(html, year);
    }

    /// <summary>
    /// Parses the holiday table on timeanddate.com/holidays/us/YEAR.
    /// Each row: &lt;td&gt;Mon D&lt;/td&gt; ... &lt;td&gt;&lt;a&gt;Name&lt;/a&gt;&lt;/td&gt; ...
    /// </summary>
    private static Dictionary<DateOnly, List<string>> ParseTable(string html, int year)
    {
        var result = new Dictionary<DateOnly, List<string>>();

        foreach (Match row in TableRowRegex().Matches(html))
        {
            var cells = CellRegex().Matches(row.Value);
            if (cells.Count < 3) continue;

            // First cell: date like "Mar 26"
            var dateText = StripTagsRegex()
                .Replace(cells[0].Groups[1].Value, "").Trim();

            if (!TryParseDate(dateText, year, out var date)) continue;

            // Third cell: holiday name (may be in an <a> tag)
            var nameRaw = StripTagsRegex()
                .Replace(cells[2].Groups[1].Value, "").Trim();
            var name = System.Net.WebUtility.HtmlDecode(nameRaw);

            if (string.IsNullOrWhiteSpace(name) || name.Length > 80) continue;

            if (!result.TryGetValue(date, out var list))
                result[date] = list = [];

            list.Add(name);
        }

        return result;
    }

    private static bool TryParseDate(string text, int year, out DateOnly date)
    {
        // Format: "Mar 26", "Dec 25", etc.
        if (DateOnly.TryParseExact(
                $"{text} {year}",
                "MMM d yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out date))
            return true;

        date = default;
        return false;
    }

    [GeneratedRegex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripTagsRegex();
}
