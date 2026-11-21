using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class ExtractionSuggester : IExtractionSuggester
{
    private const int PreviewMaxChars = 72;
    private const string HtmlFallbackMessage =
        "Ce HTML est trop irrégulier pour être analysé automatiquement : sélectionne le texte directement ci-dessous.";

    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        MaxDepth = 1024
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex HtmlDoctypeRegex = new(
        @"<!DOCTYPE\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex HtmlVoidTagRegex = new(
        @"<(br|img|input|hr|meta|link|source)(\b(?:[^""'>]|""[^""]*""|'[^']*')*?)(/?)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    public ResponseFormat DetectFormat(string? text, ResponseFormat requestedFormat)
    {
        if (requestedFormat != ResponseFormat.Auto)
        {
            return requestedFormat;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return ResponseFormat.Text;
        }

        var trimmed = text.TrimStart('\uFEFF').TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return ResponseFormat.Json;
        }

        if (trimmed.StartsWith('<'))
        {
            if (trimmed.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseFormat.Html;
            }

            return ResponseFormat.Xml;
        }

        return ResponseFormat.Text;
    }

    public FormatAnalysisResult Analyze(string? text, ResponseFormat requestedFormat)
    {
        var content = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return new FormatAnalysisResult(
                ResponseFormat.Text,
                FallbackToText: true,
                StatusMessage: "Colle une réponse pour commencer l'analyse.",
                RootNodes: []);
        }

        var targetFormat = DetectFormat(content, requestedFormat);

        return targetFormat switch
        {
            ResponseFormat.Json => AnalyzeJson(content),
            ResponseFormat.Xml => AnalyzeXml(content),
            ResponseFormat.Html => AnalyzeHtml(content),
            _ => new FormatAnalysisResult(ResponseFormat.Text, FallbackToText: true, StatusMessage: null, RootNodes: [])
        };
    }

    public string? SuggestJsonPath(ExtractorNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return string.IsNullOrWhiteSpace(node.JsonPath) ? null : node.JsonPath;
    }

    public ExtractionSuggestion SuggestForNode(string rawText, ExtractorNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var text = rawText ?? string.Empty;

        return node.NodeKind switch
        {
            ExtractorNodeKind.JsonValue or ExtractorNodeKind.JsonObject or ExtractorNodeKind.JsonArray
                => SuggestJsonNode(text, node),
            ExtractorNodeKind.XmlElement or ExtractorNodeKind.XmlAttribute
                => SuggestXmlNode(text, node),
            _ => new ExtractionSuggestion(null, null, 0, null, "Type de nœud non pris en charge.")
        };
    }

    public ExtractionSuggestion SuggestManualRegex(
        string rawText,
        int selectionStart,
        int selectionLength,
        int contextLength,
        ExtractorValueKind valueKind,
        string? customPattern,
        bool useTrailingAnchor)
    {
        var text = rawText ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return new ExtractionSuggestion(null, null, 0, null, "Le texte est vide.");
        }

        var safeStart = Math.Clamp(selectionStart, 0, text.Length);
        var safeLength = Math.Clamp(selectionLength, 0, text.Length - safeStart);
        if (safeLength <= 0)
        {
            return new ExtractionSuggestion(null, null, 0, null, "Sélectionne un passage dans le texte avant de générer un regex.");
        }

        var safeContextLen = Math.Clamp(contextLength, 1, 100);
        var ctxStart = Math.Max(0, safeStart - safeContextLen);
        var beforeText = text[ctxStart..safeStart];
        var leadingAnchor = EscapeLiteralWithWhitespace(beforeText);

        string captureGroup;
        if (valueKind == ExtractorValueKind.Custom)
        {
            var trimmedCustom = customPattern?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmedCustom))
            {
                return new ExtractionSuggestion(null, null, 0, null, "Saisis un motif personnalisé.");
            }

            try
            {
                _ = new Regex(trimmedCustom, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return new ExtractionSuggestion(null, null, 0, null, $"Motif personnalisé invalide : {ex.Message}");
            }

            captureGroup = $"({trimmedCustom})";
        }
        else
        {
            captureGroup = valueKind switch
            {
                ExtractorValueKind.Digits => @"(\d+)",
                ExtractorValueKind.NonWhitespace => @"(\S+)",
                ExtractorValueKind.UntilEndOfLine => @"([^\r\n]*)",
                _ => @"(\S+)"
            };
        }

        var trailingAnchor = string.Empty;
        if (useTrailingAnchor)
        {
            var afterStart = safeStart + safeLength;
            var afterEnd = Math.Min(text.Length, afterStart + safeContextLen);
            if (afterEnd > afterStart)
            {
                var afterText = text[afterStart..afterEnd];
                trailingAnchor = EscapeLiteralWithWhitespace(afterText);
            }
        }

        if (string.IsNullOrEmpty(leadingAnchor) && string.IsNullOrEmpty(trailingAnchor))
        {
            return new ExtractionSuggestion(
                null,
                captureGroup,
                CountRegexMatches(text, captureGroup),
                "Aucun contexte autour de la sélection : ajoute du texte avant ou active l'ancre de fin.",
                null);
        }

        var finalRegex = $"{leadingAnchor}{captureGroup}{trailingAnchor}";
        if (!IsValidRegex(finalRegex))
        {
            return new ExtractionSuggestion(null, null, 0, null, "Le motif généré est invalide.");
        }

        var occurrences = CountRegexMatches(text, finalRegex);
        var warning = occurrences > 1
            ? $"Ce motif correspond à {occurrences} passages dans le texte : augmente la longueur du contexte ou active l'ancre de fin."
            : null;

        return new ExtractionSuggestion(null, finalRegex, occurrences, warning, null);
    }

    public IReadOnlyList<RealtimeExtractionSuggestion> SuggestRealtime(
        ResponseModel response,
        ChainExtractionSource source,
        ExtractionKind kind,
        string? query,
        string? headerName = null,
        string? variableName = null,
        bool isList = false,
        int maxResults = 25)
    {
        if (response is null)
        {
            return [];
        }

        return RealtimeExtractionMatcher.SuggestRealtime(
            response,
            source,
            kind,
            query,
            headerName,
            variableName,
            isList,
            this,
            maxResults);
    }

    public IReadOnlyList<RealtimeExtractionSuggestion> SuggestHeaderNames(
        ResponseModel response,
        string? query,
        int maxResults = 25)
    {
        if (response is null)
        {
            return [];
        }

        return RealtimeExtractionMatcher.SuggestHeaderNames(
            response,
            query,
            this,
            maxResults);
    }

    private static FormatAnalysisResult AnalyzeJson(string text)
    {
        try
        {
            var normalized = text.TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(normalized, JsonParseOptions);
            var rootNode = BuildJsonNode(doc.RootElement, keyLabel: null, jsonPath: string.Empty, depth: 0);
            var nodes = new ObservableCollection<ExtractorNode> { rootNode };
            return new FormatAnalysisResult(ResponseFormat.Json, FallbackToText: false, StatusMessage: null, RootNodes: nodes);
        }
        catch (JsonException)
        {
            return new FormatAnalysisResult(
                ResponseFormat.Text,
                FallbackToText: true,
                StatusMessage: "JSON invalide : bascule en mode texte (sélection manuelle).",
                RootNodes: []);
        }
    }

    private static ExtractorNode BuildJsonNode(JsonElement element, string? keyLabel, string jsonPath, int depth)
    {
        var prefix = keyLabel is null ? string.Empty : $"{keyLabel} : ";

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var propCount = 0;
                foreach (var _ in element.EnumerateObject())
                {
                    propCount++;
                }

                var node = new ExtractorNode
                {
                    Label = keyLabel is null
                        ? $"{{ }} ({propCount} propriété{(propCount > 1 ? "s" : string.Empty)})"
                        : $"{keyLabel} {{ {propCount} }}",
                    KindTag = "obj",
                    NodeKind = ExtractorNodeKind.JsonObject,
                    JsonPath = string.IsNullOrEmpty(jsonPath) ? null : jsonPath,
                    AnchorName = keyLabel is not null && !keyLabel.StartsWith('[') ? keyLabel : null,
                    JsonKind = JsonValueKind.Object,
                    IsLeaf = false,
                    IsSelectable = !string.IsNullOrEmpty(jsonPath),
                    IsExpanded = depth < 2
                };

                foreach (var prop in element.EnumerateObject())
                {
                    var childPath = string.IsNullOrEmpty(jsonPath)
                        ? prop.Name
                        : $"{jsonPath}.{prop.Name}";
                    node.Children.Add(BuildJsonNode(prop.Value, prop.Name, childPath, depth + 1));
                }

                return node;
            }

            case JsonValueKind.Array:
            {
                var len = element.GetArrayLength();
                var node = new ExtractorNode
                {
                    Label = keyLabel is null
                        ? $"[ ] ({len} élément{(len > 1 ? "s" : string.Empty)})"
                        : $"{keyLabel} [ {len} ]",
                    KindTag = "arr",
                    NodeKind = ExtractorNodeKind.JsonArray,
                    JsonPath = string.IsNullOrEmpty(jsonPath) ? null : jsonPath,
                    AnchorName = keyLabel is not null && !keyLabel.StartsWith('[') ? keyLabel : null,
                    JsonKind = JsonValueKind.Array,
                    IsLeaf = false,
                    IsSelectable = !string.IsNullOrEmpty(jsonPath),
                    IsExpanded = depth < 2
                };

                var idx = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var childPath = $"{jsonPath}[{idx}]";
                    node.Children.Add(BuildJsonNode(item, $"[{idx}]", childPath, depth + 1));
                    idx++;
                }

                return node;
            }

            default:
            {
                var rawScalar = element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
                var displayVal = element.ValueKind == JsonValueKind.String
                    ? $"\"{Truncate(rawScalar, PreviewMaxChars)}\""
                    : Truncate(rawScalar, PreviewMaxChars);

                var kindTag = element.ValueKind switch
                {
                    JsonValueKind.String => "str",
                    JsonValueKind.Number => "num",
                    JsonValueKind.True or JsonValueKind.False => "bool",
                    JsonValueKind.Null => "null",
                    _ => "val"
                };

                return new ExtractorNode
                {
                    Label = $"{prefix}{displayVal}",
                    KindTag = kindTag,
                    NodeKind = ExtractorNodeKind.JsonValue,
                    JsonPath = string.IsNullOrEmpty(jsonPath) ? null : jsonPath,
                    AnchorName = keyLabel is not null && !keyLabel.StartsWith('[') ? keyLabel : null,
                    JsonKind = element.ValueKind,
                    RawScalarValue = rawScalar,
                    IsLeaf = true,
                    IsSelectable = !string.IsNullOrEmpty(jsonPath),
                    IsExpanded = false
                };
            }
        }
    }

    private static FormatAnalysisResult AnalyzeXml(string text)
    {
        try
        {
            var doc = LoadXmlDocument(text);
            if (doc.Root is null)
            {
                return new FormatAnalysisResult(
                    ResponseFormat.Text,
                    FallbackToText: true,
                    StatusMessage: "XML vide ou sans élément racine : bascule en mode texte (sélection manuelle).",
                    RootNodes: []);
            }

            var rootNode = BuildXmlNode(doc.Root, depth: 0);
            var nodes = new ObservableCollection<ExtractorNode> { rootNode };
            return new FormatAnalysisResult(ResponseFormat.Xml, FallbackToText: false, StatusMessage: null, RootNodes: nodes);
        }
        catch (Exception)
        {
            return new FormatAnalysisResult(
                ResponseFormat.Text,
                FallbackToText: true,
                StatusMessage: "XML invalide : bascule en mode texte (sélection manuelle).",
                RootNodes: []);
        }
    }

    private static FormatAnalysisResult AnalyzeHtml(string text)
    {
        try
        {
            var preprocessed = PreprocessHtmlForXml(text);
            var doc = LoadXmlDocument(preprocessed);
            if (doc.Root is null)
            {
                return new FormatAnalysisResult(
                    ResponseFormat.Text,
                    FallbackToText: true,
                    StatusMessage: HtmlFallbackMessage,
                    RootNodes: []);
            }

            var rootNode = BuildXmlNode(doc.Root, depth: 0);
            var nodes = new ObservableCollection<ExtractorNode> { rootNode };
            return new FormatAnalysisResult(ResponseFormat.Html, FallbackToText: false, StatusMessage: null, RootNodes: nodes);
        }
        catch (Exception)
        {
            return new FormatAnalysisResult(
                ResponseFormat.Text,
                FallbackToText: true,
                StatusMessage: HtmlFallbackMessage,
                RootNodes: []);
        }
    }

    private static string PreprocessHtmlForXml(string html)
    {
        var withoutDoctype = HtmlDoctypeRegex.Replace(html, string.Empty);
        return HtmlVoidTagRegex.Replace(withoutDoctype, match =>
        {
            if (match.Groups[3].Value == "/")
            {
                return match.Value;
            }

            return $"<{match.Groups[1].Value}{match.Groups[2].Value} />";
        });
    }

    private static XDocument LoadXmlDocument(string xmlText)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

        using var reader = new StringReader(xmlText);
        using var xmlReader = XmlReader.Create(reader, settings);
        return XDocument.Load(xmlReader);
    }

    private static ExtractorNode BuildXmlNode(XElement element, int depth)
    {
        var prefix = element.Name.Namespace != XNamespace.None
            ? element.GetPrefixOfNamespace(element.Name.Namespace)
            : null;
        var qualifiedTag = string.IsNullOrEmpty(prefix)
            ? element.Name.LocalName
            : $"{prefix}:{element.Name.LocalName}";

        var hasChildElements = element.HasElements;
        var scalarText = !hasChildElements ? element.Value.Trim() : string.Empty;

        var label = !hasChildElements && !string.IsNullOrEmpty(scalarText)
            ? $"<{qualifiedTag}>{Truncate(scalarText, PreviewMaxChars)}</{qualifiedTag}>"
            : $"<{qualifiedTag}>";

        var node = new ExtractorNode
        {
            Label = label,
            KindTag = "xml",
            NodeKind = ExtractorNodeKind.XmlElement,
            ElementTagName = qualifiedTag,
            RawScalarValue = scalarText,
            IsLeaf = !hasChildElements && !element.HasAttributes,
            IsSelectable = !hasChildElements || element.HasAttributes,
            IsExpanded = depth < 2
        };

        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration)
            {
                continue;
            }

            var attrPrefix = attr.Name.Namespace != XNamespace.None
                ? element.GetPrefixOfNamespace(attr.Name.Namespace)
                : null;
            var qualifiedAttr = string.IsNullOrEmpty(attrPrefix)
                ? attr.Name.LocalName
                : $"{attrPrefix}:{attr.Name.LocalName}";

            node.Children.Add(new ExtractorNode
            {
                Label = $"@{qualifiedAttr} = \"{Truncate(attr.Value, PreviewMaxChars)}\"",
                KindTag = "attr",
                NodeKind = ExtractorNodeKind.XmlAttribute,
                ElementTagName = qualifiedTag,
                AttributeName = qualifiedAttr,
                RawScalarValue = attr.Value,
                IsLeaf = true,
                IsSelectable = true,
                IsExpanded = false
            });
        }

        foreach (var child in element.Elements())
        {
            node.Children.Add(BuildXmlNode(child, depth + 1));
        }

        return node;
    }

    private static ExtractionSuggestion SuggestJsonNode(string rawText, ExtractorNode node)
    {
        var jsonPath = string.IsNullOrWhiteSpace(node.JsonPath) ? null : node.JsonPath;

        if (string.IsNullOrEmpty(node.AnchorName))
        {
            return new ExtractionSuggestion(
                jsonPath,
                null,
                0,
                null,
                "Élément d'un tableau sans clé nommée : utilise le chemin JSONPath.");
        }

        var escapedKey = EscapeLiteralWithWhitespace(node.AnchorName);
        var keyAnchor = $@"""{escapedKey}""\s*:";

        string? regex;
        string? info = null;

        if (node.JsonKind == JsonValueKind.String)
        {
            regex = $@"{keyAnchor}\s*""([^""\\]*(?:\\.[^""\\]*)*)""";
        }
        else if (node.JsonKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
        {
            regex = $@"{keyAnchor}\s*([^\s,\}}\]]+)";
        }
        else
        {
            regex = null;
            info = "Sélectionne une valeur feuille pour générer un regex alternatif.";
        }

        var occurrences = CountRegexMatches(rawText, keyAnchor);
        var warning = occurrences > 1 && regex is not null
            ? $"La clé « {node.AnchorName} » apparaît {occurrences} fois dans la réponse : le regex capturera la première occurrence. Utilise JSONPath pour cibler cet élément."
            : null;

        return new ExtractionSuggestion(jsonPath, regex, occurrences, warning, info);
    }

    private static ExtractionSuggestion SuggestXmlNode(string rawText, ExtractorNode node)
    {
        if (node.NodeKind == ExtractorNodeKind.XmlAttribute && !string.IsNullOrWhiteSpace(node.AttributeName))
        {
            var attrEsc = EscapeLiteralWithWhitespace(node.AttributeName);
            string regex;
            string anchorPattern;

            if (!string.IsNullOrWhiteSpace(node.ElementTagName))
            {
                var tagEsc = EscapeLiteralWithWhitespace(node.ElementTagName);
                anchorPattern = $@"<{tagEsc}\b[^>]*\b{attrEsc}\s*=";
                regex = $@"<{tagEsc}\b[^>]*\b{attrEsc}\s*=\s*[""']([^""']*)[""']";
            }
            else
            {
                anchorPattern = $@"\b{attrEsc}\s*=";
                regex = $@"\b{attrEsc}\s*=\s*[""']([^""']*)[""']";
            }

            var occurrences = CountRegexMatches(rawText, anchorPattern);
            var warning = occurrences > 1
                ? $"L'attribut « {node.AttributeName} » apparaît {occurrences} fois : le regex capturera la première occurrence."
                : null;

            return new ExtractionSuggestion(null, regex, occurrences, warning, null);
        }

        if (!string.IsNullOrWhiteSpace(node.ElementTagName))
        {
            var tagEsc = EscapeLiteralWithWhitespace(node.ElementTagName);
            var anchorPattern = $@"<{tagEsc}(?:\s+[^>]*)?>";
            var regex = $@"<{tagEsc}(?:\s+[^>]*)?>([\s\S]*?)</{tagEsc}>";

            var occurrences = CountRegexMatches(rawText, anchorPattern);
            var warning = occurrences > 1
                ? $"La balise <{node.ElementTagName}> apparaît {occurrences} fois : le regex capturera la première occurrence."
                : null;

            return new ExtractionSuggestion(null, regex, occurrences, warning, null);
        }

        return new ExtractionSuggestion(null, null, 0, null, "Impossible de générer un regex pour ce nœud XML.");
    }

    internal static string EscapeLiteralWithWhitespace(string literal)
    {
        if (string.IsNullOrEmpty(literal))
        {
            return string.Empty;
        }

        var result = new StringBuilder(literal.Length * 2);
        var token = new StringBuilder();
        var inWhitespace = false;

        foreach (var ch in literal)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (token.Length > 0)
                {
                    result.Append(Regex.Escape(token.ToString()));
                    token.Clear();
                }

                if (!inWhitespace)
                {
                    result.Append(@"\s+");
                    inWhitespace = true;
                }
            }
            else
            {
                inWhitespace = false;
                token.Append(ch);
            }
        }

        if (token.Length > 0)
        {
            result.Append(Regex.Escape(token.ToString()));
        }

        return result.ToString();
    }

    private static int CountRegexMatches(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
        {
            return 0;
        }

        try
        {
            return Regex.Matches(text, pattern, RegexOptions.CultureInvariant, RegexTimeout).Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var singleLine = value.Replace("\r\n", " ", StringComparison.Ordinal)
                              .Replace('\n', ' ')
                              .Replace('\r', ' ');
        return singleLine.Length <= maxChars
            ? singleLine
            : $"{singleLine[..(maxChars - 1)]}…";
    }
}
