namespace IntegrationHub.Core.Processing;

/// <summary>Long-term (minutes) retry schedule for inbox and outbox rows. Bound from configuration.</summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);

    public bool ShouldDeadLetter(int attemptCount) => attemptCount >= MaxAttempts;

    /// <summary>
    /// Exponential backoff (base * 2^(attempt-1), capped) with "equal jitter": half of the delay is fixed,
    /// half is random. Spreads out retries of many messages that failed together without collapsing to ~0.
    /// </summary>
    /// <param name="attemptCount">Attempts made so far (1 after the first failure).</param>
    /// <param name="random01">Uniform random value in [0, 1); injected so the calculation is testable.</param>
    public TimeSpan NextDelay(int attemptCount, double random01)
    {
        var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(attemptCount, 1) - 1);
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds((capped / 2) + (capped / 2 * random01));
    }
}
