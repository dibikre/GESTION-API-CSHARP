using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ApiTester.Services;
using ApiTester.ViewModels;

namespace ApiTester.Models;

public sealed class ChainDefinition
{
    public ObservableCollection<ChainStep> Steps { get; } = [];
}

public sealed class ChainAccumulatedVariableItem
{
    public required string Name { get; init; }
    public required string RawValue { get; init; }
    public string? FullText { get; init; }
    public string DisplayValue => BatchExecutionHelper.MaskIfSensitive(Name, RawValue);
    public bool HasInspectableText =>
        !string.IsNullOrEmpty(FullText) && !BatchExecutionHelper.IsSensitiveName(Name);
}

public sealed class ChainStepResult
{
    public int Index { get; set; }
    public int StepIndex { get; set; }
    public string StepName { get; set; } = string.Empty;
    public int Iteration { get; set; } = 1;
    public int TotalIterationsForStep { get; set; } = 1;
    public string RequestSummary { get; set; } = string.Empty;
    public string RequestUrl { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public StatusCategory StatusCategory { get; set; } = StatusCategory.None;
    public long DurationMs { get; set; }
    public long SizeBytes { get; set; }
    public string SizeText { get; set; } = "0 B";
    public string ContentType { get; set; } = string.Empty;
    public ResponseModel? Response { get; set; }
    public IReadOnlyDictionary<string, string> ExtractedValues { get; set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExtractedLists { get; set; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyList<string> ExtractionErrors { get; set; } = [];
    public ChainStepStatus Status { get; set; } = ChainStepStatus.Success;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<KeyValueItem> VaryingParams { get; set; } = [];
    public IReadOnlyList<KeyValueItem> VariableValues { get; set; } = [];
    public IReadOnlyList<KeyValueItem> OrderedAxisValues { get; set; } = [];
    public IReadOnlyList<KeyValueItem> QueryParams { get; set; } = [];
    public string? ReturnValue { get; set; }
    public bool IsReturnOnly { get; set; }
    public bool WasRateLimited { get; set; }
    public int RetryAttempts { get; set; }
    public long FinalWaitMs { get; set; }

    public string StepIterationLabel => TotalIterationsForStep > 1
        ? $"{StepIndex}. {StepName} ({Iteration}/{TotalIterationsForStep})"
        : $"{StepIndex}. {StepName}";

    public bool IsSuccessStatus => Status == ChainStepStatus.Success;
    public bool IsFailedStatus => Status == ChainStepStatus.Failed;
    public bool IsSkippedStatus => Status == ChainStepStatus.Skipped;
    public bool HasReturnValue => !string.IsNullOrEmpty(ReturnValue);
    public string ReturnPreviewText => HasReturnValue
        ? $"Retour : {BatchExecutionHelper.TruncateForDisplay(ReturnValue!)}"
        : string.Empty;

    public string StatusBadgeText => Status switch
    {
        ChainStepStatus.Skipped => "Ignorée",
        ChainStepStatus.Failed when StatusCode > 0 => $"{StatusCode} (échec)",
        ChainStepStatus.Failed => "Échec",
        _ when IsReturnOnly => "Retour",
        _ when StatusCode > 0 => $"{StatusCode}",
        _ => "OK"
    };

    public string RateLimitSuffixText => !WasRateLimited
        ? string.Empty
        : RetryAttempts switch
        {
            0 => "(rate limit)",
            1 => "(1 tentative)",
            _ => $"({RetryAttempts} tentatives)"
        };

    public string ExtractedSummaryText
    {
        get
        {
            if (ExtractedValues.Count == 0 && ExtractedLists.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(ExtractedValues.Count + ExtractedLists.Count);
            foreach (var kvp in ExtractedValues)
            {
                var display = BatchExecutionHelper.TruncateForDisplay(
                    BatchExecutionHelper.MaskIfSensitive(kvp.Key, kvp.Value));
                parts.Add($"{kvp.Key}={display}");
            }

            foreach (var kvp in ExtractedLists)
            {
                var list = kvp.Value;
                if (BatchExecutionHelper.IsSensitiveName(kvp.Key))
                {
                    parts.Add($"[[{kvp.Key}]]=[{list.Count}] ***");
                }
                else
                {
                    var preview = BatchExecutionHelper.TruncateForDisplay(string.Join(", ", list.Take(3)));
                    parts.Add($"[[{kvp.Key}]]=[{list.Count}] {preview}");
                }
            }

            return string.Join(", ", parts);
        }
    }

    public bool HasExtractedSummary => ExtractedValues.Count > 0 || ExtractedLists.Count > 0;
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    public BatchResultItem ToBatchResultItem() => new()
    {
        Index = Index,
        StepIndex = StepIndex,
        StepName = StepName,
        Iteration = Iteration,
        ExtractedValues = ExtractedValues,
        ParamsText = TotalIterationsForStep > 1
            ? $"{StepName} (#{Iteration}/{TotalIterationsForStep})"
            : StepName,
        StatusCode = StatusCode,
        StatusCategory = StatusCategory,
        DurationMs = DurationMs,
        SizeText = SizeText,
        SizeBytes = SizeBytes,
        ContentType = ContentType,
        RequestUrl = RequestUrl,
        VaryingParams = VaryingParams,
        VariableValues = VariableValues,
        OrderedAxisValues = OrderedAxisValues,
        QueryParams = QueryParams,
        TimestampUtc = TimestampUtc,
        ErrorMessage = ErrorMessage,
        Response = Response,
        WasRateLimited = WasRateLimited,
        RetryAttempts = RetryAttempts,
        FinalWaitMs = FinalWaitMs
    };
}

public sealed class ChainRunResult
{
    public IReadOnlyList<ChainStepResult> StepResults { get; init; } = [];
    public ChainRunStatus FinalStatus { get; init; } = ChainRunStatus.Success;
    public IReadOnlyDictionary<string, string> AllVariables { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AllLists { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public string? FinalReturnValue { get; init; }
    public long TotalDurationMs { get; init; }
}
