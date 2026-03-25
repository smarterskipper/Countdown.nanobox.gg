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

// ── HttpClient for WindowSwap proxy ──────────────────────────────────────────
builder.Services.AddHttpClient("windowswap", c =>
{
    c.BaseAddress = new Uri("https://www.window-swap.com/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    c.Timeout = TimeSpan.FromSeconds(15);
});

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

// ── WindowSwap reverse proxy ──────────────────────────────────────────────────
// Fetches the target URL, strips X-Frame-Options so it embeds in our iframe.
app.MapGet("/api/windowswap", async (HttpContext ctx, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("windowswap");
    try
    {
        var upstream = await client.GetAsync("/");
        var body = await upstream.Content.ReadAsStringAsync();
        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "text/html";

        ctx.Response.Headers.Remove("X-Frame-Options");
        ctx.Response.Headers.Remove("Content-Security-Policy");
        ctx.Response.ContentType = contentType;
        await ctx.Response.WriteAsync(body);
    }
    catch
    {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync("WindowSwap unavailable");
    }
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
