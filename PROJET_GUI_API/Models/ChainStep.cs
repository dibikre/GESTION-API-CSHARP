using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class ChainStep : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepHeaderLabel))]
    [NotifyPropertyChangedFor(nameof(MoveUpAutomationName))]
    [NotifyPropertyChangedFor(nameof(MoveDownAutomationName))]
    public partial string Name { get; set; } = "Étape";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepHeaderLabel))]
    public partial int Rank { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHttpRequestStep))]
    [NotifyPropertyChangedFor(nameof(IsReturnStep))]
    [NotifyPropertyChangedFor(nameof(StepKindBadgeText))]
    public partial ChainStepKind StepKind { get; set; } = ChainStepKind.HttpRequest;

    [ObservableProperty]
    public partial string CurlText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReturnExpression { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasParsed { get; set; }

    [ObservableProperty]
    public partial HttpMethodType Method { get; set; } = HttpMethodType.Get;

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormBody))]
    [NotifyPropertyChangedFor(nameof(IsBodyNotNone))]
    public partial BodyType BodyType { get; set; } = BodyType.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOrderIgnored))]
    [NotifyPropertyChangedFor(nameof(ShowOrderPanel))]
    public partial BatchMode SelectedMode { get; set; } = BatchMode.Combinations;

    [ObservableProperty]
    public partial int DelayAfterMs { get; set; } = 500;

    [ObservableProperty]
    public partial bool ContinueOnError { get; set; }

    [ObservableProperty]
    public partial bool StepRateLimitUsesGlobalPolicy { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVariablesError))]
    public partial string? VariablesError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanError))]
    public partial string? PlanError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IterationsBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsLooping))]
    public partial int TotalIterations { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IterationsBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsLooping))]
    public partial bool HasDynamicIterations { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsedExtractedLists))]
    public partial string UsedExtractedListsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int UsedVariablesCount { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastResponse))]
    public partial ResponseModel? LastResponse { get; set; }

    [ObservableProperty]
    public partial string LastResponseSummary { get; set; } = string.Empty;

    public AuthConfig ParsedAuth { get; set; } = new();
    public int ParsedTimeoutSeconds { get; set; } = 30;
    public List<string> ParseWarnings { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<KeyValueItem> Headers { get; } = [];
    public ObservableCollection<KeyValueItem> FormFields { get; } = [];
    public ObservableCollection<VaryingParam> VaryingParams { get; } = [];
    public ObservableCollection<BatchVariable> Variables { get; } = [];
    public ObservableCollection<BatchAxis> Axes { get; } = [];
    public ObservableCollection<ChainExtraction> Extractions { get; } = [];

    public bool IsHttpRequestStep => StepKind == ChainStepKind.HttpRequest;
    public bool IsReturnStep => StepKind == ChainStepKind.Return;
    public string StepKindBadgeText => IsReturnStep ? "RETURN" : Method.ToString().ToUpperInvariant();
    public bool IsFormBody => BodyType == BodyType.FormUrlEncoded;
    public bool IsBodyNotNone => BodyType != BodyType.None && BodyType != BodyType.FormUrlEncoded;
    public bool HasWarnings => Warnings.Count > 0;
    public string WarningsSummaryText => Warnings.Count <= 1
        ? $"{Warnings.Count} avertissement"
        : $"{Warnings.Count} avertissements";
    public bool HasMultipleAxes => Axes.Count >= 2;
    public bool IsOrderIgnored => Axes.Count >= 2 && SelectedMode == BatchMode.Parallel;
    public bool ShowOrderPanel => Axes.Count >= 2 && SelectedMode == BatchMode.Combinations;
    public bool HasVariablesError => !string.IsNullOrWhiteSpace(VariablesError);
    public bool HasUsedExtractedLists => !string.IsNullOrWhiteSpace(UsedExtractedListsText);
    public bool HasPlanError => !string.IsNullOrWhiteSpace(PlanError);
    public bool HasLastResponse => LastResponse is not null;
    public bool IsLooping => HasDynamicIterations || TotalIterations > 1;
    public string IterationsBadgeText => HasDynamicIterations
        ? (TotalIterations > 1 ? $"x({TotalIterations}×liste)" : "x(liste)")
        : (TotalIterations <= 1 ? string.Empty : $"x{TotalIterations}");
    public string StepHeaderLabel => $"{Rank}. {(string.IsNullOrWhiteSpace(Name) ? "Sans nom" : Name.Trim())}";
    public string MoveUpAutomationName => $"Monter l'étape {Name}";
    public string MoveDownAutomationName => $"Descendre l'étape {Name}";

    public void NotifyCollectionsChanged()
    {
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(WarningsSummaryText));
        OnPropertyChanged(nameof(HasMultipleAxes));
        OnPropertyChanged(nameof(IsOrderIgnored));
        OnPropertyChanged(nameof(ShowOrderPanel));
    }

    public RequestModel BuildTemplateRequest()
    {
        return new RequestModel
        {
            Method = Method,
            Url = Url,
            QueryParams = VaryingParams
                .Select(p => new KeyValueItem(p.Name, p.Value, p.IsEnabled))
                .ToList(),
            Headers = Headers
                .Select(h => new KeyValueItem(h.Key, h.Value, h.IsEnabled))
                .ToList(),
            BodyType = BodyType,
            Body = Body,
            FormFields = FormFields
                .Select(f => new KeyValueItem(f.Key, f.Value, f.IsEnabled))
                .ToList(),
            Auth = new AuthConfig
            {
                Type = ParsedAuth.Type,
                Token = ParsedAuth.Token,
                Username = ParsedAuth.Username,
                Password = ParsedAuth.Password,
                ApiKeyName = ParsedAuth.ApiKeyName,
                ApiKeyValue = ParsedAuth.ApiKeyValue,
                ApiKeyInHeader = ParsedAuth.ApiKeyInHeader
            },
            TimeoutSeconds = ParsedTimeoutSeconds > 0 ? ParsedTimeoutSeconds : 30
        };
    }

    public ChainStep SnapshotForRun()
    {
        var copy = new ChainStep
        {
            Id = Id,
            Name = Name,
            Rank = Rank,
            StepKind = StepKind,
            CurlText = CurlText,
            ReturnExpression = ReturnExpression,
            HasParsed = HasParsed,
            Method = Method,
            Url = Url,
            Body = Body,
            BodyType = BodyType,
            SelectedMode = SelectedMode,
            DelayAfterMs = Math.Max(0, DelayAfterMs),
            ContinueOnError = ContinueOnError,
            StepRateLimitUsesGlobalPolicy = StepRateLimitUsesGlobalPolicy,
            ParsedTimeoutSeconds = ParsedTimeoutSeconds > 0 ? ParsedTimeoutSeconds : 30,
            ParsedAuth = new AuthConfig
            {
                Type = ParsedAuth.Type,
                Token = ParsedAuth.Token,
                Username = ParsedAuth.Username,
                Password = ParsedAuth.Password,
                ApiKeyName = ParsedAuth.ApiKeyName,
                ApiKeyValue = ParsedAuth.ApiKeyValue,
                ApiKeyInHeader = ParsedAuth.ApiKeyInHeader
            }
        };

        foreach (var h in Headers)
        {
            copy.Headers.Add(new KeyValueItem(h.Key, h.Value, h.IsEnabled));
        }

        foreach (var f in FormFields)
        {
            copy.FormFields.Add(new KeyValueItem(f.Key, f.Value, f.IsEnabled));
        }

        var paramMap = new Dictionary<VaryingParam, VaryingParam>();
        foreach (var p in VaryingParams)
        {
            var clonedParam = new VaryingParam
            {
                Name = p.Name,
                Value = p.Value,
                IsEnabled = p.IsEnabled,
                IsVarying = p.IsVarying,
                Start = p.Start,
                End = p.End,
                Step = p.Step
            };
            paramMap[p] = clonedParam;
            copy.VaryingParams.Add(clonedParam);
        }

        var varMap = new Dictionary<BatchVariable, BatchVariable>();
        foreach (var v in Variables)
        {
            var clonedVar = v.Clone();
            varMap[v] = clonedVar;
            copy.Variables.Add(clonedVar);
        }

        foreach (var axis in Axes)
        {
            var snapParam = axis.SourceParam is not null && paramMap.TryGetValue(axis.SourceParam, out var sp) ? sp : null;
            var snapVar = axis.SourceVariable is not null && varMap.TryGetValue(axis.SourceVariable, out var sv) ? sv : null;
            copy.Axes.Add(axis.CloneWithSources(snapParam, snapVar));
        }

        foreach (var ext in Extractions)
        {
            copy.Extractions.Add(ext.Clone());
        }

        return copy;
    }
}
