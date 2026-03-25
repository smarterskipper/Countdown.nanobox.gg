namespace HomelabCountdown.Models;

public class WeatherEffect
{
    public DateOnly Date { get; set; }
    public int DaysRemaining { get; set; }
    public string Type { get; set; } = "clear";       // rain, snow, sun, wind, fog, storm, heat, aurora
    public string Location { get; set; } = "";
    public string Connection { get; set; } = "";
    public string Description { get; set; } = "";
    public double Intensity { get; set; } = 0.5;
    public string Color { get; set; } = "#6699cc";
    public DateTime GeneratedAt { get; set; }
}
