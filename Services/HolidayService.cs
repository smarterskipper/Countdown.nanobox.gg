using HomelabCountdown.Models;
using Nager.Date;
using Nager.Date.Helpers;

namespace HomelabCountdown.Services;

public class HolidayService
{
    // Curated list of country codes supported by Nager.Date
    private static readonly string[] Countries =
    [
        "JP", "IN", "BR", "MX", "TH", "PH", "ID", "KR",
        "ZA", "NG", "KE", "GH",
        "US", "CA", "GB", "AU", "NZ", "IE",
        "DE", "FR", "IT", "ES", "PT", "NL", "BE", "CH", "AT", "SE",
        "NO", "DK", "FI", "PL", "CZ", "HU", "RO", "GR",
        "AR", "CL", "CO", "PE",
        "RU", "UA", "IL"
    ];

    private static readonly Dictionary<string, string> CountryNames = new()
    {
        ["JP"] = "Japan", ["IN"] = "India", ["BR"] = "Brazil", ["MX"] = "Mexico",
        ["CN"] = "China", ["TH"] = "Thailand", ["PH"] = "Philippines", ["ID"] = "Indonesia",
        ["VN"] = "Vietnam", ["KR"] = "South Korea", ["ZA"] = "South Africa", ["NG"] = "Nigeria",
        ["EG"] = "Egypt", ["KE"] = "Kenya", ["GH"] = "Ghana", ["ET"] = "Ethiopia",
        ["US"] = "United States", ["CA"] = "Canada", ["GB"] = "United Kingdom",
        ["AU"] = "Australia", ["NZ"] = "New Zealand", ["IE"] = "Ireland",
        ["DE"] = "Germany", ["FR"] = "France", ["IT"] = "Italy", ["ES"] = "Spain",
        ["PT"] = "Portugal", ["NL"] = "Netherlands", ["BE"] = "Belgium", ["CH"] = "Switzerland",
        ["AT"] = "Austria", ["SE"] = "Sweden", ["NO"] = "Norway", ["DK"] = "Denmark",
        ["FI"] = "Finland", ["PL"] = "Poland", ["CZ"] = "Czech Republic", ["HU"] = "Hungary",
        ["RO"] = "Romania", ["GR"] = "Greece", ["TR"] = "Turkey", ["AR"] = "Argentina",
        ["CL"] = "Chile", ["CO"] = "Colombia", ["PE"] = "Peru", ["VE"] = "Venezuela",
        ["RU"] = "Russia", ["UA"] = "Ukraine", ["IL"] = "Israel", ["SA"] = "Saudi Arabia",
        ["AE"] = "United Arab Emirates"
    };

    public HolidayInfo? GetHolidayForDate(DateOnly date)
    {
        var found = new List<HolidayInfo>();

        foreach (var code in Countries)
        {
            try
            {
                if (!CountryCodeHelper.TryParseCountryCode(code, out var countryCode))
                    continue;

                var holidays = HolidaySystem.GetHolidays(date.Year, countryCode);
                var match = holidays?.FirstOrDefault(h => DateOnly.FromDateTime(h.Date) == date);
                if (match is not null)
                {
                    found.Add(new HolidayInfo
                    {
                        Name = match.EnglishName,
                        LocalName = match.LocalName,
                        CountryCode = code,
                        CountryName = CountryNames.GetValueOrDefault(code, code),
                        Date = date
                    });
                }
            }
            catch
            {
                // Unsupported country, skip
            }
        }

        if (found.Count == 0)
            return null;

        // Prefer non-US/non-Western holidays for visual diversity;
        // fall back to whatever is available
        var preferred = found.FirstOrDefault(h =>
            h.CountryCode is not ("US" or "CA" or "GB" or "AU"));

        return preferred ?? found[0];
    }

    public HolidayInfo GetFallbackHoliday(DateOnly date)
    {
        // Pick a seasonal/astronomical context so art isn't generic
        var (name, localName, country) = GetSeasonalContext(date);
        return new HolidayInfo
        {
            Name = name,
            LocalName = localName,
            CountryCode = "UN",
            CountryName = country,
            Date = date
        };
    }

    private static (string Name, string LocalName, string Country) GetSeasonalContext(DateOnly date)
    {
        int m = date.Month, d = date.Day;

        // Solstices & equinoxes
        if (m == 3 && d is >= 19 and <= 22) return ("Spring Equinox", "Vernal Equinox", "The Northern Hemisphere");
        if (m == 6 && d is >= 20 and <= 22) return ("Summer Solstice", "Midsummer", "The Northern Hemisphere");
        if (m == 9 && d is >= 22 and <= 24) return ("Autumn Equinox", "Fall Equinox", "The Northern Hemisphere");
        if (m == 12 && d is >= 21 and <= 22) return ("Winter Solstice", "Midwinter", "The Northern Hemisphere");

        // Full moon names (approximate mid-month)
        if (m == 1 && d is >= 13 and <= 17) return ("Wolf Moon", "Full Moon", "The World");
        if (m == 2 && d is >= 12 and <= 16) return ("Snow Moon", "Full Moon", "The World");
        if (m == 3 && d is >= 13 and <= 17) return ("Worm Moon", "Full Moon", "The World");
        if (m == 4 && d is >= 12 and <= 16) return ("Pink Moon", "Full Moon", "The World");
        if (m == 5 && d is >= 12 and <= 16) return ("Flower Moon", "Full Moon", "The World");
        if (m == 6 && d is >= 11 and <= 15) return ("Strawberry Moon", "Full Moon", "The World");
        if (m == 7 && d is >= 10 and <= 14) return ("Buck Moon", "Full Moon", "The World");
        if (m == 8 && d is >= 9 and <= 13)  return ("Sturgeon Moon", "Full Moon", "The World");
        if (m == 9 && d is >= 7 and <= 11)  return ("Harvest Moon", "Full Moon", "The World");
        if (m == 10 && d is >= 7 and <= 11) return ("Hunter's Moon", "Full Moon", "The World");
        if (m == 11 && d is >= 5 and <= 9)  return ("Beaver Moon", "Full Moon", "The World");
        if (m == 12 && d is >= 4 and <= 8)  return ("Cold Moon", "Full Moon", "The World");

        // Seasons (Northern Hemisphere)
        return (m, d) switch
        {
            (12, >= 1) or (1, _) or (2, _) or (3, < 19) =>
                ("Deep Winter", "Winter's Heart", "The Northern Hemisphere"),
            (3, _) or (4, _) or (5, _) or (6, < 20) =>
                ("Bloom of Spring", "Spring", "The Northern Hemisphere"),
            (6, _) or (7, _) or (8, _) or (9, < 22) =>
                ("Height of Summer", "Summer", "The Northern Hemisphere"),
            _ =>
                ("Golden Autumn", "Autumn", "The Northern Hemisphere")
        };
    }
}
