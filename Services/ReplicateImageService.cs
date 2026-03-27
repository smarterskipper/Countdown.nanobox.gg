using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HomelabCountdown.Services;

/// <summary>
/// Calls the Replicate API to generate images (Flux 2 Pro) and animate them (Stable Video Diffusion).
/// </summary>
public class ReplicateImageService
{
    private const string PredictionUrl = "https://api.replicate.com/v1/models/black-forest-labs/flux-2-pro/predictions";
    // Stable Video Diffusion XT — versioned endpoint required (model doesn't support "latest" API)
    private const string PredictionsUrl = "https://api.replicate.com/v1/predictions";
    private const string SvdVersion = "3f0457e4619daac51203dedb472816fd4af51f3149fa7a9e0b5ffcf1b8172438";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

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
        _logger.LogInformation("Submitting Replicate prediction for prompt ({Len} chars)", prompt.Length);

        // Create prediction
        var body = JsonSerializer.Serialize(new
        {
            input = new
            {
                prompt,
                output_format = "png"
            }
        });

        var createResp = await _http.PostAsync(
            PredictionUrl,
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        createResp.EnsureSuccessStatusCode();
        var createJson = await createResp.Content.ReadAsStringAsync(ct);
        var createDoc = JsonDocument.Parse(createJson);

        var predId = createDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("No prediction ID in response");
        var pollUrl = createDoc.RootElement.GetProperty("urls").GetProperty("get").GetString()
            ?? throw new InvalidOperationException("No poll URL in response");

        _logger.LogInformation("Prediction {Id} created, polling…", predId);

        // Poll until done
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
                // Output is an array of URLs; take the first
                var outputEl = pollDoc.RootElement.GetProperty("output");
                var outputUrl = outputEl.ValueKind == JsonValueKind.Array
                    ? outputEl.EnumerateArray().First().GetString()
                    : outputEl.GetString()
                    ?? throw new InvalidOperationException("No output URL in succeeded prediction");

                _logger.LogInformation("Prediction {Id} succeeded, downloading image from {Url}", predId, outputUrl);
                return await _http.GetByteArrayAsync(outputUrl, ct);
            }

            if (status is "failed" or "canceled")
            {
                var error = pollDoc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                throw new InvalidOperationException($"Replicate prediction {predId} {status}: {error}");
            }
        }

        throw new TimeoutException($"Replicate prediction {predId} did not complete within {Timeout}");
    }

    /// <summary>
    /// Takes a PNG image and animates it into a short looping MP4 via Stable Video Diffusion.
    /// </summary>
    public async Task<byte[]> AnimateImageAsync(byte[] imagePng, CancellationToken ct = default)
    {
        // Pass image as base64 data URI — avoids the file upload API entirely
        var base64 = Convert.ToBase64String(imagePng);
        var dataUri = $"data:image/png;base64,{base64}";

        _logger.LogInformation("Submitting SVD prediction ({Kb} KB image as data URI)", imagePng.Length / 1024);
        var body = JsonSerializer.Serialize(new
        {
            version = SvdVersion,
            input = new
            {
                input_image = dataUri,
                video_length = "25_frames_with_svd_xt",
                sizing_strategy = "crop_to_aspect_ratio",
                frames_per_second = 6,
                motion_bucket_id = 127,
                cond_aug = 0.02
            }
        });

        var createResp = await _http.PostAsync(
            PredictionsUrl,
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        if (!createResp.IsSuccessStatusCode)
        {
            var errBody = await createResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"SVD create failed {(int)createResp.StatusCode}: {errBody}");
        }

        var createJson = await createResp.Content.ReadAsStringAsync(ct);
        var createDoc = JsonDocument.Parse(createJson);

        var predId = createDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("No prediction ID in SVD response");
        var pollUrl = createDoc.RootElement.GetProperty("urls").GetProperty("get").GetString()
            ?? throw new InvalidOperationException("No poll URL in SVD response");

        _logger.LogInformation("SVD prediction {Id} created, polling…", predId);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);

            var pollResp = await _http.GetAsync(pollUrl, ct);
            pollResp.EnsureSuccessStatusCode();

            var pollJson = await pollResp.Content.ReadAsStringAsync(ct);
            var pollDoc = JsonDocument.Parse(pollJson);
            var status = pollDoc.RootElement.GetProperty("status").GetString();

            _logger.LogInformation("SVD {Id} status: {Status}", predId, status);

            if (status == "succeeded")
            {
                var outputEl = pollDoc.RootElement.GetProperty("output");
                var videoUrl = outputEl.ValueKind == JsonValueKind.Array
                    ? outputEl.EnumerateArray().First().GetString()
                    : outputEl.GetString()
                    ?? throw new InvalidOperationException("No output URL in succeeded SVD prediction");

                _logger.LogInformation("SVD {Id} succeeded, downloading video from {Url}", predId, videoUrl);
                return await _http.GetByteArrayAsync(videoUrl, ct);
            }

            if (status is "failed" or "canceled")
            {
                var error = pollDoc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                throw new InvalidOperationException($"SVD prediction {predId} {status}: {error}");
            }
        }

        throw new TimeoutException($"SVD prediction {predId} did not complete within {Timeout}");
    }
}
