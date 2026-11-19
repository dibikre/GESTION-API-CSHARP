using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class ChainPlanResult
{
    public int TotalRequests { get; set; }
    public bool ExceedsHardLimit { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? ValidationError { get; set; }
    public IReadOnlyList<int> StepIterationCounts { get; set; } = [];
}

public sealed class ChainRunOptions
{
    public bool KeepBodies { get; set; } = true;
    public RateLimitPolicy GlobalRateLimitPolicy { get; set; } = new();
}

public sealed class ChainProgressReport
{
    public int StepIndex { get; set; }
    public int TotalSteps { get; set; }
    public int Iteration { get; set; }
    public int StepTotalIterations { get; set; }
    public int GlobalIndex { get; set; }
    public int GlobalTotal { get; set; }
    public int RetryCount { get; set; }
    public bool IsRetryUpdate { get; set; }
    public string StepName { get; set; } = string.Empty;
    public ChainStepResult? Item { get; set; }
    public IReadOnlyDictionary<string, string>? AccumulatedVariables { get; set; }
}

public interface IChainRunner
{
    ChainPlanResult BuildPlan(IReadOnlyList<ChainStep> steps);

    Task<ChainRunResult> RunAsync(
        IReadOnlyList<ChainStep> steps,
        ChainRunOptions options,
        IProgress<ChainProgressReport> progress,
        Func<ChainStepResult, CancellationToken, Task>? onResultAsync = null,
        CancellationToken ct = default);
}
