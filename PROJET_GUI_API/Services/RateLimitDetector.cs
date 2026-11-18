using System;
using System.Globalization;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class RateLimitDetector : IRateLimitDetector
{
    /// <summary>
    /// Seuil de distinction pour l'en-tête de réinitialisation du quota :
    /// - valeur >= 1_000_000_000 (sept. 2001 UTC) -> horodatage Unix en secondes ;
    /// - valeur &lt; 1_000_000_000 -> durée d'attente relative en secondes.
    /// </summary>
    public const long UnixEpochThresholdSeconds = 1_000_000_000L;

    private const double JitterRatio = 0.10;

    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly Func<double> _randomUnitProvider;

    public RateLimitDetector()
        : this(() => DateTimeOffset.UtcNow, () => Random.Shared.NextDouble())
    {
    }

    public RateLimitDetector(Func<DateTimeOffset> utcNowProvider, Func<double> randomUnitProvider)
    {
        _utcNowProvider = utcNowProvider ?? throw new ArgumentNullException(nameof(utcNowProvider));
        _randomUnitProvider = randomUnitProvider ?? throw new ArgumentNullException(nameof(randomUnitProvider));
    }

    public bool IsRateLimited(ResponseModel response, RateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(policy);

        if (response.StatusCode == 429)
        {
            return true;
        }

        return response.StatusCode >= RateLimitPolicy.MinHttpStatusCode &&
               policy.ExtraStatusCodes.Contains(response.StatusCode);
    }

    public TimeSpan? ParseRetryAfter(ResponseModel response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ParseRetryAfter(response, _utcNowProvider());
    }

    public static TimeSpan? ParseRetryAfter(ResponseModel response, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = FindHeaderValue(response, "Retry-After");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds < 0)
            {
                return null;
            }

            return seconds >= (long)TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var retryAt))
        {
            var delta = retryAt - utcNow;
            return delta <= TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return null;
    }

    public bool ShouldSlowDownPreemptively(ResponseModel response, RateLimitPolicy policy, out TimeSpan? suggestedWait)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(policy);

        return ShouldSlowDownPreemptively(response, policy, _utcNowProvider(), out suggestedWait);
    }

    public static bool ShouldSlowDownPreemptively(
        ResponseModel response,
        RateLimitPolicy policy,
        DateTimeOffset utcNow,
        out TimeSpan? suggestedWait)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(policy);

        suggestedWait = null;

        if (string.IsNullOrWhiteSpace(policy.QuotaRemainingHeader))
        {
            return false;
        }

        var remainingRaw = FindHeaderValue(response, policy.QuotaRemainingHeader);
        if (string.IsNullOrWhiteSpace(remainingRaw) ||
            !long.TryParse(remainingRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
        {
            return false;
        }

        if (remaining > policy.QuotaThreshold)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(policy.QuotaResetHeader))
        {
            var resetRaw = FindHeaderValue(response, policy.QuotaResetHeader);
            if (!string.IsNullOrWhiteSpace(resetRaw) &&
                double.TryParse(resetRaw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var resetValue) &&
                double.IsFinite(resetValue) &&
                resetValue >= 0)
            {
                TimeSpan computedWait;
                if (resetValue >= UnixEpochThresholdSeconds)
                {
                    var wholeSeconds = resetValue >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(resetValue);
                    if (wholeSeconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                    {
                        var resetAt = DateTimeOffset.FromUnixTimeSeconds(wholeSeconds);
                        computedWait = resetAt > utcNow ? resetAt - utcNow : TimeSpan.Zero;
                    }
                    else
                    {
                        computedWait = TimeSpan.FromSeconds(Math.Max(0, policy.MaxRetryAfterSeconds));
                    }
                }
                else
                {
                    var maxCapSeconds = Math.Max(0, policy.MaxRetryAfterSeconds);
                    computedWait = resetValue > maxCapSeconds
                        ? TimeSpan.FromSeconds(maxCapSeconds)
                        : TimeSpan.FromSeconds(resetValue);
                }

                var maxRetryAfter = TimeSpan.FromSeconds(Math.Max(0, policy.MaxRetryAfterSeconds));
                suggestedWait = computedWait > maxRetryAfter ? maxRetryAfter : computedWait;
            }
        }

        return true;
    }

    public TimeSpan ComputeRateLimitDelay(
        ResponseModel response,
        RateLimitPolicy policy,
        int normalDelayMs,
        ref int consecutiveRateLimitFailures,
        out bool usedSpecificRateLimitWait)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.RespectRetryAfter)
        {
            var retryAfter = ParseRetryAfter(response);
            if (retryAfter.HasValue)
            {
                usedSpecificRateLimitWait = true;
                var maxCap = TimeSpan.FromSeconds(Math.Max(0, policy.MaxRetryAfterSeconds));
                return retryAfter.Value > maxCap ? maxCap : retryAfter.Value;
            }
        }

        if (policy.EnableBackoff)
        {
            usedSpecificRateLimitWait = true;
            return ComputeBackoffDelay(policy, ref consecutiveRateLimitFailures);
        }

        usedSpecificRateLimitWait = false;
        return TimeSpan.FromMilliseconds(Math.Max(0, normalDelayMs));
    }

    public TimeSpan ComputeBackoffDelay(RateLimitPolicy policy, ref int consecutiveRateLimitFailures)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var initialMs = Math.Max(0, policy.BackoffInitialMs);
        var maxMs = Math.Max(initialMs, policy.BackoffMaxMs);
        var multiplier = double.IsFinite(policy.BackoffMultiplier) && policy.BackoffMultiplier > 1.0
            ? policy.BackoffMultiplier
            : 2.0;

        if (initialMs == 0 || maxMs == 0)
        {
            return TimeSpan.Zero;
        }

        double baseDelayMs = initialMs;
        for (var i = 0; i < consecutiveRateLimitFailures; i++)
        {
            baseDelayMs *= multiplier;
            if (baseDelayMs >= maxMs)
            {
                baseDelayMs = maxMs;
                break;
            }
        }

        if (baseDelayMs < maxMs)
        {
            consecutiveRateLimitFailures++;
        }

        var unit = Math.Clamp(_randomUnitProvider(), 0.0, 1.0);
        var jitterFactor = (1.0 - JitterRatio) + (unit * (2.0 * JitterRatio));
        var jitteredMs = (long)Math.Round(baseDelayMs * jitterFactor);
        var clampedMs = Math.Clamp(jitteredMs, 0L, maxMs);
        return TimeSpan.FromMilliseconds(clampedMs);
    }

    private static string? FindHeaderValue(ResponseModel response, string headerName)
    {
        var target = headerName.Trim();
        for (var i = response.Headers.Count - 1; i >= 0; i--)
        {
            var header = response.Headers[i];
            if (header.IsEnabled &&
                header.Key.Trim().Equals(target, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(header.Value))
            {
                return header.Value;
            }
        }

        return null;
    }
}
