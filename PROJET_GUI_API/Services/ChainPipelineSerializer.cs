using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApiTester.Models;

namespace ApiTester.Services;

public static class ChainPipelineSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static ChainPipelineDocument CreateDocument(
        IReadOnlyList<ChainStep> steps,
        bool keepBodies,
        ChainCliLanguage selectedCliLanguage,
        RateLimitPolicy globalRateLimitPolicy,
        BatchSaveOptions? saveOptions = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(globalRateLimitPolicy);

        var document = new ChainPipelineDocument
        {
            Format = ChainPipelineDocument.CurrentFormat,
            Version = ChainPipelineDocument.CurrentVersion,
            SavedAtUtc = DateTimeOffset.UtcNow,
            KeepBodies = keepBodies,
            SelectedCliLanguage = selectedCliLanguage,
            GlobalRateLimitPolicy = MapRateLimitPolicy(globalRateLimitPolicy),
            SaveOptions = MapSaveOptions(saveOptions)
        };

        for (var i = 0; i < steps.Count; i++)
        {
            document.Steps.Add(MapStep(steps[i], i + 1));
        }

        return document;
    }

    public static string Serialize(ChainPipelineDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static ChainPipelineRateLimitPolicyDto MapRateLimitPolicy(RateLimitPolicy policy) => new()
    {
        IsEnabled = policy.IsEnabled,
        RespectRetryAfter = policy.RespectRetryAfter,
        MaxRetryAfterSeconds = policy.MaxRetryAfterSeconds,
        EnableBackoff = policy.EnableBackoff,
        BackoffInitialMs = policy.BackoffInitialMs,
        BackoffMultiplier = policy.BackoffMultiplier,
        BackoffMaxMs = policy.BackoffMaxMs,
        MaxRetriesPerRequest = policy.MaxRetriesPerRequest,
        ExtraStatusCodes = [.. policy.ExtraStatusCodes],
        QuotaRemainingHeader = policy.QuotaRemainingHeader ?? string.Empty,
        QuotaResetHeader = policy.QuotaResetHeader ?? string.Empty,
        QuotaThreshold = policy.QuotaThreshold,
        PauseFloorMs = policy.PauseFloorMs
    };

    private static ChainPipelineSaveOptionsDto MapSaveOptions(BatchSaveOptions? options)
    {
        if (options is null)
        {
            return new ChainPipelineSaveOptionsDto();
        }

        return new ChainPipelineSaveOptionsDto
        {
            SaveMode = options.SaveMode,
            OutputFolder = options.OutputFolder ?? string.Empty,
            GroupedFormat = options.GroupedFormat,
            CreateTimestampSubfolder = options.CreateTimestampSubfolder,
            IncludeSummaryCsv = options.IncludeSummaryCsv
        };
    }

    private static ChainPipelineStepDto MapStep(ChainStep step, int rank) => new()
    {
        Id = step.Id,
        Name = step.Name ?? string.Empty,
        Rank = rank,
        CurlText = step.CurlText ?? string.Empty,
        HasParsed = step.HasParsed,
        Method = step.Method,
        Url = step.Url ?? string.Empty,
        Body = step.Body ?? string.Empty,
        BodyType = step.BodyType,
        ReturnExpression = step.ReturnExpression ?? string.Empty,
        SelectedMode = step.SelectedMode,
        DelayAfterMs = Math.Max(0, step.DelayAfterMs),
        ContinueOnError = step.ContinueOnError,
        StepRateLimitUsesGlobalPolicy = step.StepRateLimitUsesGlobalPolicy,
        ParsedTimeoutSeconds = step.ParsedTimeoutSeconds > 0 ? step.ParsedTimeoutSeconds : 30,
        ParsedAuth = new ChainPipelineAuthDto
        {
            Type = step.ParsedAuth.Type,
            Token = step.ParsedAuth.Token ?? string.Empty,
            Username = step.ParsedAuth.Username ?? string.Empty,
            Password = step.ParsedAuth.Password ?? string.Empty,
            ApiKeyName = step.ParsedAuth.ApiKeyName ?? string.Empty,
            ApiKeyValue = step.ParsedAuth.ApiKeyValue ?? string.Empty,
            ApiKeyInHeader = step.ParsedAuth.ApiKeyInHeader
        },
        ParseWarnings = [.. step.ParseWarnings],
        Headers = step.Headers
            .Select(h => new ChainPipelineKeyValueDto
            {
                Key = h.Key ?? string.Empty,
                Value = h.Value ?? string.Empty,
                IsEnabled = h.IsEnabled
            })
            .ToList(),
        FormFields = step.FormFields
            .Select(f => new ChainPipelineKeyValueDto
            {
                Key = f.Key ?? string.Empty,
                Value = f.Value ?? string.Empty,
                IsEnabled = f.IsEnabled
            })
            .ToList(),
        VaryingParams = step.VaryingParams
            .Select(p => new ChainPipelineVaryingParamDto
            {
                Name = p.Name ?? string.Empty,
                Value = p.Value ?? string.Empty,
                IsEnabled = p.IsEnabled,
                IsVarying = p.IsVarying,
                Start = p.Start,
                End = p.End,
                Step = p.Step
            })
            .ToList(),
        Variables = step.Variables
            .Select(v => new ChainPipelineVariableDto
            {
                Name = v.Name ?? string.Empty,
                UsedIn = v.UsedIn ?? string.Empty,
                IsUsed = v.IsUsed,
                IsVarying = v.IsVarying,
                FixedValue = v.FixedValue ?? string.Empty,
                Mode = v.Mode,
                Base = v.Base,
                StartText = v.StartText ?? string.Empty,
                EndText = v.EndText ?? string.Empty,
                StepText = v.StepText ?? string.Empty,
                Width = v.Width,
                UppercaseHex = v.UppercaseHex,
                UsePrefix = v.UsePrefix,
                ListText = v.ListText ?? string.Empty,
                RandomKind = v.RandomKind,
                RandomCount = v.RandomCount,
                IsUnique = v.IsUnique,
                SeedText = v.SeedText ?? string.Empty,
                SeedUsed = v.SeedUsed,
                MinText = v.MinText ?? string.Empty,
                MaxText = v.MaxText ?? string.Empty,
                ByteCount = v.ByteCount,
                TextLength = v.TextLength,
                IncludeLowercase = v.IncludeLowercase,
                IncludeUppercase = v.IncludeUppercase,
                IncludeDigits = v.IncludeDigits,
                IncludeDashUnderscore = v.IncludeDashUnderscore,
                IsConfigured = v.IsConfigured
            })
            .ToList(),
        AxisOrder = step.Axes
            .Select(a => new ChainPipelineAxisOrderDto
            {
                Kind = a.Kind,
                Name = a.Name ?? string.Empty
            })
            .ToList(),
        Extractions = step.Extractions
            .Select(MapExtraction)
            .ToList()
    };

    private static ChainPipelineExtractionDto MapExtraction(ChainExtraction extraction) => new()
    {
        VariableName = extraction.VariableName ?? string.Empty,
        Kind = extraction.Kind,
        Expression = extraction.Expression ?? string.Empty,
        RegexGroup = extraction.RegexGroup,
        Source = extraction.Source,
        HeaderName = extraction.HeaderName ?? string.Empty,
        SourceVariableName = extraction.SourceVariableName ?? string.Empty,
        IsRequired = extraction.IsRequired,
        IsList = extraction.IsList,
        DefaultValueIfMissing = extraction.DefaultValueIfMissing ?? string.Empty,
        ConditionMatchMode = extraction.ConditionMatchMode,
        DeduplicateList = extraction.DeduplicateList,
        Conditions = extraction.Conditions
            .Select(c => new ChainPipelineExtractionConditionDto
            {
                IsEnabled = c.IsEnabled,
                IsConditional = c.IsConditional,
                Operator = c.Operator,
                Value = c.Value ?? string.Empty,
                Replacement = c.Replacement ?? string.Empty,
                RegexGroup = c.RegexGroup,
                SegmentIndex = c.SegmentIndex,
                IgnoreCase = c.IgnoreCase,
                ThenBranch = MapBranch(c.ThenBranch),
                ElseBranch = MapBranch(c.ElseBranch)
            })
            .ToList()
    };

    private static ChainPipelineConditionalBranchDto MapBranch(ChainConditionalBranch branch) => new()
    {
        Action = branch.Action,
        Value = branch.Value ?? string.Empty,
        Replacement = branch.Replacement ?? string.Empty,
        RegexGroup = branch.RegexGroup,
        SegmentIndex = branch.SegmentIndex,
        IgnoreCase = branch.IgnoreCase
    };
}
