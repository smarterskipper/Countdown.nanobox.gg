namespace HomelabCountdown.Services;

/// <summary>
/// Singleton that broadcasts live art-generation progress to any subscribed UI.
/// </summary>
public class GenerationStatusService
{
    public GenerationProgress? Current { get; private set; }

    public event Action<GenerationProgress?>? OnChanged;

    public void Update(string phase, int attempt = 0, int maxAttempts = 0, double? lastScore = null)
    {
        Current = new GenerationProgress(phase, attempt, maxAttempts, lastScore);
        OnChanged?.Invoke(Current);
    }

    public void Clear()
    {
        Current = null;
        OnChanged?.Invoke(null);
    }
}

public sealed record GenerationProgress(
    string Phase,
    int Attempt,
    int MaxAttempts,
    double? LastScore
);
