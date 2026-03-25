namespace HomelabCountdown.Models;

public class HolidayInfo
{
    public string Name { get; set; } = "";
    public string LocalName { get; set; } = "";
    public string CountryCode { get; set; } = "";
    public string CountryName { get; set; } = "";
    public DateOnly Date { get; set; }
}
