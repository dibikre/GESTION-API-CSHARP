using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class ExtractionConditionsViewModel : ViewModelBase
{
    public static IReadOnlyList<ExtractionConditionOperator> OperatorOptions { get; } =
        Enum.GetValues<ExtractionConditionOperator>();

    public static IReadOnlyList<ExtractionConditionOperator> FilterOperatorOptions { get; } =
        Enum.GetValues<ExtractionConditionOperator>()
            .Where(ChainExtractionCondition.IsFilterOperator)
            .ToArray();

    public static IReadOnlyList<ExtractionConditionalAction> ConditionalActionOptions { get; } =
        Enum.GetValues<ExtractionConditionalAction>();

    public static IReadOnlyList<ExtractionConditionMatchMode> MatchModeOptions { get; } =
        Enum.GetValues<ExtractionConditionMatchMode>();

    public static IReadOnlyList<ExtractionConditionPreset> PresetOptions =>
        ExtractionPresetCatalog.Presets;

    private readonly IReadOnlyList<string> _rawItems;
    private readonly Action<IReadOnlyList<ChainExtractionCondition>, ExtractionConditionMatchMode, bool> _onApply;
    private readonly string _initialSnapshotSignature;
    private bool _isSyncing;

    public ExtractionConditionsViewModel(ExtractionConditionsOverlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        VariableName = string.IsNullOrWhiteSpace(request.VariableName) ? "liste" : request.VariableName.Trim();
        _rawItems = request.RawItems ?? [];
        _onApply = request.OnApply ?? throw new ArgumentNullException(nameof(request));

        Conditions.CollectionChanged += OnConditionsCollectionChanged;

        _isSyncing = true;
        try
        {
            MatchMode = request.InitialMatchMode;
            DeduplicateList = request.InitialDeduplicate;
            foreach (var cond in request.InitialConditions)
            {
                AddTrackedCondition(cond.Clone());
            }

            ManualText = ExtractionConditionSyntax.FormatManual(
                Conditions,
                MatchMode,
                DeduplicateList,
                includeExamplesWhenEmpty: true);
        }
        finally
        {
            _isSyncing = false;
        }

        _initialSnapshotSignature = BuildSignature(Conditions, MatchMode, DeduplicateList);
        RecalculatePreview();
    }

    public event Action? PreviewUpdated;

    public string VariableName { get; }

    public int RawCount => _rawItems.Count;

    public ObservableCollection<ChainExtractionCondition> Conditions { get; } = [];

    [ObservableProperty]
    public partial ExtractionConditionPreset? SelectedPreset { get; set; } = ExtractionPresetCatalog.Presets[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectionSuggestion))]
    [NotifyPropertyChangedFor(nameof(SelectionSummaryText))]
    public partial ExtractionSelectionSuggestion? SelectionSuggestion { get; private set; }

    public bool HasSelectionSuggestion => SelectionSuggestion is not null;

    public string SelectionSummaryText => SelectionSuggestion is null
        ? string.Empty
        : $"Sélection : « {TruncatePreview(SelectionSuggestion.SelectedText, 26)} » ({SelectionSuggestion.DetectedKindLabel})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuidedMode))]
    public partial bool IsManualMode { get; set; }

    public bool IsGuidedMode => !IsManualMode;

    [ObservableProperty]
    public partial ExtractionConditionMatchMode MatchMode { get; set; } = ExtractionConditionMatchMode.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveConditionsCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveConditionsBadge))]
    public partial bool DeduplicateList { get; set; }

    [ObservableProperty]
    public partial string ManualText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualError))]
    [NotifyPropertyChangedFor(nameof(HasBlockingError))]
    public partial string? ManualError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterError))]
    [NotifyPropertyChangedFor(nameof(HasBlockingError))]
    public partial string? FilterError { get; set; }

    [ObservableProperty]
    public partial string FilterSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilteredText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int FilteredCount { get; set; }

    [ObservableProperty]
    public partial bool HasChanges { get; set; }

    public bool HasManualError => !string.IsNullOrWhiteSpace(ManualError);

    public bool HasFilterError => !string.IsNullOrWhiteSpace(FilterError);

    public bool HasBlockingError => HasManualError || HasFilterError;

    public int ActiveConditionsCount => Conditions.Count(c => c.IsEnabled) + (DeduplicateList ? 1 : 0);

    public bool HasActiveConditionsBadge => ActiveConditionsCount > 0;

    public void UpdateSourceSelection(string? fullText, int selectionStart, int selectionEnd)
    {
        SelectionSuggestion = ExtractionPresetCatalog.AnalyzeSelection(fullText, selectionStart, selectionEnd);
    }

    partial void OnMatchModeChanged(ExtractionConditionMatchMode value)
    {
        if (!_isSyncing)
        {
            RecalculatePreview();
        }
    }

    partial void OnDeduplicateListChanged(bool value)
    {
        OnPropertyChanged(nameof(ActiveConditionsCount));
        OnPropertyChanged(nameof(HasActiveConditionsBadge));
        if (!_isSyncing)
        {
            RecalculatePreview();
        }
    }

    partial void OnManualTextChanged(string value)
    {
        if (_isSyncing || !IsManualMode)
        {
            return;
        }

        var parsed = ExtractionConditionSyntax.ParseManual(value);
        if (!parsed.IsSuccess)
        {
            ManualError = parsed.ErrorMessage;
            HasChanges = true;
            PreviewUpdated?.Invoke();
            return;
        }

        ManualError = null;
        _isSyncing = true;
        try
        {
            ClearTrackedConditions();
            foreach (var cond in parsed.Conditions)
            {
                AddTrackedCondition(cond);
            }
            MatchMode = parsed.MatchMode;
            DeduplicateList = parsed.Deduplicate;
        }
        finally
        {
            _isSyncing = false;
        }

        RecalculatePreview();
    }

    [RelayCommand]
    private void SwitchToGuided()
    {
        if (!IsManualMode)
        {
            return;
        }

        var parsed = ExtractionConditionSyntax.ParseManual(ManualText);
        if (!parsed.IsSuccess)
        {
            ManualError = parsed.ErrorMessage;
            return;
        }

        ManualError = null;
        _isSyncing = true;
        try
        {
            ClearTrackedConditions();
            foreach (var cond in parsed.Conditions)
            {
                AddTrackedCondition(cond);
            }
            MatchMode = parsed.MatchMode;
            DeduplicateList = parsed.Deduplicate;
        }
        finally
        {
            _isSyncing = false;
        }

        IsManualMode = false;
        RecalculatePreview();
    }

    [RelayCommand]
    private void SwitchToManual()
    {
        if (IsManualMode)
        {
            return;
        }

        SyncManualTextFromRules();
        IsManualMode = true;
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset is null)
        {
            return;
        }

        AddOrReplaceBlankRule(SelectedPreset.CreateCondition());
    }

    [RelayCommand]
    private void ApplySelectionType()
    {
        if (SelectionSuggestion is null)
        {
            return;
        }

        AddOrReplaceBlankRule(SelectionSuggestion.BuildTypeCondition());
    }

    [RelayCommand]
    private void ApplySelectionContext()
    {
        if (SelectionSuggestion is null)
        {
            return;
        }

        AddOrReplaceBlankRule(SelectionSuggestion.BuildContextCondition());
    }

    [RelayCommand]
    private void ApplySelectionContains()
    {
        if (SelectionSuggestion is null)
        {
            return;
        }

        AddOrReplaceBlankRule(SelectionSuggestion.BuildContainsCondition());
    }

    [RelayCommand]
    private void AddCondition()
    {
        AddTrackedCondition(new ChainExtractionCondition());
        RecalculatePreview();
    }

    [RelayCommand]
    private void AddConditionalRule()
    {
        var cond = new ChainExtractionCondition
        {
            IsConditional = true,
            Operator = ExtractionConditionOperator.Contains
        };
        cond.ReplaceBranches(
            new ChainConditionalBranch { Action = ExtractionConditionalAction.Keep },
            new ChainConditionalBranch { Action = ExtractionConditionalAction.Exclude });
        AddTrackedCondition(cond);
        RecalculatePreview();
    }

    [RelayCommand]
    private void ToggleConditionalRule(ChainExtractionCondition? condition)
    {
        if (condition is null)
        {
            return;
        }

        condition.IsConditional = !condition.IsConditional;
    }

    [RelayCommand]
    private void RemoveCondition(ChainExtractionCondition? condition)
    {
        if (condition is null)
        {
            return;
        }

        condition.PropertyChanged -= OnConditionItemPropertyChanged;
        Conditions.Remove(condition);
        RecalculatePreview();
    }

    [RelayCommand]
    private void MoveConditionUp(ChainExtractionCondition? condition)
    {
        if (condition is null)
        {
            return;
        }

        var idx = Conditions.IndexOf(condition);
        if (idx > 0)
        {
            Conditions.Move(idx, idx - 1);
            RecalculatePreview();
        }
    }

    [RelayCommand]
    private void MoveConditionDown(ChainExtractionCondition? condition)
    {
        if (condition is null)
        {
            return;
        }

        var idx = Conditions.IndexOf(condition);
        if (idx >= 0 && idx < Conditions.Count - 1)
        {
            Conditions.Move(idx, idx + 1);
            RecalculatePreview();
        }
    }

    [RelayCommand]
    private void ClearConditions()
    {
        _isSyncing = true;
        try
        {
            ClearTrackedConditions();
            MatchMode = ExtractionConditionMatchMode.All;
            DeduplicateList = false;
            if (IsManualMode)
            {
                ManualText = ExtractionConditionSyntax.FormatManual(
                    Conditions,
                    MatchMode,
                    DeduplicateList,
                    includeExamplesWhenEmpty: true);
                ManualError = null;
            }
        }
        finally
        {
            _isSyncing = false;
        }

        RecalculatePreview();
    }

    public bool TryApply()
    {
        if (IsManualMode)
        {
            var parsed = ExtractionConditionSyntax.ParseManual(ManualText);
            if (!parsed.IsSuccess)
            {
                ManualError = parsed.ErrorMessage;
                return false;
            }

            ManualError = null;
            _isSyncing = true;
            try
            {
                ClearTrackedConditions();
                foreach (var cond in parsed.Conditions)
                {
                    AddTrackedCondition(cond);
                }
                MatchMode = parsed.MatchMode;
                DeduplicateList = parsed.Deduplicate;
            }
            finally
            {
                _isSyncing = false;
            }
        }

        var validation = ExtractionConditionEngine.ApplyToList(
            _rawItems,
            Conditions,
            MatchMode,
            DeduplicateList,
            failWhenEmpty: false);
        if (!validation.IsSuccess)
        {
            FilterError = validation.ErrorMessage;
            return false;
        }

        var cloned = Conditions.Select(c => c.Clone()).ToList();
        _onApply(cloned, MatchMode, DeduplicateList);
        return true;
    }

    private void AddOrReplaceBlankRule(ChainExtractionCondition condition)
    {
        _isSyncing = true;
        try
        {
            if (Conditions.Count == 1 &&
                Conditions[0].RequiresValue &&
                string.IsNullOrWhiteSpace(Conditions[0].Value) &&
                string.IsNullOrWhiteSpace(Conditions[0].Replacement))
            {
                ClearTrackedConditions();
            }

            AddTrackedCondition(condition);
            if (IsManualMode)
            {
                ManualText = ExtractionConditionSyntax.FormatManual(
                    Conditions,
                    MatchMode,
                    DeduplicateList,
                    includeExamplesWhenEmpty: true);
                ManualError = null;
            }
        }
        finally
        {
            _isSyncing = false;
        }

        RecalculatePreview();
    }

    private void SyncManualTextFromRules()
    {
        _isSyncing = true;
        try
        {
            ManualText = ExtractionConditionSyntax.FormatManual(
                Conditions,
                MatchMode,
                DeduplicateList,
                includeExamplesWhenEmpty: true);
            ManualError = null;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void AddTrackedCondition(ChainExtractionCondition condition)
    {
        condition.PropertyChanged += OnConditionItemPropertyChanged;
        Conditions.Add(condition);
    }

    private void ClearTrackedConditions()
    {
        foreach (var existing in Conditions)
        {
            existing.PropertyChanged -= OnConditionItemPropertyChanged;
        }
        Conditions.Clear();
    }

    private void OnConditionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ActiveConditionsCount));
        OnPropertyChanged(nameof(HasActiveConditionsBadge));
    }

    private void OnConditionItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSyncing)
        {
            return;
        }

        OnPropertyChanged(nameof(ActiveConditionsCount));
        OnPropertyChanged(nameof(HasActiveConditionsBadge));
        RecalculatePreview();
    }

    private void RecalculatePreview()
    {
        OnPropertyChanged(nameof(ActiveConditionsCount));
        OnPropertyChanged(nameof(HasActiveConditionsBadge));

        var result = ExtractionConditionEngine.ApplyToList(
            _rawItems,
            Conditions,
            MatchMode,
            DeduplicateList,
            failWhenEmpty: false);

        if (!result.IsSuccess)
        {
            FilterError = result.ErrorMessage;
            FilteredText = string.Join(Environment.NewLine, _rawItems);
            FilteredCount = _rawItems.Count;
            FilterSummaryText = $"{_rawItems.Count} élément(s) brut(s) — condition invalide";
        }
        else
        {
            FilterError = null;
            FilteredCount = result.Values.Count;
            FilteredText = string.Join(Environment.NewLine, result.Values);
            FilterSummaryText = ActiveConditionsCount == 0
                ? $"{_rawItems.Count} élément(s) extrait(s)"
                : $"{FilteredCount} élément(s) conservé(s) sur {_rawItems.Count} brut(s)";
        }

        HasChanges = BuildSignature(Conditions, MatchMode, DeduplicateList) != _initialSnapshotSignature;
        PreviewUpdated?.Invoke();
    }

    private static string BuildSignature(
        IReadOnlyList<ChainExtractionCondition> conditions,
        ExtractionConditionMatchMode mode,
        bool dedup)
    {
        return string.Join("|",
            mode.ToString(),
            dedup ? "1" : "0",
            string.Join(";", conditions.Select(c =>
                $"{(c.IsEnabled ? '1' : '0')}:{(c.IsConditional ? '1' : '0')}:{(int)c.Operator}:{c.Value}:{c.Replacement}:{c.RegexGroup}:{c.SegmentIndex}:{(c.IgnoreCase ? '1' : '0')}|{(int)c.ThenBranch.Action}:{c.ThenBranch.Value}:{c.ThenBranch.Replacement}:{c.ThenBranch.RegexGroup}:{c.ThenBranch.SegmentIndex}:{(c.ThenBranch.IgnoreCase ? '1' : '0')}|{(int)c.ElseBranch.Action}:{c.ElseBranch.Value}:{c.ElseBranch.Replacement}:{c.ElseBranch.RegexGroup}:{c.ElseBranch.SegmentIndex}:{(c.ElseBranch.IgnoreCase ? '1' : '0')}")));
    }

    private static string TruncatePreview(string text, int maxLen) =>
        text.Length <= maxLen ? text : string.Concat(text.AsSpan(0, maxLen), "...");
}
