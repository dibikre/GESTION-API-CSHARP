using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class ChainExtraction : ObservableObject
{
    public ChainExtraction()
    {
        Conditions.CollectionChanged += OnConditionsCollectionChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastExtractedDisplay))]
    [NotifyPropertyChangedFor(nameof(VariablePlaceholderHint))]
    public partial string VariableName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonPathKind))]
    [NotifyPropertyChangedFor(nameof(IsRegexKind))]
    [NotifyPropertyChangedFor(nameof(ShowRegexGroup))]
    [NotifyPropertyChangedFor(nameof(ExpressionPlaceholder))]
    public partial ExtractionKind Kind { get; set; } = ExtractionKind.JsonPath;

    [ObservableProperty]
    public partial string Expression { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int RegexGroup { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodySource))]
    [NotifyPropertyChangedFor(nameof(IsHeaderSource))]
    [NotifyPropertyChangedFor(nameof(ExpressionPlaceholder))]
    public partial ChainExtractionSource Source { get; set; } = ChainExtractionSource.Body;

    [ObservableProperty]
    public partial string HeaderName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOptional))]
    public partial bool IsRequired { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VariablePlaceholderHint))]
    [NotifyPropertyChangedFor(nameof(ExpressionPlaceholder))]
    [NotifyPropertyChangedFor(nameof(LastExtractedDisplay))]
    public partial bool IsList { get; set; }

    [ObservableProperty]
    public partial string DefaultValueIfMissing { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastExtractedValue))]
    [NotifyPropertyChangedFor(nameof(LastExtractedDisplay))]
    public partial string? LastExtractedValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastExtractedList))]
    [NotifyPropertyChangedFor(nameof(CanInspectList))]
    public partial IReadOnlyList<string>? LastExtractedList { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInspectList))]
    public partial IReadOnlyList<string>? LastRawExtractedList { get; set; }

    [ObservableProperty]
    public partial ExtractionConditionMatchMode ConditionMatchMode { get; set; } = ExtractionConditionMatchMode.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConditions))]
    [NotifyPropertyChangedFor(nameof(ConditionsCount))]
    [NotifyPropertyChangedFor(nameof(ConditionsSummaryText))]
    public partial bool DeduplicateList { get; set; }

    public ObservableCollection<ChainExtractionCondition> Conditions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    public bool IsBodySource => Source == ChainExtractionSource.Body;

    public bool IsHeaderSource => Source == ChainExtractionSource.Header;

    public bool IsJsonPathKind => Kind == ExtractionKind.JsonPath;

    public bool IsRegexKind => Kind == ExtractionKind.Regex;

    public bool ShowRegexGroup => Kind == ExtractionKind.Regex || Source == ChainExtractionSource.Header;

    public bool IsOptional => !IsRequired;

    public bool HasLastExtractedValue => LastExtractedValue is not null;

    public bool HasLastExtractedList =>
        LastExtractedList is { Count: > 0 } && !BatchExecutionHelper.IsSensitiveName(VariableName);

    public bool CanInspectList =>
        (LastExtractedList is { Count: > 0 } || (LastRawExtractedList is { Count: > 0 } && HasConditions)) &&
        !BatchExecutionHelper.IsSensitiveName(VariableName);

    public bool HasConditions => Conditions.Count > 0 || DeduplicateList;

    public int ConditionsCount => Conditions.Count + (DeduplicateList ? 1 : 0);

    public string ConditionsSummaryText => ConditionsCount switch
    {
        0 => "Conditions",
        1 => "Conditions (1)",
        _ => $"Conditions ({ConditionsCount})"
    };

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string LastExtractedDisplay => LastExtractedValue is null
        ? string.Empty
        : BatchExecutionHelper.IsSensitiveName(VariableName)
            ? $"*** ({LastExtractedValue.Length} car.)"
            : LastExtractedValue;

    public string VariablePlaceholderHint => string.IsNullOrWhiteSpace(VariableName)
        ? (IsList ? "nom_liste" : "nom_variable")
        : (IsList ? $"[[{VariableName.Trim()}]]" : $"{{{{{VariableName.Trim()}}}}}");

    public string ExpressionPlaceholder => Source == ChainExtractionSource.Header
        ? "ex. Bearer (.+)"
        : Kind == ExtractionKind.JsonPath
            ? (IsList ? "ex. items[*].id, items[n++].id ou data.tags" : "ex. data.token ou items[0].id")
            : "ex. \"token\":\"([^\"]+)\"";

    partial void OnKindChanged(ExtractionKind value)
    {
        ClearPreviewState();
    }

    partial void OnExpressionChanged(string value)
    {
        ClearPreviewState();
    }

    partial void OnHeaderNameChanged(string value)
    {
        ClearPreviewState();
    }

    partial void OnIsListChanged(bool value)
    {
        ClearPreviewState();
    }

    partial void OnSourceChanged(ChainExtractionSource value)
    {
        if (value == ChainExtractionSource.Header && Kind == ExtractionKind.JsonPath)
        {
            Kind = ExtractionKind.Regex;
        }

        ClearPreviewState();
    }

    partial void OnRegexGroupChanged(int value)
    {
        if (value < 0)
        {
            RegexGroup = 0;
        }

        ClearPreviewState();
    }

    public void NotifyConditionsChanged()
    {
        OnPropertyChanged(nameof(HasConditions));
        OnPropertyChanged(nameof(ConditionsCount));
        OnPropertyChanged(nameof(ConditionsSummaryText));
        OnPropertyChanged(nameof(CanInspectList));
    }

    private void OnConditionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyConditionsChanged();
    }

    private void ClearPreviewState()
    {
        LastExtractedValue = null;
        LastExtractedList = null;
        LastRawExtractedList = null;
        ErrorText = null;
    }

    public ChainExtraction Clone()
    {
        var clone = new ChainExtraction
        {
            VariableName = VariableName,
            Kind = Kind,
            Expression = Expression,
            RegexGroup = RegexGroup,
            Source = Source,
            HeaderName = HeaderName,
            IsRequired = IsRequired,
            IsList = IsList,
            DefaultValueIfMissing = DefaultValueIfMissing,
            ConditionMatchMode = ConditionMatchMode,
            DeduplicateList = DeduplicateList
        };

        foreach (var cond in Conditions)
        {
            clone.Conditions.Add(cond.Clone());
        }

        return clone;
    }
}
