using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed record ExtractionConditionPreset(
    string Label,
    Func<ChainExtractionCondition> CreateCondition)
{
    public override string ToString() => Label;
}

public sealed record ExtractionSelectionSuggestion(
    string SelectedText,
    string DetectedKindLabel,
    Func<ChainExtractionCondition> BuildTypeCondition,
    Func<ChainExtractionCondition> BuildContextCondition,
    Func<ChainExtractionCondition> BuildContainsCondition);

public static partial class ExtractionPresetCatalog
{
    public static IReadOnlyList<ExtractionConditionPreset> Presets { get; } =
    [
        new("Hex long (16+ car.)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = "([0-9a-fA-F]{16,})",
            RegexGroup = 1
        }),
        new("Hex 32 car. (MD5 / hash)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = "([0-9a-fA-F]{32})",
            RegexGroup = 1
        }),
        new("Hex 40 / 64 car. (SHA)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = "([0-9a-fA-F]{40,64})",
            RegexGroup = 1
        }),
        new("Tous les hex longs (éclater par ligne)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtractAll,
            Value = "([0-9a-fA-F]{16,})",
            RegexGroup = 1
        }),
        new("UUID / GUID", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexGroup = 1
        }),
        new("Nombres / IDs numériques", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = @"(\d+)",
            RegexGroup = 1
        }),
        new("URLs complètes (http / https)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = @"(https?://[^\s""'<>]+)",
            RegexGroup = 1
        }),
        new("Domaine / hôte d'URL", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = @"https?://([^/:?#]+)",
            RegexGroup = 1
        }),
        new("Dernier segment d'URL / chemin (/)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.SplitPart,
            Value = "/",
            SegmentIndex = -1
        }),
        new("Retirer paramètres d'URL (?...)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexReplace,
            Value = @"\?.*$",
            Replacement = string.Empty
        }),
        new("Adresses e-mail", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = @"([a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,})",
            RegexGroup = 1
        }),
        new("Jetons alphanumériques (16+ car.)", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.RegexExtract,
            Value = @"([A-Za-z0-9_-]{16,})",
            RegexGroup = 1
        }),
        new("Lignes non vides uniquement", () => new ChainExtractionCondition
        {
            Operator = ExtractionConditionOperator.NotEmpty
        })
    ];

    public static ExtractionSelectionSuggestion? AnalyzeSelection(string? fullText, int selectionStart, int selectionEnd)
    {
        if (string.IsNullOrEmpty(fullText))
        {
            return null;
        }

        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, fullText.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, fullText.Length);
        if (end <= start)
        {
            return null;
        }

        var selected = fullText[start..end];
        if (selected.Contains('\n') || selected.Contains('\r') || string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        var lineStart = fullText.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = fullText.IndexOf('\n', end);
        lineEnd = lineEnd < 0 ? fullText.Length : lineEnd;
        if (lineEnd > lineStart && fullText[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        var beforeOnLine = fullText[lineStart..start];
        var afterOnLine = fullText[end..lineEnd];

        var (kindLabel, valueRegex) = ClassifySelectedToken(selected);
        var contextPattern = BuildContextRegex(beforeOnLine, afterOnLine, valueRegex);
        var capturedSelected = selected;

        return new ExtractionSelectionSuggestion(
            capturedSelected,
            kindLabel,
            () => new ChainExtractionCondition
            {
                Operator = ExtractionConditionOperator.RegexExtract,
                Value = $"({valueRegex})",
                RegexGroup = 1
            },
            () => new ChainExtractionCondition
            {
                Operator = ExtractionConditionOperator.RegexExtract,
                Value = contextPattern,
                RegexGroup = 1
            },
            () => new ChainExtractionCondition
            {
                Operator = ExtractionConditionOperator.Contains,
                Value = capturedSelected
            });
    }

    private static (string Label, string ValueRegex) ClassifySelectedToken(string selected)
    {
        if (UuidRegex().IsMatch(selected))
        {
            return ("UUID / GUID", "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        }

        if (HexRegex().IsMatch(selected) && selected.Length >= 8)
        {
            var minLen = selected.Length >= 16 ? 16 : selected.Length;
            return ($"Hexa {selected.Length} car.", $"[0-9a-fA-F]{{{minLen},}}");
        }

        if (DigitsRegex().IsMatch(selected))
        {
            return ("Chiffres", @"\d+");
        }

        if (HttpUrlRegex().IsMatch(selected))
        {
            return ("URL HTTP/HTTPS", @"https?://[^\s""'<>]+");
        }

        if (AlphaNumTokenRegex().IsMatch(selected))
        {
            return ("Jeton / identifiant", "[A-Za-z0-9_-]+");
        }

        return ("Texte personnalisé", @"\S+");
    }

    private static string BuildContextRegex(string beforeOnLine, string afterOnLine, string valueRegex)
    {
        var leftAnchor = ExtractDelimiterAnchor(beforeOnLine, fromEnd: true);
        var rightAnchor = ExtractDelimiterAnchor(afterOnLine, fromEnd: false);

        if (string.IsNullOrEmpty(leftAnchor) && string.IsNullOrEmpty(rightAnchor))
        {
            return $"({valueRegex})";
        }

        var escapedLeft = Regex.Escape(leftAnchor);
        var escapedRight = Regex.Escape(rightAnchor);
        var inner = valueRegex;

        if (!string.IsNullOrEmpty(rightAnchor) && inner == "[A-Za-z0-9_-]+" && rightAnchor.StartsWith('-'))
        {
            inner = @"[^/\-\s]+";
        }
        else if (!string.IsNullOrEmpty(leftAnchor) && !string.IsNullOrEmpty(rightAnchor) && inner == @"\S+")
        {
            var boundaryChar = rightAnchor[0];
            inner = $"[^{Regex.Escape(boundaryChar.ToString())}]+";
        }

        return $"{escapedLeft}({inner}){escapedRight}";
    }

    private static string ExtractDelimiterAnchor(string text, bool fromEnd)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        const int maxAnchorLen = 8;
        char[] separators = ['/', '=', ':', '?', '&', '"', '\'', '(', '[', '{'];
        if (fromEnd)
        {
            var lastCh = text[^1];
            if (Array.IndexOf(separators, lastCh) >= 0)
            {
                return lastCh.ToString();
            }

            var slice = text.Length > maxAnchorLen ? text[^maxAnchorLen..] : text;
            var sepIdx = slice.LastIndexOfAny(separators);
            if (sepIdx >= 0 && sepIdx < slice.Length - 1)
            {
                return slice[sepIdx..];
            }
            return slice;
        }
        else
        {
            var slice = text.Length > maxAnchorLen ? text[..maxAnchorLen] : text;
            var sepIdx = slice.IndexOfAny(['/', '?', '&', '"', '\'', ')', ']', '}']);
            if (sepIdx > 0)
            {
                return slice[..sepIdx];
            }
            if (sepIdx == 0)
            {
                return slice[0].ToString();
            }
            return slice;
        }
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^https?://\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex AlphaNumTokenRegex();
}
