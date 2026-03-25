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

            if (cache.GetArtForDate(date) is not null)
            {
                _logger.LogInformation("Art already exists for {Date}, skipping generation", date);
                return;
            }

            var holidayService = scope.ServiceProvider.GetRequiredService<HolidayService>();
            var artGen = scope.ServiceProvider.GetRequiredService<ArtGenerationService>();

            var holiday = holidayService.GetHolidayForDate(date)
                       ?? holidayService.GetFallbackHoliday(date);

            _logger.LogInformation("Generating art for {Date}: {Holiday} in {Country}",
                date, holiday.Name, holiday.CountryName);

            await artGen.GenerateAndCacheAsync(date, holiday);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Art generation failed for {Date}", date);
        }
    }
}
