namespace HomelabCountdown.Models;

public class ViewerEntry
{
    public string Key { get; set; } = "";          // "US-CA" or "CN"
    public string CountryCode { get; set; } = "";  // "US", "CN", "GB"
    public string CountryName { get; set; } = "";  // "United States", "China"
    public string? StateCode { get; set; }          // "CA" (US only)
    public string? StateName { get; set; }          // "California" (US only)
    public int Count { get; set; }
}
