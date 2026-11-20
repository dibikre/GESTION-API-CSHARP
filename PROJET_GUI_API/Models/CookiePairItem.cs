using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class CookiePairItem : ObservableObject
{
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    public CookiePairItem()
    {
    }

    public CookiePairItem(string name, string value, bool isEnabled = true)
    {
        Name = name;
        Value = value;
        IsEnabled = isEnabled;
    }

    public static List<CookiePairItem> Parse(string? rawCookieHeader)
    {
        var items = new List<CookiePairItem>();
        if (string.IsNullOrWhiteSpace(rawCookieHeader))
        {
            return items;
        }

        var segments = rawCookieHeader.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                items.Add(new CookiePairItem(segment.Trim(), string.Empty));
                continue;
            }

            var name = segment[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = segment[(separatorIndex + 1)..].Trim();
            items.Add(new CookiePairItem(name, value));
        }

        return items;
    }

    public static string Serialize(IEnumerable<CookiePairItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var pairs = new List<string>();
        foreach (var item in items.Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Name)))
        {
            var cleanName = StripNewLines(item.Name).Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                continue;
            }

            var cleanValue = StripNewLines(item.Value).Trim();
            pairs.Add($"{cleanName}={cleanValue}");
        }

        return string.Join("; ", pairs);
    }

    public static string NormalizeText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        if (!rawText.Contains('\r', StringComparison.Ordinal) &&
            !rawText.Contains('\n', StringComparison.Ordinal))
        {
            return rawText.Trim();
        }

        var lines = rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var cleaned = line.Trim().TrimEnd(';').Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                parts.Add(cleaned);
            }
        }

        return string.Join("; ", parts);
    }

    private static string StripNewLines(string? text)
    {
        return string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r", string.Empty, StringComparison.Ordinal)
                  .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}
