using System.Text;
using System.Text.Json;
using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

public class DiscordNotificationService
{
    private readonly HttpClient _http;
    private readonly string? _webhookUrl;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(IHttpClientFactory factory, IConfiguration config, ILogger<DiscordNotificationService> logger)
    {
        _http = factory.CreateClient();
        _webhookUrl = config["Discord:WebhookUrl"] is { Length: > 0 } s ? s
                    : Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        _logger = logger;
    }

    public async Task NotifyArtGeneratedAsync(DailyArt art)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;

        try
        {
            var flag = GetFlag(art.CountryCode);
            var payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title = $"{flag} Today's art is ready — {art.HolidayName}",
                        description = $"**{art.CountryName}** · {art.Date:MMMM d, yyyy}",
                        color = HexToInt(art.AccentColor),
                        fields = new[]
                        {
                            new { name = "Theme", value = art.Theme, inline = true },
                            new { name = "Score", value = $"{art.FinalScore:F1}/10", inline = true },
                            new { name = "Attempts", value = art.AttemptCount.ToString(), inline = true },
                            new { name = "Claude's notes", value = string.IsNullOrEmpty(art.FinalCritique) ? "Passed first review ✨" : art.FinalCritique, inline = false }
                        },
                        footer = new { text = "HomelabCountdown · daily AI art" }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(_webhookUrl, content);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Discord notification failed: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord notification error");
        }
    }

    public async Task SendApprovalRequestAsync(string email, string token, string siteBaseUrl)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;
        var approveUrl = $"{siteBaseUrl}/auth/approve?token={token}";
        var denyUrl    = $"{siteBaseUrl}/auth/deny?token={token}";

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = "🔐 New sign-in request",
                    description = $"**{email}** wants access to the countdown.",
                    color = 0xF59E0B,
                    fields = new[]
                    {
                        new { name = "Approve", value = $"[✅ Click to approve]({approveUrl})", inline = true },
                        new { name = "Deny",    value = $"[❌ Click to deny]({denyUrl})",       inline = true }
                    },
                    footer = new { text = "Link expires in 24 hours" }
                }
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            await _http.PostAsync(_webhookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to send approval request to Discord"); }
    }

    public async Task SendApprovalResultAsync(string email, bool approved)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;
        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = approved ? "✅ User approved" : "❌ User denied",
                    description = $"**{email}** was {(approved ? "approved" : "denied")}.",
                    color = approved ? 0x3BA55D : 0xED4245
                }
            }
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            await _http.PostAsync(_webhookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to send approval result to Discord"); }
    }

    private static int HexToInt(string hex)
    {
        try { return Convert.ToInt32(hex.TrimStart('#'), 16); }
        catch { return 0x6366f1; }
    }

    private static string GetFlag(string code) => code.Length != 2 ? "🌍" :
        string.Concat(code.ToUpperInvariant().Select(c => char.ConvertFromUtf32(c + 0x1F1A5)));
}
