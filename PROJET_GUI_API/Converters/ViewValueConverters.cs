using System;
using System.Globalization;
using ApiTester.Models;
using ApiTester.ViewModels;
using Avalonia.Data.Converters;

namespace ApiTester.Converters;

public static class StatusCategoryConverters
{
    public static readonly IValueConverter IsSuccess =
        new FuncValueConverter<StatusCategory, bool>(c => c == StatusCategory.Success);

    public static readonly IValueConverter IsRedirect =
        new FuncValueConverter<StatusCategory, bool>(c => c == StatusCategory.Redirect);

    public static readonly IValueConverter IsError =
        new FuncValueConverter<StatusCategory, bool>(c =>
            c is StatusCategory.ClientError or StatusCategory.ServerError or StatusCategory.NetworkError);

    public static readonly IValueConverter IsPositiveInt =
        new FuncValueConverter<int, bool>(v => v > 0);

    public static readonly IValueConverter IsZeroInt =
        new FuncValueConverter<int, bool>(v => v == 0);
}

public static class BodyTypeConverters
{
    public static readonly IValueConverter IsNone =
        new FuncValueConverter<BodyType, bool>(b => b == BodyType.None);
}

public sealed class IntToDecimalConverter : IValueConverter
{
    public static readonly IntToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)intValue;
        }

        return 30m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, 1, 3600);
        }

        return 30;
    }
}

public sealed class DelayMsToDecimalConverter : IValueConverter
{
    public static readonly DelayMsToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)intValue;
        }

        return (decimal)RateLimitPolicy.DefaultPauseFloorMs;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, 0, 60_000);
        }

        return RateLimitPolicy.DefaultPauseFloorMs;
    }
}

public sealed class LongToDecimalConverter : IValueConverter
{
    public static readonly LongToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long longValue)
        {
            return (decimal)longValue;
        }

        return 1m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return (long)decValue;
        }

        return 1L;
    }
}

public static class BatchModeConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<BatchMode, string>(mode => mode switch
        {
            BatchMode.Combinations => "Combinaisons",
            BatchMode.Parallel => "En parallèle",
            _ => mode.ToString()
        });
}

public static class SaveModeConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<SaveMode, string>(mode => mode switch
        {
            SaveMode.None => "Ne pas enregistrer",
            SaveMode.PerRequest => "Un fichier par requête",
            SaveMode.Grouped => "Un seul fichier groupé",
            SaveMode.Both => "Les deux",
            _ => mode.ToString()
        });
}

public static class GroupedFormatConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<GroupedFormat, string>(format => format switch
        {
            GroupedFormat.Json => "JSON (un tableau)",
            GroupedFormat.JsonLines => "JSON Lines (une ligne par requête)",
            _ => format.ToString()
        });
}

public static class HttpMethodConverters
{
    public static readonly IValueConverter ToUpper =
        new FuncValueConverter<HttpMethodType, string>(method => method.ToString().ToUpperInvariant());

    public static readonly IValueConverter IsGet =
        new FuncValueConverter<HttpMethodType, bool>(method => method == HttpMethodType.Get);

    public static readonly IValueConverter IsPost =
        new FuncValueConverter<HttpMethodType, bool>(method => method == HttpMethodType.Post);

    public static readonly IValueConverter IsPutOrPatch =
        new FuncValueConverter<HttpMethodType, bool>(method => method is HttpMethodType.Put or HttpMethodType.Patch);

    public static readonly IValueConverter IsDelete =
        new FuncValueConverter<HttpMethodType, bool>(method => method == HttpMethodType.Delete);
}

public static class CollectionCountConverters
{
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(count => count == 0);

    public static readonly IValueConverter IsPositive =
        new FuncValueConverter<int, bool>(count => count > 0);
}

public static class StringDisplayConverters
{
    public static readonly IValueConverter EmptyToNull =
        new FuncValueConverter<string?, string?>(text => string.IsNullOrWhiteSpace(text) ? null : text);
}

public static class VariableModeConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<VariableMode, string>(mode => mode switch
        {
            VariableMode.Range => "Plage (incrément)",
            VariableMode.List => "Liste de valeurs",
            VariableMode.Random => "Aléatoire",
            _ => mode.ToString()
        });
}

public static class NumberBaseConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<NumberBase, string>(numberBase => numberBase switch
        {
            NumberBase.Binary => "Binaire (2)",
            NumberBase.Octal => "Octal (8)",
            NumberBase.Decimal => "Décimal (10)",
            NumberBase.Hexadecimal => "Hexadécimal (16)",
            _ => numberBase.ToString()
        });
}

