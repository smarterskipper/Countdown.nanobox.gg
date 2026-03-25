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
        // Returns a generic "Today" holiday so art generation always has context
        return new HolidayInfo
        {
            Name = "A New Day",
            LocalName = "Today",
            CountryCode = "UN",
            CountryName = "The World",
            Date = date
        };
    }
}
