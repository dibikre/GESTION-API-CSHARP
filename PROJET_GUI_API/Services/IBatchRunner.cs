using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class BatchPlanResult
{
    public int TotalRequests { get; set; }
    public bool ExceedsHardLimit { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string? ValidationError { get; set; }
}

public sealed class BatchRunOptions
{
    public BatchMode Mode { get; set; } = BatchMode.Combinations;
    public int DelayMs { get; set; } = 500;
    public bool StopOnFailure { get; set; }
    public bool KeepBodies { get; set; } = true;
}

public sealed class BatchProgressReport
{
    public int Index { get; set; }
    public int Total { get; set; }
    public BatchResultItem Item { get; set; } = new();
}

public interface IBatchRunner
{
    BatchPlanResult BuildPlan(IReadOnlyList<VaryingParam> parameters, BatchMode mode);

    Task RunAsync(
        RequestModel templateRequest,
        IReadOnlyList<VaryingParam> parameters,
        BatchRunOptions options,
        IProgress<BatchProgressReport> progress,
        Func<BatchResultItem, CancellationToken, Task>? onResultAsync = null,
        CancellationToken ct = default);
}
