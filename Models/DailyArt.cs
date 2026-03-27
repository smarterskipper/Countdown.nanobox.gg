using System.Text.Json.Serialization;

namespace HomelabCountdown.Models;

public class DailyArt
{
    public DateOnly Date { get; set; }
    public string HolidayName { get; set; } = "";
    public string HolidayLocalName { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string CountryName { get; set; } = "";
    public string SvgFileName { get; set; } = "";       // relative filename inside art-cache/
    public string ScreenshotFileName { get; set; } = ""; // relative filename inside art-cache/
    public string PrimaryColor { get; set; } = "#6366f1";
    public string SecondaryColor { get; set; } = "#8b5cf6";
    public string AccentColor { get; set; } = "#f59e0b";
    public string Theme { get; set; } = "";
    public int AttemptCount { get; set; }
    public double FinalScore { get; set; }
    public string FinalCritique { get; set; } = "";
    public DateTime GeneratedAt { get; set; }

    public string VideoFileName { get; set; } = "";

    [JsonIgnore]
    public string SvgUrl => $"/art-cache/{SvgFileName}";

    [JsonIgnore]
    public string ScreenshotUrl => $"/art-cache/{ScreenshotFileName}?v={GeneratedAt.Ticks}";

    [JsonIgnore]
    public string VideoUrl => $"/art-cache/{VideoFileName}?v={GeneratedAt.Ticks}";

    [JsonIgnore]
    public bool HasVideo => !string.IsNullOrEmpty(VideoFileName);
}
