using Microsoft.Playwright;

namespace HomelabCountdown.Services;

public class PlaywrightScreenshotService : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly ILogger<PlaywrightScreenshotService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public PlaywrightScreenshotService(ILogger<PlaywrightScreenshotService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> ScreenshotSvgAsync(string svgContent, int width = 900, int height = 600)
    {
        await EnsureInitializedAsync();

        var html = WrapSvgInHtml(svgContent, width, height);
        var tmpFile = Path.Combine(Path.GetTempPath(), $"art-preview-{Guid.NewGuid()}.html");

        try
        {
            await File.WriteAllTextAsync(tmpFile, html);

            var page = await _browser!.NewPageAsync();
            try
            {
                await page.SetViewportSizeAsync(width, height);
                await page.GotoAsync($"file:///{tmpFile.Replace('\\', '/')}");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    FullPage = false
                });
                return bytes;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            if (File.Exists(tmpFile))
                File.Delete(tmpFile);
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_initialized) return;

            // Install browsers if not present
            try
            {
                Microsoft.Playwright.Program.Main(["install", "chromium", "--with-deps"]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "playwright install returned non-zero (may be OK if already installed)");
            }

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-setuid-sandbox"]
            });
            _initialized = true;
            _logger.LogInformation("Playwright Chromium browser launched");
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string WrapSvgInHtml(string svgContent, int width, int height) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8"/>
          <style>
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body { width: {{width}}px; height: {{height}}px; overflow: hidden; background: #000; }
            svg { width: 100%; height: 100%; display: block; }
          </style>
        </head>
        <body>
        {{svgContent}}
        </body>
        </html>
        """;

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
