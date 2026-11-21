using System;
using System.Text.RegularExpressions;

namespace ApiTester.Services;

internal static class VariablePathHelper
{
    internal const string ScalarInnerPattern =
        @"(?:\$[\p{L}_][\p{L}\p{N}_]*|[\p{L}_][\p{L}\p{N}_]*(?:(?:\.[^.\{\}\[\]\r\n\s]+|\[[^\{\}\[\]\r\n]*\])+)?)";

    internal const string ListInnerPattern =
        @"[\p{L}_][\p{L}\p{N}_]*(?:(?:\.[^.\{\}\[\]\r\n\s]+|\[[^\{\}\[\]\r\n]*\])+)?";

    internal static readonly Regex ScalarMarkerRegex = new(
        @"\{\{\s*(" + ScalarInnerPattern + @")\s*\}\}",
        RegexOptions.Compiled);

    internal static readonly Regex ListMarkerRegex = new(
        @"\[\[\s*(" + ListInnerPattern + @")\s*\]\]",
        RegexOptions.Compiled);

    internal static readonly Regex AnyMarkerRegex = new(
        @"(?:\{\{\s*(" + ScalarInnerPattern + @")\s*\}\}|\[\[\s*(" + ListInnerPattern + @")\s*\]\])",
        RegexOptions.Compiled);

    public static bool TrySplit(string? expression, out string rootName, out string? jsonPath)
    {
        var trimmed = expression?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('$'))
        {
            rootName = trimmed;
            jsonPath = null;
            return false;
        }

        var dotIndex = trimmed.IndexOf('.');
        var bracketIndex = trimmed.IndexOf('[');
        var sepIndex = (dotIndex, bracketIndex) switch
        {
            ( < 0, < 0) => -1,
            ( >= 0, < 0) => dotIndex,
            ( < 0, >= 0) => bracketIndex,
            _ => Math.Min(dotIndex, bracketIndex)
        };

        if (sepIndex <= 0)
        {
            rootName = trimmed;
            jsonPath = null;
            return false;
        }

        rootName = trimmed[..sepIndex].Trim();
        var rawPath = trimmed[sepIndex] == '.'
            ? trimmed[(sepIndex + 1)..].Trim()
            : trimmed[sepIndex..].Trim();

        if (string.IsNullOrEmpty(rootName) || string.IsNullOrEmpty(rawPath))
        {
            jsonPath = null;
            return false;
        }

        jsonPath = rawPath;
        return true;
    }

    public static string ExtractRootName(string? expression)
    {
        TrySplit(expression, out var rootName, out _);
        return rootName;
    }
}
