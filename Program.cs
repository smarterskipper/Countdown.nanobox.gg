using System.Security.Claims;
using Anthropic.SDK;
using HomelabCountdown.Components;
using HomelabCountdown.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── MudBlazor ────────────────────────────────────────────────────────────────
builder.Services.AddMudServices();

// ── Razor / Blazor ───────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = null); // unlimited for large photo uploads

builder.Services.AddCascadingAuthenticationState();

// ── Google SSO ───────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath        = "/auth/login";
        o.AccessDeniedPath = "/auth/denied";
        o.ExpireTimeSpan   = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    })
    .AddGoogle(o =>
    {
        o.ClientId     = builder.Configuration["Google:ClientId"]     ?? throw new InvalidOperationException("Google:ClientId not configured");
        o.ClientSecret = builder.Configuration["Google:ClientSecret"] ?? throw new InvalidOperationException("Google:ClientSecret not configured");
        o.CallbackPath = "/auth/google/callback";

        o.Events.OnCreatingTicket = async ctx =>
        {
            var email = ctx.Principal?.FindFirstValue(ClaimTypes.Email) ?? "";
            var approval = ctx.HttpContext.RequestServices.GetRequiredService<ApprovalService>();

            if (!approval.IsApproved(email))
            {
                // Determine site base URL for the approve/deny links
                var req      = ctx.HttpContext.Request;
                var baseUrl  = $"{req.Scheme}://{req.Host}";
                await approval.RequestApprovalAsync(email, baseUrl);
                ctx.Fail("pending_approval");
            }
        };

        o.Events.OnRemoteFailure = ctx =>
        {
            if (ctx.Failure?.Message == "pending_approval")
            {
                ctx.Response.Redirect("/auth/pending");
                ctx.HandleResponse();
            }
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

// ── HttpClient ────────────────────────────────────────────────────────────────
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
    var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    return new AnthropicClient(new APIAuthentication(apiKey), httpClient);
});

// ── App services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ArtCacheService>();
builder.Services.AddSingleton<PlaywrightScreenshotService>();
builder.Services.AddSingleton<DiscordNotificationService>();
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddScoped<HolidayService>();
builder.Services.AddSingleton<TimeAndDateHolidayService>();
builder.Services.AddSingleton<GeoLocationService>();
builder.Services.AddSingleton<ViewerDbService>();
builder.Services.AddSingleton<ViewerTrackingService>();
builder.Services.AddScoped<ArtGenerationService>();
builder.Services.AddSingleton<PhotoService>();
builder.Services.AddScoped<WeatherService>();
builder.Services.AddHostedService<DailyArtHostedService>();

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// ── Public static files (wwwroot: app.css, favicon, MudBlazor, _framework) ───
// Served BEFORE auth so the login/pending pages can load their styles.
app.UseStaticFiles();

// ── Authentication ────────────────────────────────────────────────────────────
app.UseAuthentication();
app.UseAuthorization();

// ── Auth redirect — protect everything except public auth paths ───────────────
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var isPublic = path.StartsWith("/auth",       StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/_blazor",    StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/_content",   StringComparison.OrdinalIgnoreCase);

    if (!isPublic && ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
        return;
    }
    await next();
});

// ── Auth endpoints ────────────────────────────────────────────────────────────
// Start Google OAuth challenge
app.MapGet("/auth/login-google", (HttpContext ctx, string? returnUrl) =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
        [GoogleDefaults.AuthenticationScheme]));

// Sign out
app.MapGet("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/auth/login");
});

// Approve a pending user (admin clicks link from Discord — no auth required)
app.MapGet("/auth/approve", async (string token, ApprovalService approval) =>
{
    var email = await approval.TryApproveAsync(token);
    return email is not null
        ? Results.Content($"<html><body style='font-family:sans-serif;background:#0f0f1a;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'><div style='text-align:center'><div style='font-size:3rem'>✅</div><h2>{email} has been approved.</h2><p style='opacity:.5'>They can now sign in.</p></div></body></html>", "text/html")
        : Results.Content("<html><body style='font-family:sans-serif;background:#0f0f1a;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'><div style='text-align:center'><div style='font-size:3rem'>⚠️</div><h2>Token invalid or expired.</h2></div></body></html>", "text/html");
});

// Deny a pending user
app.MapGet("/auth/deny", async (string token, ApprovalService approval) =>
{
    var email = await approval.TryDenyAsync(token);
    return email is not null
        ? Results.Content($"<html><body style='font-family:sans-serif;background:#0f0f1a;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'><div style='text-align:center'><div style='font-size:3rem'>❌</div><h2>{email} has been denied.</h2></div></body></html>", "text/html")
        : Results.Content("<html><body style='font-family:sans-serif;background:#0f0f1a;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0'><div style='text-align:center'><div style='font-size:3rem'>⚠️</div><h2>Token invalid or expired.</h2></div></body></html>", "text/html");
});

// ── Viewer tracking middleware ─────────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Method == "GET"
        && ctx.Request.Path == "/"
        && ctx.Request.Headers.Accept.Any(a => a != null && a.Contains("text/html")))
    {
        var ip = ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
              ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
              ?? ctx.Connection.RemoteIpAddress?.ToString();
        if (ip is not null)
        {
            var tracker = ctx.RequestServices.GetRequiredService<ViewerTrackingService>();
            _ = tracker.RecordVisitAsync(ip);
        }
    }
    await next();
});

app.UseAntiforgery();

// ── Persistent art-cache static files (protected — auth required) ─────────────
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