public static class RandomKindConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<RandomKind, string>(kind => kind switch
        {
            RandomKind.Integer => "Entier",
            RandomKind.HexBytes => "Hexadécimal (octets)",
            RandomKind.Text => "Texte",
            RandomKind.Guid => "GUID",
            RandomKind.PickFromList => "Choix dans une liste",
            _ => kind.ToString()
        });
}

public sealed class BoundedIntToDecimalConverter : IValueConverter
{
    public static readonly BoundedIntToDecimalConverter Width = new(0, BatchVariable.MaxWidth, 0);
    public static readonly BoundedIntToDecimalConverter RandomCount = new(1, BatchVariable.MaxRandomCount, 10);
    public static readonly BoundedIntToDecimalConverter ByteCount = new(1, BatchVariable.MaxByteCount, 4);
    public static readonly BoundedIntToDecimalConverter TextLength = new(1, BatchVariable.MaxTextLength, 8);

    private readonly int _min;
    private readonly int _max;
    private readonly int _fallback;

    public BoundedIntToDecimalConverter(int min, int max, int fallback)
    {
        _min = min;
        _max = max;
        _fallback = fallback;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)intValue;
        }

        return (decimal)_fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, _min, _max);
        }

        return _fallback;
    }
}

public sealed class ZeroPadToDecimalConverter : IValueConverter
{
    public static readonly ZeroPadToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)intValue;
        }

        return 0m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, 0, 12);
        }

        return 0;
    }
}

public static class ExtractionKindConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ExtractionKind, string>(kind => kind switch
        {
            ExtractionKind.JsonPath => "JSONPath",
            ExtractionKind.Regex => "Regex",
            _ => kind.ToString()
        });
}

public static class ChainExtractionSourceConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ChainExtractionSource, string>(source => source switch
        {
            ChainExtractionSource.Body => "Corps",
            ChainExtractionSource.Header => "En-tête",
            _ => source.ToString()
        });
}

public static class ExtractionConditionOperatorConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ExtractionConditionOperator, string>(op => op switch
        {
            ExtractionConditionOperator.Contains => "Contient",
            ExtractionConditionOperator.NotContains => "Ne contient pas",
            ExtractionConditionOperator.StartsWith => "Commence par",
            ExtractionConditionOperator.NotStartsWith => "Ne commence pas par",
            ExtractionConditionOperator.EndsWith => "Finit par",
            ExtractionConditionOperator.NotEndsWith => "Ne finit pas par",
            ExtractionConditionOperator.Equals => "Égal à (==)",
            ExtractionConditionOperator.NotEquals => "Différent de (!=)",
            ExtractionConditionOperator.InList => "Est dans la liste",
            ExtractionConditionOperator.NotInList => "Exclu de la liste",
            ExtractionConditionOperator.RegexMatch => "Regex correspond",
            ExtractionConditionOperator.RegexNotMatch => "Regex exclut",
            ExtractionConditionOperator.RegexExtract => "Regex extraire (groupe)",
            ExtractionConditionOperator.RegexExtractAll => "Regex extraire tout (éclater)",
            ExtractionConditionOperator.RegexReplace => "Regex remplacer",
            ExtractionConditionOperator.ReplaceText => "Remplacer texte",
            ExtractionConditionOperator.PrependText => "Ajouter au début (préfixe)",
            ExtractionConditionOperator.AppendText => "Ajouter à la fin (suffixe)",
            ExtractionConditionOperator.SetValue => "Remplacer toute la valeur par",
            ExtractionConditionOperator.Between => "Entre délimiteurs",
            ExtractionConditionOperator.SplitPart => "Découper (séparateur / index)",
            ExtractionConditionOperator.Trim => "Nettoyer début / fin",
            ExtractionConditionOperator.ToLower => "Convertir en minuscules",
            ExtractionConditionOperator.ToUpper => "Convertir en MAJUSCULES",
            ExtractionConditionOperator.IsEmpty => "Est vide",
            ExtractionConditionOperator.NotEmpty => "Non vide",
            ExtractionConditionOperator.LengthEquals => "Longueur ==",
            ExtractionConditionOperator.LengthMin => "Longueur >=",
            ExtractionConditionOperator.LengthMax => "Longueur <=",
            ExtractionConditionOperator.NumberEquals => "Nombre ==",
            ExtractionConditionOperator.NumberNotEquals => "Nombre !=",
            ExtractionConditionOperator.GreaterThan => "Nombre >",
            ExtractionConditionOperator.GreaterOrEqual => "Nombre >=",
            ExtractionConditionOperator.LessThan => "Nombre <",
            ExtractionConditionOperator.LessOrEqual => "Nombre <=",
            _ => op.ToString()
        });
}

