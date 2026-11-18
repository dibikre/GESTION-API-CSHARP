using System;
using ApiTester.Models;

namespace ApiTester.Services;

public interface IRateLimitDetector
{
    bool IsRateLimited(ResponseModel response, RateLimitPolicy policy);

    TimeSpan? ParseRetryAfter(ResponseModel response);

    bool ShouldSlowDownPreemptively(ResponseModel response, RateLimitPolicy policy, out TimeSpan? suggestedWait);

    TimeSpan ComputeRateLimitDelay(
        ResponseModel response,
        RateLimitPolicy policy,
        int normalDelayMs,
        ref int consecutiveRateLimitFailures,
        out bool usedSpecificRateLimitWait);
}
