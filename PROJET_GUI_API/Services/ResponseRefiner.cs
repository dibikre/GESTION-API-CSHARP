using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class ResponseRefiner : IResponseRefiner
{
    private const int MaxRegexMatches = 10_000;

    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        MaxDepth = 1024
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 1024
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    public ResponseRefinementResult Refine(
        string rawBody,
        string formattedBody,
        string contentType,
        string expression,
        ResponseFilterMode mode)
    {
        var raw = rawBody ?? string.Empty;
        var formatted = formattedBody ?? raw;
        var trimmedExpr = expression?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(trimmedExpr))
        {
            return ResponseRefinementResult.Success(formatted, string.Empty);
        }

        if (string.IsNullOrEmpty(raw) && string.IsNullOrEmpty(formatted))
        {
            return ResponseRefinementResult.Failure("Le corps de la réponse est vide.");
        }

        return mode switch
        {
            ResponseFilterMode.JsonPath => RefineJsonPath(raw, trimmedExpr),
            ResponseFilterMode.Regex => RefineRegex(raw, formatted, trimmedExpr),
            ResponseFilterMode.XPath => RefineXPath(raw, trimmedExpr),
            _ => RefineAuto(raw, formatted, contentType, trimmedExpr)
        };
    }

    private static ResponseRefinementResult RefineAuto(
        string raw,
        string formatted,
        string contentType,
        string expr)
    {
        if (LooksLikeJson(raw, contentType) && LooksLikeJsonPath(expr))
        {
            var jsonResult = RefineJsonPath(raw, expr);
            if (jsonResult.IsSuccess)
            {
                return jsonResult;
            }

            // Si l'expression est un chemin explicite (avec '.', '[' ou '$'), retourner l'erreur JSONPath précise ;
            // si c'est un mot simple absent de la racine JSON, tenter en Regex sur tout le corps.
            var isExplicitPath = expr.Contains('.') || expr.Contains('[') || expr.StartsWith('$');
            if (isExplicitPath)
            {
                return jsonResult;
            }
        }

        if (expr.StartsWith('/') && LooksLikeXml(raw, contentType))
        {
            var xpathResult = RefineXPath(raw, expr);
            if (xpathResult.IsSuccess)
            {
                return xpathResult;
            }
        }

        return RefineRegex(raw, formatted, expr);
    }

    private static ResponseRefinementResult RefineJsonPath(string raw, string expr)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ResponseRefinementResult.Failure("Le corps de la réponse est vide (JSON attendu).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw.TrimStart('\uFEFF'), JsonParseOptions);
        }
        catch (JsonException)
        {
            return ResponseRefinementResult.Failure("Le corps de la réponse n'est pas un JSON valide.");
        }

        using (document)
        {
            if (expr == "$")
            {
                var rootJson = JsonSerializer.Serialize(document.RootElement, IndentedJsonOptions);
                var summary = document.RootElement.ValueKind == JsonValueKind.Array
                    ? $"JSONPath • tableau ({document.RootElement.GetArrayLength()} élément(s))"
                    : "JSONPath • racine";
                return ResponseRefinementResult.Success(rootJson, summary);
            }

            if (!ResponseExtractor.TryParseJsonPath(expr, out var segments, out var pathError))
            {
                return ResponseRefinementResult.Failure(pathError!);
            }

            var currentElements = new List<JsonElement> { document.RootElement };
            var hasWildcard = false;

            foreach (var segment in segments)
            {
                if (segment.PropertyName is not null)
                {
                    var nextElements = new List<JsonElement>(currentElements.Count);
                    foreach (var el in currentElements)
                    {
                        if (el.ValueKind != JsonValueKind.Object)
                        {
                            if (!hasWildcard)
                            {
                                return ResponseRefinementResult.Failure(
                                    $"Impossible d'accéder à la propriété « {segment.PropertyName} » sur un élément de type {ResponseExtractor.DescribeKind(el.ValueKind)}.");
                            }

                            continue;
                        }

                        if (!el.TryGetProperty(segment.PropertyName, out var propVal))
                        {
                            if (!hasWildcard)
                            {
                                return ResponseRefinementResult.Failure(
                                    $"Propriété JSON introuvable : « {segment.PropertyName} ».");
                            }

                            continue;
                        }

                        nextElements.Add(propVal);
                    }

                    if (nextElements.Count == 0)
                    {
                        return ResponseRefinementResult.Failure(
                            $"Propriété JSON introuvable : « {segment.PropertyName} ».");
                    }

                    currentElements = nextElements;
                }

                if (segment.IsWildcard)
                {
                    var wasWildcard = hasWildcard;
                    hasWildcard = true;
                    var nextElements = new List<JsonElement>();

                    foreach (var el in currentElements)
                    {
                        if (el.ValueKind != JsonValueKind.Array)
                        {
                            if (!wasWildcard)
                            {
                                return ResponseRefinementResult.Failure(
                                    $"Le sélecteur [*] est inapplicable sur un élément de type {ResponseExtractor.DescribeKind(el.ValueKind)}.");
                            }

                            continue;
                        }

                        foreach (var item in el.EnumerateArray())
                        {
                            nextElements.Add(item);
                        }
                    }

                    if (nextElements.Count == 0)
                    {
                        return ResponseRefinementResult.Failure("Le tableau JSON ciblé est vide.");
                    }

                    currentElements = nextElements;
                }
                else if (segment.ArrayIndex.HasValue)
                {
                    var rawIndex = segment.ArrayIndex.Value;
                    var nextElements = new List<JsonElement>(currentElements.Count);

                    foreach (var el in currentElements)
                    {
                        if (el.ValueKind != JsonValueKind.Array)
                        {
                            if (!hasWildcard)
                            {
                                return ResponseRefinementResult.Failure(
                                    $"Index [{rawIndex}] inapplicable sur un élément de type {ResponseExtractor.DescribeKind(el.ValueKind)}.");
                            }

                            continue;
                        }

                        var length = el.GetArrayLength();
                        var resolvedIndex = rawIndex < 0 ? length + rawIndex : rawIndex;
                        if (resolvedIndex < 0 || resolvedIndex >= length)
                        {
                            if (!hasWildcard)
                            {
                                return ResponseRefinementResult.Failure(
                                    $"Index [{rawIndex}] hors limites (taille du tableau : {length}).");
                            }

                            continue;
                        }

                        nextElements.Add(el[resolvedIndex]);
                    }

                    if (nextElements.Count == 0)
                    {
                        return ResponseRefinementResult.Failure($"Aucune valeur trouvée pour l'index [{rawIndex}].");
                    }

                    currentElements = nextElements;
                }
            }

            if (currentElements.Count == 0)
            {
                return ResponseRefinementResult.Failure("Aucune valeur extraite pour l'expression JSONPath.");
            }

            if (!hasWildcard && currentElements.Count == 1)
            {
                var single = currentElements[0];
                if (single.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var pretty = JsonSerializer.Serialize(single, IndentedJsonOptions);
                    var summary = single.ValueKind == JsonValueKind.Array
                        ? $"JSONPath • tableau ({single.GetArrayLength()} élément(s))"
                        : "JSONPath • objet";
                    return ResponseRefinementResult.Success(pretty, summary);
                }

                return ResponseRefinementResult.Success(FormatJsonPrimitive(single), "JSONPath • 1 valeur");
            }

            var hasComplexElement = false;
            foreach (var el in currentElements)
            {
                if (el.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    hasComplexElement = true;
                    break;
                }
            }

            if (hasComplexElement)
            {
                var arrayText = SerializeElementsAsIndentedArray(currentElements);
                return ResponseRefinementResult.Success(arrayText, $"JSONPath • {currentElements.Count} élément(s)");
            }

            var scalarLines = new List<string>(currentElements.Count);
            foreach (var el in currentElements)
            {
                scalarLines.Add(FormatJsonPrimitive(el));
            }

            return ResponseRefinementResult.Success(
                string.Join(Environment.NewLine, scalarLines),
                $"JSONPath • {scalarLines.Count} valeur(s)");
        }
    }

    private static ResponseRefinementResult RefineXPath(string raw, string expr)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ResponseRefinementResult.Failure("Le corps de la réponse est vide (XML attendu).");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(raw.TrimStart('\uFEFF'), LoadOptions.None);
        }
        catch (XmlException)
        {
            var htmlDoc = HtmlDocumentParser.ToXDocument(raw);
            if (htmlDoc is null)
            {
                return ResponseRefinementResult.Failure(
                    "Le corps de la réponse n'est pas un XML/HTML valide pour XPath (essaie le mode Regex).");
            }

            document = htmlDoc;
        }

        object evaluated;
        try
        {
            evaluated = document.XPathEvaluate(expr);
        }
        catch (XPathException ex)
        {
            return ResponseRefinementResult.Failure($"Expression XPath invalide : {ex.Message}");
        }

        if (evaluated is string strVal)
        {
            return string.IsNullOrEmpty(strVal)
                ? ResponseRefinementResult.Failure("Aucun résultat trouvé pour cette expression XPath.")
                : ResponseRefinementResult.Success(strVal, "XPath • 1 valeur");
        }

        if (evaluated is IEnumerable enumerable)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                switch (item)
                {
                    case XElement element:
                        items.Add(element.ToString(SaveOptions.None));
                        break;
                    case XAttribute attribute:
                        items.Add(attribute.Value);
                        break;
                    case XText textNode:
                        items.Add(textNode.Value);
                        break;
                    case XNode node:
                        items.Add(node.ToString(SaveOptions.None));
                        break;
                    case not null:
                        var text = Convert.ToString(item, CultureInfo.InvariantCulture);
                        if (text is not null)
                        {
                            items.Add(text);
                        }
                        break;
                }
            }

            if (items.Count == 0)
            {
                return ResponseRefinementResult.Failure("Aucun nœud XML trouvé pour cette expression XPath.");
            }

            return ResponseRefinementResult.Success(
                string.Join(Environment.NewLine, items),
                $"XPath • {items.Count} résultat(s)");
        }

        var scalar = Convert.ToString(evaluated, CultureInfo.InvariantCulture) ?? string.Empty;
        return string.IsNullOrEmpty(scalar)
            ? ResponseRefinementResult.Failure("Aucun résultat trouvé pour cette expression XPath.")
            : ResponseRefinementResult.Success(scalar, "XPath • 1 valeur");
    }

    private static ResponseRefinementResult RefineRegex(string raw, string formatted, string pattern)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return ResponseRefinementResult.Failure($"Expression régulière invalide : {ex.Message}");
        }

        var hasCaptureGroups = regex.GetGroupNumbers().Length > 1;
        var results = new List<string>();
        var truncated = false;

        try
        {
            var target = !string.IsNullOrEmpty(formatted) ? formatted : raw;
            var matches = regex.Matches(target);
            if (matches.Count == 0 && !string.Equals(raw, target, StringComparison.Ordinal) && !string.IsNullOrEmpty(raw))
            {
                matches = regex.Matches(raw);
            }

            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                if (results.Count >= MaxRegexMatches)
                {
                    truncated = true;
                    break;
                }

                results.Add(ExtractMatchText(match, hasCaptureGroups));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return ResponseRefinementResult.Failure("L'évaluation du regex a expiré (> 500 ms).");
        }

        if (results.Count == 0)
        {
            return ResponseRefinementResult.Failure("Aucune correspondance trouvée pour cette expression régulière.");
        }

        var summary = truncated
            ? $"Regex • {MaxRegexMatches}+ correspondance(s) (limité à {MaxRegexMatches})"
            : $"Regex • {results.Count} correspondance(s)";

        return ResponseRefinementResult.Success(string.Join(Environment.NewLine, results), summary);
    }

    private static string ExtractMatchText(Match match, bool hasCaptureGroups)
    {
        if (!hasCaptureGroups || match.Groups.Count <= 1)
        {
            return match.Value;
        }

        var captured = new List<string>(match.Groups.Count - 1);
        for (var i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Success)
            {
                captured.Add(match.Groups[i].Value);
            }
        }

        return captured.Count switch
        {
            0 => match.Value,
            1 => captured[0],
            _ => string.Join(" | ", captured)
        };
    }

    private static string SerializeElementsAsIndentedArray(IReadOnlyList<JsonElement> elements)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var element in elements)
            {
                element.WriteTo(writer);
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string FormatJsonPrimitive(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => element.GetRawText()
    };

    private static bool LooksLikeJson(string raw, string contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = raw.TrimStart('\uFEFF').TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static bool LooksLikeXml(string raw, string contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = raw.TrimStart('\uFEFF').TrimStart();
        return trimmed.StartsWith('<');
    }

    private static bool LooksLikeJsonPath(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
        {
            return false;
        }

        var inBracket = false;
        for (var i = 0; i < expr.Length; i++)
        {
            var ch = expr[i];
            if (char.IsWhiteSpace(ch))
            {
                return false;
            }

            switch (ch)
            {
                case '[':
                    inBracket = true;
                    break;
                case ']':
                    inBracket = false;
                    break;
                case '$':
                    if (i != 0 || (expr.Length > 1 && expr[1] != '.' && expr[1] != '['))
                    {
                        return false;
                    }
                    break;
                case '*':
                case '+':
                    if (!inBracket)
                    {
                        return false;
                    }
                    break;
                case '(':
                case ')':
                case '\\':
                case '?':
                case '|':
                case '{':
                case '}':
                case '"':
                case '\'':
                case '^':
                case ':':
                    return false;
            }
        }

        return ResponseExtractor.TryParseJsonPath(expr, out _, out _) || expr == "$";
    }
}
