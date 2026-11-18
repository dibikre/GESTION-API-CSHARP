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