public static class ExtractionConditionalActionConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ExtractionConditionalAction, string>(action => action switch
        {
            ExtractionConditionalAction.Keep => "Garder tel quel",
            ExtractionConditionalAction.Exclude => "Exclure l'élément",
            ExtractionConditionalAction.SetValue => "Remplacer toute la valeur par",
            ExtractionConditionalAction.PrependText => "Ajouter au début (préfixe)",
            ExtractionConditionalAction.AppendText => "Ajouter à la fin (suffixe)",
            ExtractionConditionalAction.ReplaceText => "Remplacer texte",
            ExtractionConditionalAction.RegexExtract => "Regex extraire (groupe)",
            ExtractionConditionalAction.RegexExtractAll => "Regex extraire tout (éclater)",
            ExtractionConditionalAction.RegexReplace => "Regex remplacer",
            ExtractionConditionalAction.Between => "Entre délimiteurs",
            ExtractionConditionalAction.SplitPart => "Découper (séparateur / index)",
            ExtractionConditionalAction.Trim => "Nettoyer début / fin",
            ExtractionConditionalAction.ToLower => "Convertir en minuscules",
            ExtractionConditionalAction.ToUpper => "Convertir en MAJUSCULES",
            _ => action.ToString()
        });
}

public static class ExtractionConditionMatchModeConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ExtractionConditionMatchMode, string>(mode => mode switch
        {
            ExtractionConditionMatchMode.All => "Toutes (ET)",
            ExtractionConditionMatchMode.Any => "Au moins une (OU)",
            _ => mode.ToString()
        });
}

public sealed class RegexGroupToDecimalConverter : IValueConverter
{
    public static readonly RegexGroupToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)Math.Clamp(intValue, 0, 99);
        }

        return 1m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, 0, 99);
        }

        return 1;
    }
}

public sealed class SegmentIndexToDecimalConverter : IValueConverter
{
    public static readonly SegmentIndexToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)Math.Clamp(intValue, -99, 99);
        }

        return 0m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, -99, 99);
        }

        return 0;
    }
}

public sealed class ChainDelayMsToDecimalConverter : IValueConverter
{
    public static readonly ChainDelayMsToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)Math.Clamp(intValue, 0, 60_000);
        }

        return 500m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, 0, 60_000);
        }

        return 500;
    }
}

public static class ResponseFormatConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ResponseFormat, string>(format => format switch
        {
            ResponseFormat.Auto => "Auto",
            ResponseFormat.Json => "JSON",
            ResponseFormat.Xml => "XML",
            ResponseFormat.Html => "HTML",
            ResponseFormat.Text => "Texte",
            _ => format.ToString()
        });
}

public static class ExtractorValueKindConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ExtractorValueKind, string>(kind => kind switch
        {
            ExtractorValueKind.Digits => "Chiffres (\\d+)",
            ExtractorValueKind.NonWhitespace => "Non-espaces (\\S+)",
            ExtractorValueKind.UntilEndOfLine => "Jusqu'à fin de ligne",
            ExtractorValueKind.Custom => "Personnalisé",
            _ => kind.ToString()
        });
}

public sealed class ExtractorContextLengthConverter : IValueConverter
{
    public static readonly ExtractorContextLengthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return (decimal)Math.Clamp(intValue, 1, 100);
        }

        return 15m;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decValue)
        {
            return Math.Clamp((int)decValue, 1, 100);
        }

        return 15;
    }
}

public static class LiveTestStatusConverters
{
    public static readonly IValueConverter IsSuccess =
        new FuncValueConverter<LiveTestStatus, bool>(status => status == LiveTestStatus.Success);

    public static readonly IValueConverter IsWarning =
        new FuncValueConverter<LiveTestStatus, bool>(status => status == LiveTestStatus.Warning);

    public static readonly IValueConverter IsError =
        new FuncValueConverter<LiveTestStatus, bool>(status => status == LiveTestStatus.Error);
}

public static class ChainCliLanguageConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ChainCliLanguage, string>(lang => lang switch
        {
            ChainCliLanguage.Python => "Python",
            ChainCliLanguage.JavaScript => "JavaScript",
            ChainCliLanguage.Php => "PHP",
            _ => lang.ToString()
        });
}

public static class ResponseFilterModeConverters
{
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<ResponseFilterMode, string>(mode => mode switch
        {
            ResponseFilterMode.Auto => "Auto",
            ResponseFilterMode.JsonPath => "JSONPath",
            ResponseFilterMode.Regex => "Regex",
            ResponseFilterMode.XPath => "XPath",
            _ => mode.ToString()
        });
}

public static class HeaderKeyConverters
{
    public static readonly IValueConverter IsCookie =
        new FuncValueConverter<string?, bool>(key =>
            string.Equals(key?.Trim(), "Cookie", StringComparison.OrdinalIgnoreCase));
}
