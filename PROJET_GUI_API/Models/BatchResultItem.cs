using System;
using System.Collections.Generic;
using ApiTester.ViewModels;

namespace ApiTester.Models;

public class BatchResultItem
{
    public int Index { get; set; }
    public string ParamsText { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public StatusCategory StatusCategory { get; set; } = StatusCategory.None;
    public long DurationMs { get; set; }
    public string SizeText { get; set; } = "0 B";
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public IReadOnlyList<KeyValueItem> VaryingParams { get; set; } = [];
    public IReadOnlyList<KeyValueItem> VariableValues { get; set; } = [];
    public IReadOnlyList<KeyValueItem> OrderedAxisValues { get; set; } = [];
    public IReadOnlyList<KeyValueItem> QueryParams { get; set; } = [];
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? ErrorMessage { get; set; }
    public ResponseModel? Response { get; set; }
    public bool WasRateLimited { get; set; }
    public int RetryAttempts { get; set; }
    public long FinalWaitMs { get; set; }
    public int StepIndex { get; set; }
    public string? StepName { get; set; }
    public int Iteration { get; set; }
    public IReadOnlyDictionary<string, string>? ExtractedValues { get; set; }

    public string RateLimitSuffixText => !WasRateLimited
        ? string.Empty
        : RetryAttempts switch
        {
            0 => "(rate limit)",
            1 => "(1 tentative)",
            _ => $"({RetryAttempts} tentatives)"
        };

    public bool HasStandardErrorMessage => !WasRateLimited && !string.IsNullOrEmpty(ErrorMessage);

    public bool HasRateLimitErrorMessage => WasRateLimited && !string.IsNullOrEmpty(ErrorMessage);
}
