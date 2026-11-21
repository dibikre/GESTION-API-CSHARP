using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class ChainConditionalBranch : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresValue))]
    [NotifyPropertyChangedFor(nameof(HasReplacementRow))]
    [NotifyPropertyChangedFor(nameof(IsRegexExtract))]
    [NotifyPropertyChangedFor(nameof(IsSplitPart))]
    [NotifyPropertyChangedFor(nameof(SupportsIgnoreCase))]
    [NotifyPropertyChangedFor(nameof(ValuePlaceholder))]
    [NotifyPropertyChangedFor(nameof(ReplacementLabel))]
    [NotifyPropertyChangedFor(nameof(ReplacementPlaceholder))]
    public partial ExtractionConditionalAction Action { get; set; } = ExtractionConditionalAction.Keep;

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

    public bool RequiresValue => Action is not (
        ExtractionConditionalAction.Keep
        or ExtractionConditionalAction.Exclude
        or ExtractionConditionalAction.ToLower
        or ExtractionConditionalAction.ToUpper);

    public bool HasReplacementRow => Action is ExtractionConditionalAction.RegexReplace
        or ExtractionConditionalAction.ReplaceText
        or ExtractionConditionalAction.Between;

    public bool IsRegexExtract => Action is ExtractionConditionalAction.RegexExtract
        or ExtractionConditionalAction.RegexExtractAll;

    public bool IsSplitPart => Action == ExtractionConditionalAction.SplitPart;

    public bool SupportsIgnoreCase => Action is ExtractionConditionalAction.ReplaceText
        or ExtractionConditionalAction.RegexExtract
        or ExtractionConditionalAction.RegexExtractAll
        or ExtractionConditionalAction.RegexReplace
        or ExtractionConditionalAction.Between
        or ExtractionConditionalAction.SplitPart;

    public string ValuePlaceholder => Action switch
    {
        ExtractionConditionalAction.SetValue => "nouvelle valeur (ex. N/A ou vide)",
        ExtractionConditionalAction.PrependText => "texte à ajouter au début (ex. https://)",
        ExtractionConditionalAction.AppendText => "texte à ajouter à la fin (ex. .json)",
        ExtractionConditionalAction.ReplaceText => "texte exact à remplacer",
        ExtractionConditionalAction.RegexExtract => "motif regex avec (...) (ex. ([0-9a-f]{16,}))",
        ExtractionConditionalAction.RegexExtractAll => "motif regex capturant chaque occurrence",
        ExtractionConditionalAction.RegexReplace => "motif regex à remplacer",
        ExtractionConditionalAction.Between => "délimiteur gauche (vide = depuis le début)",
        ExtractionConditionalAction.SplitPart => "séparateur (ex. / ou - ou ?)",
        ExtractionConditionalAction.Trim => "caractères à retirer (vide = espaces)",
        _ => string.Empty
    };

    public string ReplacementLabel => Action == ExtractionConditionalAction.Between
        ? "Jusqu'à (fin)"
        : "Remplacer par";

    public string ReplacementPlaceholder => Action == ExtractionConditionalAction.Between
        ? "délimiteur droit (vide = jusqu'à la fin)"
        : "nouveau texte (vide = supprimer)";

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

    public ChainConditionalBranch Clone() => new()
    {
        Action = Action,
        Value = Value,
        Replacement = Replacement,
        RegexGroup = RegexGroup,
        SegmentIndex = SegmentIndex,
        IgnoreCase = IgnoreCase
    };
}
