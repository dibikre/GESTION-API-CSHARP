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
    [NotifyPropertyChangedFor(nameof(IsFullResponseKind))]
    [NotifyPropertyChangedFor(nameof(RequiresExpression))]
    [NotifyPropertyChangedFor(nameof(CanBeList))]
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
    [NotifyPropertyChangedFor(nameof(IsVariableSource))]
    [NotifyPropertyChangedFor(nameof(CanSelectKind))]
    [NotifyPropertyChangedFor(nameof(RequiresExpression))]
    [NotifyPropertyChangedFor(nameof(ExpressionPlaceholder))]
    public partial ChainExtractionSource Source { get; set; } = ChainExtractionSource.Body;

    [ObservableProperty]
    public partial string HeaderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceVariableName { get; set; } = string.Empty;

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

    public bool IsVariableSource => Source == ChainExtractionSource.Variable;

    public bool CanSelectKind => Source != ChainExtractionSource.Header;

    public bool IsJsonPathKind => Kind == ExtractionKind.JsonPath;

    public bool IsRegexKind => Kind == ExtractionKind.Regex;

    public bool IsFullResponseKind => Kind == ExtractionKind.FullResponse;

    public bool RequiresExpression => Kind != ExtractionKind.FullResponse || Source == ChainExtractionSource.Header;

    public bool CanBeList => Kind != ExtractionKind.FullResponse;

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

    public string LastExtractedDisplay
    {
        get
        {
            if (LastExtractedValue is null)
            {
                return string.Empty;
            }

            if (BatchExecutionHelper.IsSensitiveName(VariableName))
            {
                return $"*** ({LastExtractedValue.Length} car.)";
            }

            var collapsed = LastExtractedValue
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            return BatchExecutionHelper.TruncateForDisplay(collapsed, 220);
        }
    }

    public string VariablePlaceholderHint => string.IsNullOrWhiteSpace(VariableName)
        ? (IsList ? "nom_liste" : "nom_variable")
        : (IsList ? $"[[{VariableName.Trim()}]]" : $"{{{{{VariableName.Trim()}}}}}");

    public string ExpressionPlaceholder => Source switch
    {
        ChainExtractionSource.Header => "ex. Bearer (.+)",
        ChainExtractionSource.Variable => Kind switch
        {
            ExtractionKind.FullResponse => "(copier la valeur de la variable source)",
            ExtractionKind.JsonPath => IsList
                ? "ex. items[*].id ou __DEFAULT_SCOPE__[\"webapp.app-context\"]"
                : "ex. __DEFAULT_SCOPE__[\"webapp.app-context\"].language",
            _ => "ex. \"language\":\"([^\"]+)\""
        },
        _ => Kind == ExtractionKind.FullResponse
            ? "(réponse complète — réutilisable via {{var.chemin}})"
            : Kind == ExtractionKind.JsonPath
                ? (IsList ? "ex. items[*].id, items[n++].id ou data.tags" : "ex. data.token ou items[0].id")
                : "ex. \"token\":\"([^\"]+)\""
    };

    partial void OnKindChanged(ExtractionKind value)
    {
        if (value == ExtractionKind.FullResponse && IsList)
        {
            IsList = false;
        }

        ClearPreviewState();
    }

    partial void OnExpressionChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) && Kind != ExtractionKind.FullResponse)
        {
            ClearPreviewState();
        }
    }

    partial void OnHeaderNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) && Source == ChainExtractionSource.Header)
        {
            ClearPreviewState();
        }
    }

    partial void OnSourceVariableNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) && Source == ChainExtractionSource.Variable)
        {
            ClearPreviewState();
        }
    }

    partial void OnIsListChanged(bool value)
    {
        ClearPreviewState();
    }

    partial void OnSourceChanged(ChainExtractionSource value)
    {
        if (value == ChainExtractionSource.Header && Kind != ExtractionKind.Regex)
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

    internal void ClearPreviewState()
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
            SourceVariableName = SourceVariableName,
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
