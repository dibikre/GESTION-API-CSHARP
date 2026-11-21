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
            if (LooksLikeHtmlDocument(trimmed))
            {
                return ResponseFormat.Html;
            }

            return ResponseFormat.Xml;
        }

        if (LooksLikeHtmlDocument(trimmed))
        {
            return ResponseFormat.Html;
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
        var htmlContext = LooksLikeMarkupForSelection(text)
            ? HtmlDocumentParser.LocateSelection(text, safeStart, safeLength)
            : null;
        var htmlPath = htmlContext?.HtmlPath;

        if (valueKind == ExtractorValueKind.Auto)
        {
            return BuildSmartAutoSuggestion(
                text,
                safeStart,
                safeLength,
                safeContextLen,
                useTrailingAnchor,
                htmlContext);
        }

        var ctxStart = Math.Max(0, safeStart - safeContextLen);
        var beforeText = text[ctxStart..safeStart];
        var leadingAnchor = EscapeLiteralWithWhitespace(beforeText);

        string captureGroup;
        if (valueKind == ExtractorValueKind.Custom)
        {
            var trimmedCustom = customPattern?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmedCustom))
            {
                return new ExtractionSuggestion(null, null, 0, null, "Saisis un motif personnalisé.", htmlPath);
            }

            try
            {
                _ = new Regex(trimmedCustom, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return new ExtractionSuggestion(null, null, 0, null, $"Motif personnalisé invalide : {ex.Message}", htmlPath);
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
                null,
                htmlPath);
        }

        var finalRegex = $"{leadingAnchor}{captureGroup}{trailingAnchor}";
        if (!IsValidRegex(finalRegex))
        {
            return new ExtractionSuggestion(null, null, 0, null, "Le motif généré est invalide.", htmlPath);
        }

        var occurrences = CountRegexMatches(text, finalRegex);
        var warning = occurrences > 1
            ? $"Ce motif correspond à {occurrences} passages dans le texte : augmente la longueur du contexte ou active l'ancre de fin."
            : null;

        return new ExtractionSuggestion(null, finalRegex, occurrences, warning, null, htmlPath);
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
                    var childPath = RealtimeExtractionCatalog.FormatJsonPathChild(jsonPath, prop.Name);
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

            var rootNode = BuildXmlNode(doc.Root, parentPath: string.Empty, siblings: new[] { doc.Root }, depth: 0);
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
            var parsed = HtmlDocumentParser.Parse(text);
            var nodes = HtmlDocumentParser.BuildExtractorTree(text, parsed);
            if (nodes.Count > 0)
            {
                return new FormatAnalysisResult(ResponseFormat.Html, FallbackToText: false, StatusMessage: null, RootNodes: nodes);
            }

            // Repli XML si fragment XML pur
            var preprocessed = PreprocessHtmlForXml(text);
            var doc = LoadXmlDocument(preprocessed);
            if (doc.Root is not null)
            {
                var rootNode = BuildXmlNode(doc.Root, parentPath: string.Empty, siblings: new[] { doc.Root }, depth: 0);
                return new FormatAnalysisResult(
                    ResponseFormat.Html,
                    FallbackToText: false,
                    StatusMessage: null,
                    RootNodes: new ObservableCollection<ExtractorNode> { rootNode });
            }

            return new FormatAnalysisResult(
                ResponseFormat.Text,
                FallbackToText: true,
                StatusMessage: HtmlFallbackMessage,
                RootNodes: []);
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

    private static ExtractorNode BuildXmlNode(
        XElement element,
        string parentPath,
        IReadOnlyList<XElement> siblings,
        int depth)
    {
        var prefix = element.Name.Namespace != XNamespace.None
            ? element.GetPrefixOfNamespace(element.Name.Namespace)
            : null;
        var qualifiedTag = string.IsNullOrEmpty(prefix)
            ? element.Name.LocalName
            : $"{prefix}:{element.Name.LocalName}";

        var sameTagCount = 0;
        var sameTagIndex = 0;
        foreach (var sib in siblings)
        {
            if (sib.Name == element.Name)
            {
                sameTagCount++;
                if (ReferenceEquals(sib, element))
                {
                    sameTagIndex = sameTagCount;
                }
            }
        }

        var pathSegment = sameTagCount > 1
            ? $"{element.Name.LocalName}[{sameTagIndex}]"
            : element.Name.LocalName;
        var elementPath = $"{parentPath}/{pathSegment}";

        var hasChildElements = element.HasElements;
        var scalarText = !hasChildElements ? element.Value.Trim() : string.Empty;

        var label = !hasChildElements && !string.IsNullOrEmpty(scalarText)
            ? $"<{qualifiedTag}>{Truncate(scalarText, PreviewMaxChars)}</{qualifiedTag}>"
            : $"<{qualifiedTag}>";

        var idAttr = element.Attribute("id")?.Value;
        var nameAttr = element.Attribute("name")?.Value;
        var propAttr = element.Attribute("property")?.Value;
        var relAttr = element.Attribute("rel")?.Value;

        var node = new ExtractorNode
        {
            Label = label,
            KindTag = "xml",
            NodeKind = ExtractorNodeKind.XmlElement,
            HtmlPath = elementPath,
            ElementTagName = qualifiedTag,
            IdAttribute = idAttr,
            NameAttribute = nameAttr,
            PropertyAttribute = propAttr,
            RelAttribute = relAttr,
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
                HtmlPath = $"{elementPath}/@{attr.Name.LocalName}",
                ElementTagName = qualifiedTag,
                AttributeName = qualifiedAttr,
                IdAttribute = idAttr,
                NameAttribute = nameAttr,
                PropertyAttribute = propAttr,
                RelAttribute = relAttr,
                RawScalarValue = attr.Value,
                IsLeaf = true,
                IsSelectable = true,
                IsExpanded = false
            });
        }

        var childElements = new List<XElement>(element.Elements());
        foreach (var child in childElements)
        {
            node.Children.Add(BuildXmlNode(child, elementPath, childElements, depth + 1));
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
        var htmlPath = string.IsNullOrWhiteSpace(node.HtmlPath) ? null : node.HtmlPath;
        var distinguishingFilter = GetDistinguishingAttributeLookahead(node, excludeAttrName: node.AttributeName);

        if (node.NodeKind == ExtractorNodeKind.XmlAttribute && !string.IsNullOrWhiteSpace(node.AttributeName))
        {
            var attrEsc = EscapeLiteralWithWhitespace(node.AttributeName);
            string regex;
            string anchorPattern;

            if (!string.IsNullOrWhiteSpace(node.ElementTagName))
            {
                var tagEsc = EscapeLiteralWithWhitespace(node.ElementTagName);
                anchorPattern = $@"<{tagEsc}\b{distinguishingFilter}[^>]*\b{attrEsc}\s*=";
                regex = $@"<{tagEsc}\b{distinguishingFilter}[^>]*\b{attrEsc}\s*=\s*[""']([^""']*)[""']";
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

            return new ExtractionSuggestion(null, regex, occurrences, warning, null, htmlPath);
        }

        if (!string.IsNullOrWhiteSpace(node.ElementTagName))
        {
            var tagEsc = EscapeLiteralWithWhitespace(node.ElementTagName);
            var anchorPattern = string.IsNullOrEmpty(distinguishingFilter)
                ? $@"<{tagEsc}(?:\s+[^>]*)?>"
                : $@"<{tagEsc}\b{distinguishingFilter}[^>]*>";
            var regex = string.IsNullOrEmpty(distinguishingFilter)
                ? $@"<{tagEsc}(?:\s+[^>]*)?>([\s\S]*?)</{tagEsc}>"
                : $@"<{tagEsc}\b{distinguishingFilter}[^>]*>([\s\S]*?)</{tagEsc}>";

            var occurrences = CountRegexMatches(rawText, anchorPattern);
            var warning = occurrences > 1
                ? $"La balise <{node.ElementTagName}> apparaît {occurrences} fois : le regex capturera la première occurrence."
                : null;

            return new ExtractionSuggestion(null, regex, occurrences, warning, null, htmlPath);
        }

        return new ExtractionSuggestion(null, null, 0, null, "Impossible de générer un regex pour ce nœud XML/HTML.", htmlPath);
    }

    private static ExtractionSuggestion BuildSmartAutoSuggestion(
        string text,
        int safeStart,
        int safeLength,
        int safeContextLen,
        bool useTrailingAnchor,
        HtmlSelectionContext? htmlContext)
    {
        var htmlPath = htmlContext?.HtmlPath;
        var selectedText = text.Substring(safeStart, safeLength);
        var beforeWindowStart = Math.Max(0, safeStart - Math.Max(safeContextLen, 96));
        var beforeWindow = text[beforeWindowStart..safeStart];
        var afterStart = safeStart + safeLength;
        var afterWindowEnd = Math.Min(text.Length, afterStart + Math.Max(safeContextLen, 32));
        var afterWindow = text[afterStart..afterWindowEnd];

        var prevChar = safeStart > 0 ? text[safeStart - 1] : (char?)null;
        var nextChar = afterStart < text.Length ? text[afterStart] : (char?)null;
        var isEscapedJsonString = beforeWindow.EndsWith("\\\"", StringComparison.Ordinal);

        string? smartRegex = null;

        // 1. JSON échappé dans un <script> HTML : \"hash\":\"3b55...\"
        var escStrMatch = Regex.Match(beforeWindow, @"\\""([A-Za-z0-9_.\-$@]{1,64})\\""\s*:\s*\\""$", RegexOptions.CultureInvariant, RegexTimeout);
        if (escStrMatch.Success)
        {
            var key = escStrMatch.Groups[1].Value;
            var cap = InferSmartCaptureGroup(selectedText, '"', isEscapedJsonString ? '\\' : nextChar, insideEscapedJson: true);
            var suffix = useTrailingAnchor || afterWindow.StartsWith("\\\"", StringComparison.Ordinal) ? @"\\""" : string.Empty;
            smartRegex = $@"\\""{EscapeLiteralWithWhitespace(key)}\\"":\s*\\""{cap}{suffix}";
        }

        // 2. JSON échappé non-chaîne : \"count\":16
        if (smartRegex is null)
        {
            var escPrimMatch = Regex.Match(beforeWindow, @"\\""([A-Za-z0-9_.\-$@]{1,64})\\""\s*:\s*$", RegexOptions.CultureInvariant, RegexTimeout);
            if (escPrimMatch.Success)
            {
                var key = escPrimMatch.Groups[1].Value;
                var cap = InferSmartCaptureGroup(selectedText, prevChar, nextChar, insideEscapedJson: true);
                smartRegex = $@"\\""{EscapeLiteralWithWhitespace(key)}\\"":\s*{cap}";
            }
        }

        // 3. JSON standard : "hash": "3b55..."
        if (smartRegex is null)
        {
            var jsonStrMatch = Regex.Match(beforeWindow, @"(?<!\\)""([A-Za-z0-9_.\-$@]{1,64})""\s*:\s*""$", RegexOptions.CultureInvariant, RegexTimeout);
            if (jsonStrMatch.Success)
            {
                var key = jsonStrMatch.Groups[1].Value;
                var cap = InferSmartCaptureGroup(selectedText, '"', nextChar, insideEscapedJson: false);
                var suffix = useTrailingAnchor ? @"""" : string.Empty;
                smartRegex = $@"""{EscapeLiteralWithWhitespace(key)}""\s*:\s*""{cap}{suffix}";
            }
        }

        // 4. JSON standard non-chaîne : "id": 123
        if (smartRegex is null)
        {
            var jsonPrimMatch = Regex.Match(beforeWindow, @"(?<!\\)""([A-Za-z0-9_.\-$@]{1,64})""\s*:\s*$", RegexOptions.CultureInvariant, RegexTimeout);
            if (jsonPrimMatch.Success)
            {
                var key = jsonPrimMatch.Groups[1].Value;
                var cap = InferSmartCaptureGroup(selectedText, prevChar, nextChar, insideEscapedJson: false);
                smartRegex = $@"""{EscapeLiteralWithWhitespace(key)}""\s*:\s*{cap}";
            }
        }

        // 5. Attribut HTML : <meta property="og:title" content="Disney Aladdin GIF">
        if (smartRegex is null && htmlContext is { IsInsideAttribute: true, Attribute: not null } attrCtx)
        {
            var attr = attrCtx.Attribute;
            var tagEsc = EscapeLiteralWithWhitespace(attrCtx.Element.TagName);
            var attrEsc = EscapeLiteralWithWhitespace(attr.Name);
            var distinguishing = GetDistinguishingAttributeLookahead(attrCtx.Element, excludeAttrName: attr.Name);
            var selectedWholeAttr = safeStart == attr.ValueStartOffset && afterStart == attr.ValueEndOffset;

            if (selectedWholeAttr)
            {
                var cap = InferSmartCaptureGroup(selectedText, attr.QuoteChar, attr.QuoteChar, insideEscapedJson: false);
                var quoteClass = attr.QuoteChar switch
                {
                    '"' => @"""",
                    '\'' => "'",
                    _ => @"[""']?"
                };
                var endQuote = useTrailingAnchor ? quoteClass : string.Empty;
                smartRegex = $@"<{tagEsc}\b{distinguishing}[^>]*\b{attrEsc}\s*=\s*{quoteClass}{cap}{endQuote}";
            }
        }

        // 6. Contenu direct d'une balise HTML : <title>Muppet Wiki</title>
        if (smartRegex is null &&
            htmlContext is { IsInsideAttribute: false, IsInsideRawText: false } &&
            htmlContext.Element.Children.Count == 0 &&
            Regex.IsMatch(beforeWindow, @">\s*$", RegexOptions.CultureInvariant) &&
            Regex.IsMatch(afterWindow, $@"^\s*</{Regex.Escape(htmlContext.Element.TagName)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var tagEsc = EscapeLiteralWithWhitespace(htmlContext.Element.TagName);
            var distinguishing = GetDistinguishingAttributeLookahead(htmlContext.Element, excludeAttrName: null);
            var cap = selectedText.Contains('\n') ? @"([\s\S]*?)" : @"([^<\r\n]+)";
            var closeTag = useTrailingAnchor ? $@"</{tagEsc}>" : string.Empty;
            smartRegex = $@"<{tagEsc}\b{distinguishing}[^>]*>\s*{cap}\s*{closeTag}";
        }

        // 7. Paramètre d'URL ou clé=valeur : token=abc123
        if (smartRegex is null)
        {
            var queryMatch = Regex.Match(beforeWindow, @"(?:^|[?&;\s,])([A-Za-z_][A-Za-z0-9_\-]{0,40})=$", RegexOptions.CultureInvariant, RegexTimeout);
            if (queryMatch.Success)
            {
                var key = queryMatch.Groups[1].Value;
                var cap = InferSmartCaptureGroup(selectedText, '=', nextChar, insideEscapedJson: false);
                smartRegex = $@"(?<![A-Za-z0-9_\-]){EscapeLiteralWithWhitespace(key)}={cap}";
            }
        }

        // 8. Repli contextuel intelligent (toujours borné par les délimiteurs autour de la sélection)
        var ctxStart = Math.Max(0, safeStart - safeContextLen);
        var beforeLiteral = EscapeLiteralWithWhitespace(text[ctxStart..safeStart]);
        var fallbackCap = InferSmartCaptureGroup(selectedText, prevChar, nextChar, insideEscapedJson: isEscapedJsonString);
        var afterLiteral = useTrailingAnchor && afterStart < text.Length
            ? EscapeLiteralWithWhitespace(text[afterStart..Math.Min(text.Length, afterStart + safeContextLen)])
            : string.Empty;
        var literalRegex = $"{beforeLiteral}{fallbackCap}{afterLiteral}";

        var chosenRegex = smartRegex;
        if (chosenRegex is not null && IsValidRegex(chosenRegex))
        {
            var smartInfo = InspectRegexMatches(text, chosenRegex, selectedText);

            // Si l'utilisateur a élargi Contexte (>15) ou si le regex sémantique ne capture pas la sélection en 1re occurrence,
            // vérifier si le contexte littéral désambiguïse mieux tout en capturant exactement la sélection.
            if ((safeContextLen > 15 || (smartInfo.Count > 1 && !string.Equals(smartInfo.FirstValue, selectedText, StringComparison.Ordinal))) &&
                (!string.IsNullOrEmpty(beforeLiteral) || !string.IsNullOrEmpty(afterLiteral)) &&
                IsValidRegex(literalRegex))
            {
                var literalInfo = InspectRegexMatches(text, literalRegex, selectedText);
                if (literalInfo.Count > 0 &&
                    literalInfo.MatchedSelected &&
                    (literalInfo.Count < smartInfo.Count ||
                     (string.Equals(literalInfo.FirstValue, selectedText, StringComparison.Ordinal) &&
                      !string.Equals(smartInfo.FirstValue, selectedText, StringComparison.Ordinal))))
                {
                    chosenRegex = literalRegex;
                }
            }
            else if (smartInfo.Count == 0 || !smartInfo.MatchedSelected)
            {
                chosenRegex = literalRegex;
            }
        }
        else
        {
            chosenRegex = literalRegex;
        }

        if (string.IsNullOrWhiteSpace(chosenRegex) || !IsValidRegex(chosenRegex))
        {
            return new ExtractionSuggestion(null, null, 0, null, "Impossible de générer un regex valide pour cette sélection.", htmlPath);
        }

        var finalInfo = InspectRegexMatches(text, chosenRegex, selectedText);
        if (finalInfo.Count == 0)
        {
            // Ultime repli sûr avec échappement strict des délimiteurs
            var strictCap = selectedText.Contains('\n') ? @"([\s\S]+?)" : @"([^\r\n]+?)";
            var safeAfter = afterStart < text.Length
                ? EscapeLiteralWithWhitespace(text[afterStart..Math.Min(text.Length, afterStart + Math.Min(safeContextLen, 8))])
                : string.Empty;
            var strictRegex = $"{beforeLiteral}{strictCap}{safeAfter}";
            if (!string.IsNullOrEmpty(beforeLiteral) && IsValidRegex(strictRegex))
            {
                chosenRegex = strictRegex;
                finalInfo = InspectRegexMatches(text, chosenRegex, selectedText);
            }
        }

        string? warning = null;
        if (finalInfo.Count > 1)
        {
            warning = string.Equals(finalInfo.FirstValue, selectedText, StringComparison.Ordinal)
                ? $"Ce motif correspond à {finalInfo.Count} passages dans le texte : augmente la longueur du contexte ou active l'ancre de fin."
                : $"Ce motif correspond à {finalInfo.Count} passages (la 1re occurrence est différente de ta sélection) : augmente le contexte ou active l'ancre de fin.";
        }

        return new ExtractionSuggestion(null, chosenRegex, finalInfo.Count, warning, null, htmlPath);
    }

    private static string InferSmartCaptureGroup(
        string selectedText,
        char? prevChar,
        char? nextChar,
        bool insideEscapedJson)
    {
        if (Regex.IsMatch(selectedText, @"^\d+$", RegexOptions.CultureInvariant))
        {
            return @"(\d+)";
        }

        if (Regex.IsMatch(selectedText, @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant))
        {
            return @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})";
        }

        if (selectedText.Length >= 8 && Regex.IsMatch(selectedText, @"^[0-9a-fA-F]+$", RegexOptions.CultureInvariant))
        {
            return @"([0-9a-fA-F]+)";
        }

        if (insideEscapedJson && !selectedText.Contains('\\') && !selectedText.Contains('"'))
        {
            return @"([^""\\]+)";
        }

        if (prevChar == '"' && nextChar == '"' && !selectedText.Contains('"'))
        {
            return selectedText.Contains('\\')
                ? @"([^""\\]*(?:\\.[^""\\]*)*)"
                : @"([^""]+)";
        }

        if (prevChar == '\'' && nextChar == '\'' && !selectedText.Contains('\''))
        {
            return @"([^']+)";
        }

        if (prevChar == '>' && nextChar == '<' && !selectedText.Contains('<'))
        {
            return selectedText.Contains('\n') ? @"([^<]+)" : @"([^<\r\n]+)";
        }

        var stopChars = new StringBuilder();
        var hasSpace = false;
        foreach (var ch in selectedText)
        {
            if (char.IsWhiteSpace(ch))
            {
                hasSpace = true;
                break;
            }
        }

        if (!hasSpace)
        {
            stopChars.Append(@"\s");
        }
        else
        {
            stopChars.Append(@"\r\n");
        }

        ReadOnlySpan<char> candidateStops = ['"', '\'', '<', '>', '&', ',', ';', '}', ']', ')', '\\', '/', '?', '='];
        foreach (var stop in candidateStops)
        {
            if (nextChar == stop && !selectedText.Contains(stop))
            {
                stopChars.Append(EscapeCharForCharClass(stop));
            }
        }

        // Toujours borner par les guillemets / chevrons si absents de la sélection
        if (!selectedText.Contains('"') && nextChar != '"')
        {
            stopChars.Append('"');
        }
        if (!selectedText.Contains('<') && nextChar != '<')
        {
            stopChars.Append('<');
        }

        return $"([^{stopChars}]+)";
    }

    private static string EscapeCharForCharClass(char ch) => ch switch
    {
        '\\' => @"\\",
        ']' => @"\]",
        '^' => @"\^",
        '-' => @"\-",
        _ => ch.ToString()
    };

    private static string GetDistinguishingAttributeLookahead(ExtractorNode node, string? excludeAttrName)
    {
        if (!string.IsNullOrWhiteSpace(node.IdAttribute) &&
            !string.Equals(excludeAttrName, "id", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\bid\s*=\s*[""']{Regex.Escape(node.IdAttribute)}[""'])";
        }

        if (!string.IsNullOrWhiteSpace(node.PropertyAttribute) &&
            !string.Equals(excludeAttrName, "property", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\bproperty\s*=\s*[""']{Regex.Escape(node.PropertyAttribute)}[""'])";
        }

        if (!string.IsNullOrWhiteSpace(node.NameAttribute) &&
            !string.Equals(excludeAttrName, "name", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\bname\s*=\s*[""']{Regex.Escape(node.NameAttribute)}[""'])";
        }

        if (!string.IsNullOrWhiteSpace(node.RelAttribute) &&
            !string.Equals(excludeAttrName, "rel", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\brel\s*=\s*[""']{Regex.Escape(node.RelAttribute)}[""'])";
        }

        return string.Empty;
    }

    private static string GetDistinguishingAttributeLookahead(HtmlParsedElement element, string? excludeAttrName)
    {
        var id = element.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id) && !string.Equals(excludeAttrName, "id", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\bid\s*=\s*[""']{Regex.Escape(id)}[""'])";
        }

        var prop = element.GetAttribute("property");
        if (!string.IsNullOrWhiteSpace(prop) && !string.Equals(excludeAttrName, "property", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\bproperty\s*=\s*[""']{Regex.Escape(prop)}[""'])";
        }

        var name = element.GetAttribute("name");
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(excludeAttrName, "name", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\bname\s*=\s*[""']{Regex.Escape(name)}[""'])";
        }

        var rel = element.GetAttribute("rel");
        if (!string.IsNullOrWhiteSpace(rel) && !string.Equals(excludeAttrName, "rel", StringComparison.OrdinalIgnoreCase))
        {
            return $@"(?=[^>]*\brel\s*=\s*[""']{Regex.Escape(rel)}[""'])";
        }

        return string.Empty;
    }

    private static (int Count, string? FirstValue, bool MatchedSelected) InspectRegexMatches(
        string text,
        string pattern,
        string selectedText)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            var matches = regex.Matches(text);
            var count = matches.Count;
            if (count == 0)
            {
                return (0, null, false);
            }

            string? firstVal = null;
            var matchedSelected = false;
            foreach (Match m in matches)
            {
                if (!m.Success)
                {
                    continue;
                }

                var val = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                firstVal ??= val;
                if (string.Equals(val, selectedText, StringComparison.Ordinal))
                {
                    matchedSelected = true;
                    break;
                }
            }

            return (count, firstVal, matchedSelected);
        }
        catch (Exception)
        {
            return (0, null, false);
        }
    }

    private static bool LooksLikeHtmlDocument(string trimmed)
    {
        return trimmed.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<head", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<meta", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("<div", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMarkupForSelection(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart('\uFEFF').TrimStart();
        return trimmed.StartsWith('<') || LooksLikeHtmlDocument(trimmed);
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
