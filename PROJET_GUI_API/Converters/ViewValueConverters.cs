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
