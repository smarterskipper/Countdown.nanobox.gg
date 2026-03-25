using Anthropic.SDK;
using HomelabCountdown.Components;
using HomelabCountdown.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── MudBlazor ────────────────────────────────────────────────────────────────
builder.Services.AddMudServices();

// ── Razor / Blazor ───────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── HttpClient (used by DiscordNotificationService) ───────────────────────────
builder.Services.AddHttpClient();

// ── Anthropic client ─────────────────────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var apiKey = builder.Configuration["Anthropic:ApiKey"] is { Length: > 0 } cfgKey
                    ? cfgKey
                    : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException(
                            "Anthropic API key not configured. " +
                            "Set Anthropic:ApiKey in appsettings or ANTHROPIC_API_KEY env var.");
    return new AnthropicClient(new APIAuthentication(apiKey));
});

// ── App services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ArtCacheService>();
builder.Services.AddSingleton<PlaywrightScreenshotService>();
builder.Services.AddSingleton<DiscordNotificationService>();
builder.Services.AddScoped<HolidayService>();
builder.Services.AddScoped<ArtGenerationService>();
builder.Services.AddHostedService<DailyArtHostedService>();

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();

// ── Persistent art-cache static files ────────────────────────────────────────
var artCachePath = app.Configuration["ArtCache:Path"] is { Length: > 0 } p
    ? p
    : Path.Combine(AppContext.BaseDirectory, "art-cache");
Directory.CreateDirectory(artCachePath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(artCachePath),
    RequestPath = "/art-cache"
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
