using System;
using System.Collections.Generic;

namespace ApiTester.Models;

public sealed class ChainPipelineDocument
{
    public const string CurrentFormat = "apitester.pipeline";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = CurrentFormat;
    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset SavedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool KeepBodies { get; set; } = true;
    public bool ContinueOnError { get; set; }
    public ChainCliLanguage SelectedCliLanguage { get; set; } = ChainCliLanguage.Python;
    public ChainPipelineRateLimitPolicyDto GlobalRateLimitPolicy { get; set; } = new();
    public ChainPipelineSaveOptionsDto SaveOptions { get; set; } = new();
    public List<ChainPipelineStepDto> Steps { get; set; } = [];
}

public sealed class ChainPipelineRateLimitPolicyDto
{
    public bool IsEnabled { get; set; } = true;
    public bool RespectRetryAfter { get; set; } = true;
    public int MaxRetryAfterSeconds { get; set; } = 120;
    public bool EnableBackoff { get; set; }
    public int BackoffInitialMs { get; set; } = 500;
    public double BackoffMultiplier { get; set; } = 2.0;
    public int BackoffMaxMs { get; set; } = 30_000;
    public int MaxRetriesPerRequest { get; set; } = 3;
    public List<int> ExtraStatusCodes { get; set; } = [];
    public string QuotaRemainingHeader { get; set; } = string.Empty;
    public string QuotaResetHeader { get; set; } = string.Empty;
    public int QuotaThreshold { get; set; } = 1;
    public int PauseFloorMs { get; set; } = RateLimitPolicy.DefaultPauseFloorMs;
}

public sealed class ChainPipelineSaveOptionsDto
{
    public SaveMode SaveMode { get; set; } = SaveMode.None;
    public string OutputFolder { get; set; } = string.Empty;
    public GroupedFormat GroupedFormat { get; set; } = GroupedFormat.JsonLines;
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool IncludeSummaryCsv { get; set; } = true;
}

public sealed class ChainPipelineStepDto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int Rank { get; set; } = 1;
    public string CurlText { get; set; } = string.Empty;
    public bool HasParsed { get; set; }
    public HttpMethodType Method { get; set; } = HttpMethodType.Get;
    public string Url { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public BodyType BodyType { get; set; } = BodyType.None;
    public string ReturnExpression { get; set; } = string.Empty;
    public BatchMode SelectedMode { get; set; } = BatchMode.Combinations;
    public int DelayAfterMs { get; set; } = 500;
    public bool ContinueOnError { get; set; }
    public bool StepRateLimitUsesGlobalPolicy { get; set; } = true;
    public int ParsedTimeoutSeconds { get; set; } = 30;
    public ChainPipelineAuthDto ParsedAuth { get; set; } = new();
    public List<string> ParseWarnings { get; set; } = [];
    public List<ChainPipelineKeyValueDto> Headers { get; set; } = [];
    public List<ChainPipelineKeyValueDto> FormFields { get; set; } = [];
    public List<ChainPipelineVaryingParamDto> VaryingParams { get; set; } = [];
    public List<ChainPipelineVariableDto> Variables { get; set; } = [];
    public List<ChainPipelineAxisOrderDto> AxisOrder { get; set; } = [];
    public List<ChainPipelineExtractionDto> Extractions { get; set; } = [];
}

public sealed class ChainPipelineAuthDto
{
    public AuthType Type { get; set; } = AuthType.None;
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ApiKeyName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public bool ApiKeyInHeader { get; set; } = true;
}

public sealed class ChainPipelineKeyValueDto
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public sealed class ChainPipelineVaryingParamDto
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool IsVarying { get; set; }
    public long Start { get; set; } = 1;
    public long End { get; set; } = 10;
    public long Step { get; set; } = 1;
}

public sealed class ChainPipelineVariableDto
{
    public string Name { get; set; } = string.Empty;
    public string UsedIn { get; set; } = string.Empty;
    public bool IsUsed { get; set; } = true;
    public bool IsVarying { get; set; }
    public string FixedValue { get; set; } = string.Empty;
    public VariableMode Mode { get; set; } = VariableMode.Range;
    public NumberBase Base { get; set; } = NumberBase.Decimal;
    public string StartText { get; set; } = "1";
    public string EndText { get; set; } = "10";
    public string StepText { get; set; } = "1";
    public int Width { get; set; }
    public bool UppercaseHex { get; set; } = true;
    public bool UsePrefix { get; set; }
    public string ListText { get; set; } = string.Empty;
    public RandomKind RandomKind { get; set; } = RandomKind.Integer;
    public int RandomCount { get; set; } = 10;
    public bool IsUnique { get; set; }
    public string SeedText { get; set; } = string.Empty;
    public long? SeedUsed { get; set; }
    public string MinText { get; set; } = "1";
    public string MaxText { get; set; } = "100";
    public int ByteCount { get; set; } = 4;
    public int TextLength { get; set; } = 8;
    public bool IncludeLowercase { get; set; } = true;
    public bool IncludeUppercase { get; set; } = true;
    public bool IncludeDigits { get; set; } = true;
    public bool IncludeDashUnderscore { get; set; }
    public bool IsConfigured { get; set; }
}

public sealed class ChainPipelineAxisOrderDto
{
    public BatchAxisKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class ChainPipelineExtractionDto
{
    public string VariableName { get; set; } = string.Empty;
    public ExtractionKind Kind { get; set; } = ExtractionKind.JsonPath;
    public string Expression { get; set; } = string.Empty;
    public int RegexGroup { get; set; } = 1;
    public ChainExtractionSource Source { get; set; } = ChainExtractionSource.Body;
    public string HeaderName { get; set; } = string.Empty;
    public string SourceVariableName { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsList { get; set; }
    public string DefaultValueIfMissing { get; set; } = string.Empty;
    public ExtractionConditionMatchMode ConditionMatchMode { get; set; } = ExtractionConditionMatchMode.All;
    public bool DeduplicateList { get; set; }
    public List<ChainPipelineExtractionConditionDto> Conditions { get; set; } = [];
}

public sealed class ChainPipelineExtractionConditionDto
{
    public bool IsEnabled { get; set; } = true;
    public bool IsConditional { get; set; }
    public ExtractionConditionOperator Operator { get; set; } = ExtractionConditionOperator.Contains;
    public string Value { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public int RegexGroup { get; set; } = 1;
    public int SegmentIndex { get; set; }
    public bool IgnoreCase { get; set; }
    public ChainPipelineConditionalBranchDto ThenBranch { get; set; } = new();
    public ChainPipelineConditionalBranchDto ElseBranch { get; set; } = new() { Action = ExtractionConditionalAction.Exclude };
}

public sealed class ChainPipelineConditionalBranchDto
{
    public ExtractionConditionalAction Action { get; set; } = ExtractionConditionalAction.Keep;
    public string Value { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public int RegexGroup { get; set; } = 1;
    public int SegmentIndex { get; set; }
    public bool IgnoreCase { get; set; }
}

public sealed record ChainPipelineSaveResult(
    bool IsSuccess,
    bool IsCancelled,
    string? FilePath,
    string? FileName,
    string? ErrorMessage)
{
    public static ChainPipelineSaveResult Success(string? filePath, string? fileName) =>
        new(true, false, filePath, fileName, null);

    public static ChainPipelineSaveResult Cancelled() =>
        new(false, true, null, null, null);

    public static ChainPipelineSaveResult Failure(string errorMessage) =>
        new(false, false, null, null, errorMessage);
}
