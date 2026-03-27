using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

public class DailyArtHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DailyArtHostedService> _logger;

    public DailyArtHostedService(IServiceProvider services, ILogger<DailyArtHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Generate today's art on startup (non-blocking — errors are logged, not thrown)
        _ = Task.Run(() => TryGenerateForDateAsync(DateOnly.FromDateTime(DateTime.Today), stoppingToken), stoppingToken);

        // Then loop: sleep until next midnight and generate next day's art
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextMidnight = DateTime.Today.AddDays(1);
            var delay = nextMidnight - DateTime.Now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

            _logger.LogInformation("Next art generation scheduled at {Time}", nextMidnight);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TryGenerateForDateAsync(DateOnly.FromDateTime(DateTime.Today), stoppingToken);
        }
    }

    private async Task TryGenerateForDateAsync(DateOnly date, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ArtCacheService>();
            var holidayService = scope.ServiceProvider.GetRequiredService<HolidayService>();

            // Generate weather first so it can inform the SVG painting
            var weatherService = scope.ServiceProvider.GetRequiredService<WeatherService>();
            var weatherEffect = cache.GetWeatherForDate(date)
                ?? await weatherService.GenerateAndCacheAsync(date);

            // Generate art
            var existingArt = cache.GetArtForDate(date);
            if (existingArt is null)
            {
                var artGen = scope.ServiceProvider.GetRequiredService<ArtGenerationService>();

                // 1. Try timeanddate.com (real observances, cultural days, etc.)
                var tadService = scope.ServiceProvider.GetRequiredService<TimeAndDateHolidayService>();
                var tadHolidays = await tadService.GetHolidaysAsync(date);
                var holiday = tadService.PickBest(tadHolidays, date)
                    // 2. Fall back to Nager.Date official public holidays
                    ?? holidayService.GetHolidayForDate(date)
                    // 3. Last resort: seasonal/astronomical context
                    ?? holidayService.GetFallbackHoliday(date);

                _logger.LogInformation("Generating art for {Date}: {Holiday} in {Country}",
                    date, holiday.Name, holiday.CountryName);

                var art = await artGen.GenerateAndCacheAsync(date, holiday, weatherEffect);
                var discord = scope.ServiceProvider.GetRequiredService<DiscordNotificationService>();
                await discord.NotifyArtGeneratedAsync(art);
            }
            else if (!existingArt.HasVideo)
            {
                _logger.LogInformation("Art exists for {Date} but has no video — animating", date);
                var artGen = scope.ServiceProvider.GetRequiredService<ArtGenerationService>();
                await artGen.AnimateOnlyAsync(existingArt);
            }
            else
            {
                _logger.LogInformation("Art already exists for {Date}, skipping generation", date);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daily generation failed for {Date}", date);
        }
    }
}
