using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class ChainExtractionCondition : ObservableObject
{
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresValue))]
    [NotifyPropertyChangedFor(nameof(IsRegexExtractAny))]
    [NotifyPropertyChangedFor(nameof(IsSplitPart))]
    [NotifyPropertyChangedFor(nameof(IsBetween))]
    [NotifyPropertyChangedFor(nameof(HasReplacementRow))]
    [NotifyPropertyChangedFor(nameof(ReplacementLabel))]
    [NotifyPropertyChangedFor(nameof(ReplacementPlaceholder))]
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
    public partial int SegmentIndex { get; set; }

    [ObservableProperty]
    public partial bool IgnoreCase { get; set; }

    public bool RequiresValue => Operator is not (
        ExtractionConditionOperator.NotEmpty
        or ExtractionConditionOperator.ToLower
        or ExtractionConditionOperator.ToUpper);

    public bool IsRegexExtractAny => Operator is ExtractionConditionOperator.RegexExtract
        or ExtractionConditionOperator.RegexExtractAll;

    public bool IsSplitPart => Operator == ExtractionConditionOperator.SplitPart;

    public bool IsBetween => Operator == ExtractionConditionOperator.Between;

    public bool HasReplacementRow => Operator is ExtractionConditionOperator.RegexReplace
        or ExtractionConditionOperator.ReplaceText
        or ExtractionConditionOperator.Between;

    public string ReplacementLabel => Operator switch
    {
        ExtractionConditionOperator.Between => "Jusqu'à (fin)",
        _ => "Remplacer par"
    };

    public string ReplacementPlaceholder => Operator switch
    {
        ExtractionConditionOperator.Between => "délimiteur droit (vide = jusqu'à la fin)",
        ExtractionConditionOperator.ReplaceText => "nouveau texte (vide = supprimer)",
        _ => "nouveau texte (vide = supprimer)"
    };

    public bool SupportsIgnoreCase => Operator is ExtractionConditionOperator.Contains
        or ExtractionConditionOperator.NotContains
        or ExtractionConditionOperator.StartsWith
        or ExtractionConditionOperator.EndsWith
        or ExtractionConditionOperator.Equals
        or ExtractionConditionOperator.NotEquals
        or ExtractionConditionOperator.RegexMatch
        or ExtractionConditionOperator.RegexNotMatch
        or ExtractionConditionOperator.RegexExtract
        or ExtractionConditionOperator.RegexExtractAll
        or ExtractionConditionOperator.RegexReplace
        or ExtractionConditionOperator.ReplaceText
        or ExtractionConditionOperator.Between
        or ExtractionConditionOperator.SplitPart;

    public string ValuePlaceholder => Operator switch
    {
        ExtractionConditionOperator.Contains => "texte à contenir",
        ExtractionConditionOperator.NotContains => "texte à exclure",
        ExtractionConditionOperator.StartsWith => "préfixe attendu",
        ExtractionConditionOperator.EndsWith => "suffixe attendu",
        ExtractionConditionOperator.Equals => "valeur exacte",
        ExtractionConditionOperator.NotEquals => "valeur à exclure",
        ExtractionConditionOperator.RegexMatch => "motif regex (ex. [0-9a-f]{16,})",
        ExtractionConditionOperator.RegexNotMatch => "motif regex à exclure",
        ExtractionConditionOperator.RegexExtract => "motif regex avec (...) (ex. ([0-9a-f]{16,}))",
        ExtractionConditionOperator.RegexExtractAll => "motif regex capturant chaque occurrence (ex. ([0-9a-f]{16,}))",
        ExtractionConditionOperator.RegexReplace => "motif regex à remplacer",
        ExtractionConditionOperator.ReplaceText => "texte exact à remplacer",
        ExtractionConditionOperator.Between => "délimiteur gauche (vide = depuis le début)",
        ExtractionConditionOperator.SplitPart => "séparateur (ex. / ou - ou ?)",
        ExtractionConditionOperator.Trim => "caractères à retirer (vide = espaces)",
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

    partial void OnSegmentIndexChanged(int value)
    {
        var clamped = Math.Clamp(value, -99, 99);
        if (clamped != value)
        {
            SegmentIndex = clamped;
        }
    }

    public ChainExtractionCondition Clone() => new()
    {
        IsEnabled = IsEnabled,
        Operator = Operator,
        Value = Value,
        Replacement = Replacement,
        RegexGroup = RegexGroup,
        SegmentIndex = SegmentIndex,
        IgnoreCase = IgnoreCase
    };
}
