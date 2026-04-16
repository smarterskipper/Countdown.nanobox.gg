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
                // Safety: wait another minute to ensure the date has rolled over
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
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

            // Generate art
            var existingArt = cache.GetArtForDate(date);
            if (existingArt is null)
            {
                // 1. Pick a random Utah place for this date
                var placeService = scope.ServiceProvider.GetRequiredService<UtahPlaceService>();
                var place = placeService.GetPlaceForDate(date);

                // 2. Fetch weather at that place's coordinates
                var weatherService = scope.ServiceProvider.GetRequiredService<WeatherService>();
                var weatherEffect = cache.GetWeatherForDate(date)
                    ?? await weatherService.GenerateAndCacheAsync(date, place.Latitude, place.Longitude, place.Name);

                _logger.LogInformation("Generating art for {Date}: {Place}",
                    date, place.Name);

                // 3. Generate art
                var artGen = scope.ServiceProvider.GetRequiredService<ArtGenerationService>();
                var art = await artGen.GenerateAndCacheAsync(date, place, weatherEffect);
                var discord = scope.ServiceProvider.GetRequiredService<DiscordNotificationService>();
                await discord.NotifyArtGeneratedAsync(art);
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
