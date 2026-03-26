using System.Collections.Concurrent;

namespace HomelabCountdown.Services;

/// <summary>
/// Manages the approved-email list and pending approval requests.
/// Approved emails persist to approved-emails.txt in the art-cache directory.
/// </summary>
public class ApprovalService
{
    private readonly string _approvedFile;
    private readonly HashSet<string> _approved;
    private readonly ConcurrentDictionary<string, (string Email, DateTimeOffset Expires)> _pending = new();
    private readonly DiscordNotificationService _discord;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ILogger<ApprovalService> _logger;

    public ApprovalService(
        IConfiguration config,
        DiscordNotificationService discord,
        ILogger<ApprovalService> logger)
    {
        _discord = discord;
        _logger = logger;

        var cacheDir = config["ArtCache:Path"] is { Length: > 0 } p
            ? p : Path.Combine(AppContext.BaseDirectory, "art-cache");
        Directory.CreateDirectory(cacheDir);
        _approvedFile = Path.Combine(cacheDir, "approved-emails.txt");

        // Seed from config (bootstrap / first-run)
        _approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configEmails = (config["Google:ApprovedEmails"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var e in configEmails) _approved.Add(e);

        // Load persisted approvals
        if (File.Exists(_approvedFile))
        {
            foreach (var line in File.ReadAllLines(_approvedFile))
            {
                var email = line.Trim();
                if (!string.IsNullOrEmpty(email)) _approved.Add(email);
            }
        }

        _logger.LogInformation("ApprovalService: {Count} approved email(s) loaded", _approved.Count);
    }

    public bool IsApproved(string email) => _approved.Contains(email);

    /// <summary>
    /// Queues an approval request and fires a Discord notification if not already pending.
    /// </summary>
    public async Task RequestApprovalAsync(string email, string siteBaseUrl)
    {
        // Don't spam Discord if already pending for this email
        var alreadyPending = _pending.Values.Any(v =>
            v.Email.Equals(email, StringComparison.OrdinalIgnoreCase)
            && v.Expires > DateTimeOffset.UtcNow);

        if (alreadyPending) return;

        var token = Guid.NewGuid().ToString("N");
        _pending[token] = (email, DateTimeOffset.UtcNow.AddHours(24));

        await _discord.SendApprovalRequestAsync(email, token, siteBaseUrl);
        _logger.LogInformation("Approval requested for {Email}, token {Token}", email, token[..8] + "…");
    }

    /// <summary>Returns the approved email on success, null if token invalid/expired.</summary>
    public async Task<string?> TryApproveAsync(string token)
    {
        if (!_pending.TryRemove(token, out var entry)) return null;
        if (entry.Expires < DateTimeOffset.UtcNow) return null;

        await _fileLock.WaitAsync();
        try
        {
            _approved.Add(entry.Email);
            await File.AppendAllTextAsync(_approvedFile, entry.Email + Environment.NewLine);
        }
        finally { _fileLock.Release(); }

        await _discord.SendApprovalResultAsync(entry.Email, approved: true);
        _logger.LogInformation("Approved {Email}", entry.Email);
        return entry.Email;
    }

    /// <summary>Returns the denied email on success, null if token invalid/expired.</summary>
    public async Task<string?> TryDenyAsync(string token)
    {
        if (!_pending.TryRemove(token, out var entry)) return null;
        await _discord.SendApprovalResultAsync(entry.Email, approved: false);
        _logger.LogInformation("Denied {Email}", entry.Email);
        return entry.Email;
    }
}
