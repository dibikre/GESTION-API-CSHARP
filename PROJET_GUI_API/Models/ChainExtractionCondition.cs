using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class ChainExtractionCondition : ObservableObject
{
    public ChainExtractionCondition()
    {
        ThenBranch = new ChainConditionalBranch { Action = ExtractionConditionalAction.Keep };
        ElseBranch = new ChainConditionalBranch { Action = ExtractionConditionalAction.Exclude };
        HookBranchEvents(ThenBranch);
        HookBranchEvents(ElseBranch);
    }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSimpleRule))]
    [NotifyPropertyChangedFor(nameof(RequiresValue))]
    [NotifyPropertyChangedFor(nameof(IsRegexExtractAny))]
    [NotifyPropertyChangedFor(nameof(IsSplitPart))]
    [NotifyPropertyChangedFor(nameof(HasReplacementRow))]
    [NotifyPropertyChangedFor(nameof(SupportsIgnoreCase))]
    [NotifyPropertyChangedFor(nameof(ValuePlaceholder))]
    public partial bool IsConditional { get; set; }

    public bool IsSimpleRule => !IsConditional;

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

    public ChainConditionalBranch ThenBranch { get; private set; }

    public ChainConditionalBranch ElseBranch { get; private set; }

    public bool RequiresValue => Operator is not (
        ExtractionConditionOperator.IsEmpty
        or ExtractionConditionOperator.NotEmpty
        or ExtractionConditionOperator.ToLower
        or ExtractionConditionOperator.ToUpper);

    public bool IsRegexExtractAny => !IsConditional && Operator is ExtractionConditionOperator.RegexExtract
        or ExtractionConditionOperator.RegexExtractAll;

    public bool IsSplitPart => !IsConditional && Operator == ExtractionConditionOperator.SplitPart;

    public bool IsBetween => !IsConditional && Operator == ExtractionConditionOperator.Between;

    public bool HasReplacementRow => !IsConditional && Operator is ExtractionConditionOperator.RegexReplace
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
        or ExtractionConditionOperator.NotStartsWith
        or ExtractionConditionOperator.EndsWith
        or ExtractionConditionOperator.NotEndsWith
        or ExtractionConditionOperator.Equals
        or ExtractionConditionOperator.NotEquals
        or ExtractionConditionOperator.InList
        or ExtractionConditionOperator.NotInList
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
        ExtractionConditionOperator.NotStartsWith => "préfixe à exclure",
        ExtractionConditionOperator.EndsWith => "suffixe attendu",
        ExtractionConditionOperator.NotEndsWith => "suffixe à exclure",
        ExtractionConditionOperator.Equals => "valeur exacte",
        ExtractionConditionOperator.NotEquals => "valeur à exclure",
        ExtractionConditionOperator.InList => "valeurs séparées par , ou | (ex. admin, vip, mod)",
        ExtractionConditionOperator.NotInList => "valeurs à exclure séparées par , ou |",
        ExtractionConditionOperator.RegexMatch => "motif regex (ex. [0-9a-f]{16,})",
        ExtractionConditionOperator.RegexNotMatch => "motif regex à exclure",
        ExtractionConditionOperator.RegexExtract => "motif regex avec (...) (ex. ([0-9a-f]{16,}))",
        ExtractionConditionOperator.RegexExtractAll => "motif regex capturant chaque occurrence (ex. ([0-9a-f]{16,}))",
        ExtractionConditionOperator.RegexReplace => "motif regex à remplacer",
        ExtractionConditionOperator.ReplaceText => "texte exact à remplacer",
        ExtractionConditionOperator.PrependText => "texte à ajouter au début (ex. https://)",
        ExtractionConditionOperator.AppendText => "texte à ajouter à la fin (ex. .json)",
        ExtractionConditionOperator.SetValue => "nouvelle valeur (ex. N/A ou vide)",
        ExtractionConditionOperator.Between => "délimiteur gauche (vide = depuis le début)",
        ExtractionConditionOperator.SplitPart => "séparateur (ex. / ou - ou ?)",
        ExtractionConditionOperator.Trim => "caractères à retirer (vide = espaces)",
        ExtractionConditionOperator.LengthEquals => "longueur exacte (ex. 32)",
        ExtractionConditionOperator.LengthMin => "longueur minimale (ex. 5)",
        ExtractionConditionOperator.LengthMax => "longueur maximale (ex. 200)",
        ExtractionConditionOperator.NumberEquals => "nombre exact (ex. 200)",
        ExtractionConditionOperator.NumberNotEquals => "nombre à exclure (ex. 404)",
        ExtractionConditionOperator.GreaterThan => "seuil numérique > (ex. 10)",
        ExtractionConditionOperator.GreaterOrEqual => "seuil numérique >= (ex. 10)",
        ExtractionConditionOperator.LessThan => "seuil numérique < (ex. 100)",
        ExtractionConditionOperator.LessOrEqual => "seuil numérique <= (ex. 100)",
        _ => string.Empty
    };

    public static bool IsFilterOperator(ExtractionConditionOperator op) => op is
        ExtractionConditionOperator.Contains
        or ExtractionConditionOperator.NotContains
        or ExtractionConditionOperator.StartsWith
        or ExtractionConditionOperator.NotStartsWith
        or ExtractionConditionOperator.EndsWith
        or ExtractionConditionOperator.NotEndsWith
        or ExtractionConditionOperator.Equals
        or ExtractionConditionOperator.NotEquals
        or ExtractionConditionOperator.InList
        or ExtractionConditionOperator.NotInList
        or ExtractionConditionOperator.RegexMatch
        or ExtractionConditionOperator.RegexNotMatch
        or ExtractionConditionOperator.IsEmpty
        or ExtractionConditionOperator.NotEmpty
        or ExtractionConditionOperator.LengthEquals
        or ExtractionConditionOperator.LengthMin
        or ExtractionConditionOperator.LengthMax
        or ExtractionConditionOperator.NumberEquals
        or ExtractionConditionOperator.NumberNotEquals
        or ExtractionConditionOperator.GreaterThan
        or ExtractionConditionOperator.GreaterOrEqual
        or ExtractionConditionOperator.LessThan
        or ExtractionConditionOperator.LessOrEqual;

    partial void OnIsConditionalChanged(bool value)
    {
        if (value && !IsFilterOperator(Operator))
        {
            Operator = ExtractionConditionOperator.Contains;
        }
    }

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

    public ChainExtractionCondition Clone()
    {
        var copy = new ChainExtractionCondition
        {
            IsEnabled = IsEnabled,
            IsConditional = IsConditional,
            Operator = Operator,
            Value = Value,
            Replacement = Replacement,
            RegexGroup = RegexGroup,
            SegmentIndex = SegmentIndex,
            IgnoreCase = IgnoreCase
        };
        copy.ReplaceBranches(ThenBranch.Clone(), ElseBranch.Clone());
        return copy;
    }

    public void ReplaceBranches(ChainConditionalBranch thenBranch, ChainConditionalBranch elseBranch)
    {
        ArgumentNullException.ThrowIfNull(thenBranch);
        ArgumentNullException.ThrowIfNull(elseBranch);

        UnhookBranchEvents(ThenBranch);
        UnhookBranchEvents(ElseBranch);

        ThenBranch = thenBranch;
        ElseBranch = elseBranch;

        HookBranchEvents(ThenBranch);
        HookBranchEvents(ElseBranch);

        OnPropertyChanged(nameof(ThenBranch));
        OnPropertyChanged(nameof(ElseBranch));
    }

    private void HookBranchEvents(ChainConditionalBranch branch)
    {
        branch.PropertyChanged += OnBranchPropertyChanged;
    }

    private void UnhookBranchEvents(ChainConditionalBranch branch)
    {
        branch.PropertyChanged -= OnBranchPropertyChanged;
    }

    private void OnBranchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(sender == ThenBranch ? nameof(ThenBranch) : nameof(ElseBranch));
    }
}
