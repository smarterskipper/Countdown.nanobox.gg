using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HomelabCountdown.Services;

/// <summary>
/// Thrown when Replicate rejects a prediction because the input or output
/// tripped its safety filter (error code E005). Callers can catch this to
/// rewrite the prompt and retry.
/// </summary>
public sealed class ReplicateSafetyException : Exception
{
    public ReplicateSafetyException(string message) : base(message) { }
}

/// <summary>
/// Calls the Replicate API to generate images via Flux 2 Pro.
/// </summary>
public class ReplicateImageService
{
    private const string FluxUrl = "https://api.replicate.com/v1/models/black-forest-labs/flux-2-pro/predictions";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly ILogger<ReplicateImageService> _logger;

    public ReplicateImageService(IConfiguration config, IHttpClientFactory httpFactory, ILogger<ReplicateImageService> logger)
    {
        _logger = logger;
        _http = httpFactory.CreateClient();

        var apiKey = config["Replicate:ApiKey"] is { Length: > 0 } k
            ? k
            : Environment.GetEnvironmentVariable("REPLICATE_API_KEY")
              ?? throw new InvalidOperationException("Replicate API key not configured. Set Replicate:ApiKey or REPLICATE_API_KEY.");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add("Prefer", "wait");
    }

    /// <summary>
    /// Generates an image from the given prompt and returns the raw PNG bytes.
    /// </summary>
    public async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken ct = default)
    {
        _logger.LogInformation("Submitting Flux prediction ({Len} chars)", prompt.Length);

        var body = JsonSerializer.Serialize(new
        {
            input = new { prompt, aspect_ratio = "16:9", output_format = "png", output_quality = 100 }
        });

        var createResp = await _http.PostAsync(
            FluxUrl,
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        createResp.EnsureSuccessStatusCode();
        var createJson = await createResp.Content.ReadAsStringAsync(ct);
        var createDoc = JsonDocument.Parse(createJson);

        var predId = createDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("No prediction ID in response");
        var pollUrl = createDoc.RootElement.GetProperty("urls").GetProperty("get").GetString()
            ?? throw new InvalidOperationException("No poll URL in response");

        _logger.LogInformation("Flux prediction {Id} created, polling…", predId);

        return await PollForOutputAsync(predId, pollUrl, ct);
    }

    private async Task<byte[]> PollForOutputAsync(string predId, string pollUrl, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);

            var pollResp = await _http.GetAsync(pollUrl, ct);
            pollResp.EnsureSuccessStatusCode();

            var pollJson = await pollResp.Content.ReadAsStringAsync(ct);
            var pollDoc = JsonDocument.Parse(pollJson);
            var status = pollDoc.RootElement.GetProperty("status").GetString();

            _logger.LogInformation("Prediction {Id} status: {Status}", predId, status);

            if (status == "succeeded")
            {
                var outputEl = pollDoc.RootElement.GetProperty("output");
                var outputUrl = outputEl.ValueKind == JsonValueKind.Array
                    ? outputEl.EnumerateArray().First().GetString()
                    : outputEl.GetString()
                    ?? throw new InvalidOperationException("No output URL in succeeded prediction");

                _logger.LogInformation("Prediction {Id} succeeded, downloading output", predId);
                return await _http.GetByteArrayAsync(outputUrl, ct);
            }

            if (status is "failed" or "canceled")
            {
                var error = pollDoc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                if (error is not null && error.Contains("E005", StringComparison.Ordinal))
                    throw new ReplicateSafetyException(error);
                throw new InvalidOperationException($"Prediction {predId} {status}: {error}");
            }
        }

        throw new TimeoutException($"Prediction {predId} did not complete within {Timeout}");
    }
}
