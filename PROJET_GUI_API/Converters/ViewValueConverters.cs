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
