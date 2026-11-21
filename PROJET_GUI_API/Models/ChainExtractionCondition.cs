using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class ChainExtractionCondition : ObservableObject
{
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresValue))]
    [NotifyPropertyChangedFor(nameof(IsRegexExtract))]
    [NotifyPropertyChangedFor(nameof(IsRegexReplace))]
    [NotifyPropertyChangedFor(nameof(SupportsIgnoreCase))]
    [NotifyPropertyChangedFor(nameof(ValuePlaceholder))]
    public partial ExtractionConditionOperator Operator { get; set; } = ExtractionConditionOperator.Contains;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Replacement { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int RegexGroup { get; set; } = 1;

    [ObservableProperty]
    public partial bool IgnoreCase { get; set; }

    public bool RequiresValue => Operator != ExtractionConditionOperator.NotEmpty;

    public bool IsRegexExtract => Operator == ExtractionConditionOperator.RegexExtract;

    public bool IsRegexReplace => Operator == ExtractionConditionOperator.RegexReplace;

    public bool SupportsIgnoreCase => Operator is ExtractionConditionOperator.Contains
        or ExtractionConditionOperator.NotContains
        or ExtractionConditionOperator.StartsWith
        or ExtractionConditionOperator.EndsWith
        or ExtractionConditionOperator.Equals
        or ExtractionConditionOperator.NotEquals
        or ExtractionConditionOperator.RegexMatch
        or ExtractionConditionOperator.RegexNotMatch
        or ExtractionConditionOperator.RegexExtract
        or ExtractionConditionOperator.RegexReplace;

    public string ValuePlaceholder => Operator switch
    {
        ExtractionConditionOperator.Contains => "texte à contenir",
        ExtractionConditionOperator.NotContains => "texte à exclure",
        ExtractionConditionOperator.StartsWith => "préfixe attendu",
        ExtractionConditionOperator.EndsWith => "suffixe attendu",
        ExtractionConditionOperator.Equals => "valeur exacte",
        ExtractionConditionOperator.NotEquals => "valeur à exclure",
        ExtractionConditionOperator.RegexMatch => "motif regex (ex. ^https://.*\\.m3u8$)",
        ExtractionConditionOperator.RegexNotMatch => "motif regex à exclure",
        ExtractionConditionOperator.RegexExtract => "motif regex avec capture (ex. /hls/(\\d+)/)",
        ExtractionConditionOperator.RegexReplace => "motif regex à remplacer",
        ExtractionConditionOperator.LengthMin => "longueur minimale (ex. 5)",
        ExtractionConditionOperator.LengthMax => "longueur maximale (ex. 200)",
        ExtractionConditionOperator.GreaterThan => "seuil numérique (ex. 10)",
        ExtractionConditionOperator.LessThan => "seuil numérique (ex. 100)",
        _ => string.Empty
    };

    partial void OnRegexGroupChanged(int value)
    {
        if (value < 0)
        {
            RegexGroup = 0;
        }
    }

    public ChainExtractionCondition Clone() => new()
    {
        IsEnabled = IsEnabled,
        Operator = Operator,
        Value = Value,
        Replacement = Replacement,
        RegexGroup = RegexGroup,
        IgnoreCase = IgnoreCase
    };
}
