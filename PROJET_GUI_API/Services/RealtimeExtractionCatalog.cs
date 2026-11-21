using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

internal sealed record IndexedSuggestionCandidate(
    string Expression,
    string AnchorKey,
    string SuggestedVariableName,
    string PreviewValue,
    string BadgeText,
    string DetailText,
    bool SuggestsList,
    int MatchCount,
    int Depth,
    bool IsContainer,
    int BasePriority,
    string? TargetHeaderName = null);

internal sealed class ResponseSuggestionIndex
{
    public ResponseSuggestionIndex(
        string body,
        ResponseFormat detectedFormat,
        IReadOnlyList<IndexedSuggestionCandidate> jsonPathCandidates,
        IReadOnlyList<IndexedSuggestionCandidate> bodyRegexCandidates,
        IReadOnlyList<IndexedSuggestionCandidate> headerNameCandidates,
        IReadOnlyList<IndexedSuggestionCandidate> headerRegexCandidates)
    {
        Body = body;
        DetectedFormat = detectedFormat;
        JsonPathCandidates = jsonPathCandidates;
        BodyRegexCandidates = bodyRegexCandidates;
        HeaderNameCandidates = headerNameCandidates;
        HeaderRegexCandidates = headerRegexCandidates;
    }

    public string Body { get; }
    public ResponseFormat DetectedFormat { get; }
    public IReadOnlyList<IndexedSuggestionCandidate> JsonPathCandidates { get; }
    public IReadOnlyList<IndexedSuggestionCandidate> BodyRegexCandidates { get; }
    public IReadOnlyList<IndexedSuggestionCandidate> HeaderNameCandidates { get; }
    public IReadOnlyList<IndexedSuggestionCandidate> HeaderRegexCandidates { get; }
}

internal static class RealtimeExtractionCatalog
{
    private const int MaxJsonCandidates = 700;
    private const int MaxJsonDepth = 16;
    private const int MaxArrayItemsIndexed = 5;
    private const int MaxWildcardSampleItems = 3;
    private const int MaxTextScanChars = 131_072;
    private const int PreviewMaxChars = 64;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly JsonDocumentOptions JsonParseOptions = new() { MaxDepth = 1024 };
    private static readonly ConditionalWeakTable<ResponseModel, ResponseSuggestionIndex> Cache = new();

    private static readonly Regex BearerRegex = new(
        @"Bearer\s+([A-Za-z0-9\-._~+/]+=*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex JwtRegex = new(
        @"(eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex UrlRegex = new(
        @"(https?://[^\s""'<>\\]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex UuidRegex = new(
        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex EmailRegex = new(
        @"([a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,})",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex QueryPairRegex = new(
        @"(?:^|[?&;\s])([A-Za-z_][A-Za-z0-9_\-]{1,31})=([^&\s""'<>#]+)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex KeyColonLineRegex = new(
        @"^([A-Za-z][A-Za-z0-9_\-]{1,31}):\s*([^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex CookiePairRegex = new(
        @"^([A-Za-z0-9_.\-]+)=([^;\r\n]*)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    public static ResponseSuggestionIndex GetOrBuild(
        ResponseModel response,
        IExtractionSuggester suggester)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(suggester);

        return Cache.GetValue(response, r => BuildIndex(r, suggester));
    }

    private static ResponseSuggestionIndex BuildIndex(
        ResponseModel response,
        IExtractionSuggester suggester)
    {
        var body = response.Body ?? string.Empty;
        var detectedFormat = DetectEffectiveFormat(body, response.ContentType, suggester);

        var jsonPathCandidates = new List<IndexedSuggestionCandidate>();
        var bodyRegexCandidates = new List<IndexedSuggestionCandidate>();
        var seenRegexExpressions = new HashSet<string>(StringComparer.Ordinal);

        if (detectedFormat == ResponseFormat.Json && !string.IsNullOrWhiteSpace(body))
        {
            IndexJson(body, jsonPathCandidates, bodyRegexCandidates, seenRegexExpressions);
        }
        else if (detectedFormat is ResponseFormat.Xml or ResponseFormat.Html && !string.IsNullOrWhiteSpace(body))
        {
            IndexMarkup(body, detectedFormat, suggester, bodyRegexCandidates, seenRegexExpressions);
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            IndexUniversalPatterns(body, detectedFormat, bodyRegexCandidates, seenRegexExpressions);
        }

        var headerNameCandidates = new List<IndexedSuggestionCandidate>();
        var headerRegexCandidates = new List<IndexedSuggestionCandidate>();
        IndexHeaders(response.Headers, headerNameCandidates, headerRegexCandidates);

        return new ResponseSuggestionIndex(
            body,
            detectedFormat,
            jsonPathCandidates,
            bodyRegexCandidates,
            headerNameCandidates,
            headerRegexCandidates);
    }

    private static ResponseFormat DetectEffectiveFormat(
        string body,
        string? contentType,
        IExtractionSuggester suggester)
    {
        var byBody = suggester.DetectFormat(body, ResponseFormat.Auto);
        if (byBody != ResponseFormat.Text)
        {
            return byBody;
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseFormat.Json;
            }
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseFormat.Html;
            }
            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return ResponseFormat.Xml;
            }
        }

        return byBody;
    }

    private static void IndexJson(
        string body,
        List<IndexedSuggestionCandidate> jsonCandidates,
        List<IndexedSuggestionCandidate> regexCandidates,
        HashSet<string> seenRegexExpressions)
    {
        try
        {
            var normalized = body.TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(normalized, JsonParseOptions);

            var seenJsonPaths = new HashSet<string>(StringComparer.Ordinal);
            var keyStats = new Dictionary<string, JsonKeyStats>(StringComparer.Ordinal);
            var wildcardCollector = new Dictionary<string, WildcardProjectionStats>(StringComparer.Ordinal);

            WalkJson(
                doc.RootElement,
                path: string.Empty,
                keyName: null,
                depth: 0,
                jsonCandidates,
                seenJsonPaths,
                keyStats,
                wildcardCollector,
                wildcardBasePath: null);

            foreach (var projection in wildcardCollector.Values)
            {
                if (jsonCandidates.Count >= MaxJsonCandidates)
                {
                    break;
                }

                if (!seenJsonPaths.Add(projection.WildcardPath))
                {
                    continue;
                }

                var sampleText = string.Join(", ", projection.Samples);
                var preview = Truncate($"[{projection.ArrayLength} élém.] {sampleText}", PreviewMaxChars);
                jsonCandidates.Add(new IndexedSuggestionCandidate(
                    Expression: projection.WildcardPath,
                    AnchorKey: projection.LeafKey,
                    SuggestedVariableName: SanitizeVariableName(projection.LeafKey, "liste"),
                    PreviewValue: preview,
                    BadgeText: $"liste ({projection.ArrayLength})",
                    DetailText: $"Liste JSON · {projection.ArrayLength} élément(s)",
                    SuggestsList: true,
                    MatchCount: projection.ArrayLength,
                    Depth: projection.Depth,
                    IsContainer: false,
                    BasePriority: 85));
            }

            foreach (var stat in keyStats.Values)
            {
                var escapedKey = ExtractionSuggester.EscapeLiteralWithWhitespace(stat.Key);
                var keyAnchor = $@"""{escapedKey}""\s*:";
                var pattern = stat.Kind == JsonValueKind.String
                    ? $@"{keyAnchor}\s*""([^""\\]*(?:\\.[^""\\]*)*)"""
                    : $@"{keyAnchor}\s*([^\s,\}}\]]+)";

                if (!seenRegexExpressions.Add(pattern))
                {
                    continue;
                }

                var countLabel = stat.Count <= 1 ? "1 corr." : $"{stat.Count} corr.";
                regexCandidates.Add(new IndexedSuggestionCandidate(
                    Expression: pattern,
                    AnchorKey: stat.Key,
                    SuggestedVariableName: SanitizeVariableName(stat.Key, "valeur"),
                    PreviewValue: stat.FirstPreview,
                    BadgeText: $"json · {countLabel}",
                    DetailText: stat.Count > 1
                        ? $"Clé JSON « {stat.Key} » ({stat.Count} occurrences)"
                        : $"Clé JSON « {stat.Key} »",
                    SuggestsList: stat.Count > 1,
                    MatchCount: stat.Count,
                    Depth: stat.FirstDepth,
                    IsContainer: false,
                    BasePriority: stat.Count == 1 ? 92 : 76));
            }
        }
        catch (JsonException)
        {
            // Ignore malformed JSON gracefully; universal text patterns remain available.
        }
    }

    private static void WalkJson(
        JsonElement element,
        string path,
        string? keyName,
        int depth,
        List<IndexedSuggestionCandidate> jsonCandidates,
        HashSet<string> seenJsonPaths,
        Dictionary<string, JsonKeyStats> keyStats,
        Dictionary<string, WildcardProjectionStats> wildcardCollector,
        string? wildcardBasePath,
        int? currentArrayLength = null)
    {
        if (depth > MaxJsonDepth || jsonCandidates.Count >= MaxJsonCandidates)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (!string.IsNullOrEmpty(path) && depth <= 5 && seenJsonPaths.Add(path))
                {
                    var propCount = 0;
                    foreach (var _ in element.EnumerateObject())
                    {
                        propCount++;
                    }

                    jsonCandidates.Add(new IndexedSuggestionCandidate(
                        Expression: path,
                        AnchorKey: keyName ?? path,
                        SuggestedVariableName: SanitizeVariableName(keyName ?? "objet", "objet"),
                        PreviewValue: $"{{ {propCount} propriété{(propCount > 1 ? "s" : string.Empty)} }}",
                        BadgeText: "obj",
                        DetailText: "Objet JSON",
                        SuggestsList: false,
                        MatchCount: 1,
                        Depth: depth,
                        IsContainer: true,
                        BasePriority: 35));
                }

                foreach (var prop in element.EnumerateObject())
                {
                    if (jsonCandidates.Count >= MaxJsonCandidates)
                    {
                        break;
                    }

                    var childPath = FormatJsonPathChild(path, prop.Name);
                    var childWildcard = wildcardBasePath is null
                        ? null
                        : FormatJsonPathChild(wildcardBasePath, prop.Name);

                    WalkJson(
                        prop.Value,
                        childPath,
                        prop.Name,
                        depth + 1,
                        jsonCandidates,
                        seenJsonPaths,
                        keyStats,
                        wildcardCollector,
                        childWildcard,
                        currentArrayLength);
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var len = element.GetArrayLength();
                if (!string.IsNullOrEmpty(path) && seenJsonPaths.Add(path))
                {
                    var previewItems = new List<string>(MaxWildcardSampleItems);
                    var sampleIdx = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (sampleIdx >= MaxWildcardSampleItems)
                        {
                            break;
                        }
                        previewItems.Add(FormatElementShortPreview(item));
                        sampleIdx++;
                    }

                    var summary = len == 0
                        ? "[]"
                        : $"[{len} élém.] {string.Join(", ", previewItems)}";

                    jsonCandidates.Add(new IndexedSuggestionCandidate(
                        Expression: path,
                        AnchorKey: keyName ?? path,
                        SuggestedVariableName: SanitizeVariableName(keyName ?? "liste", "liste"),
                        PreviewValue: Truncate(summary, PreviewMaxChars),
                        BadgeText: $"arr ({len})",
                        DetailText: $"Tableau JSON · {len} élément(s)",
                        SuggestsList: len > 0,
                        MatchCount: len,
                        Depth: depth,
                        IsContainer: true,
                        BasePriority: len > 0 ? 74 : 25));
                }

                if (!string.IsNullOrEmpty(path) || depth == 0)
                {
                    var arrayWildcardRoot = string.IsNullOrEmpty(path) ? "[*]" : $"{path}[*]";
                    var idx = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (idx >= MaxArrayItemsIndexed)
                        {
                            break;
                        }

                        var elemPath = string.IsNullOrEmpty(path) ? $"[{idx}]" : $"{path}[{idx}]";
                        WalkJson(
                            item,
                            elemPath,
                            $"[{idx}]",
                            depth + 1,
                            jsonCandidates,
                            seenJsonPaths,
                            keyStats,
                            wildcardCollector,
                            idx < MaxWildcardSampleItems ? arrayWildcardRoot : null,
                            currentArrayLength: len);
                        idx++;
                    }
                }

                break;
            }

            default:
            {
                var rawScalar = element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
                var truncatedPreview = Truncate(rawScalar, PreviewMaxChars);
                var kindTag = element.ValueKind switch
                {
                    JsonValueKind.String => "str",
                    JsonValueKind.Number => "num",
                    JsonValueKind.True or JsonValueKind.False => "bool",
                    JsonValueKind.Null => "null",
                    _ => "val"
                };

                if (!string.IsNullOrEmpty(path) && seenJsonPaths.Add(path))
                {
                    var anchor = keyName is not null && !keyName.StartsWith('[') ? keyName : path;
                    jsonCandidates.Add(new IndexedSuggestionCandidate(
                        Expression: path,
                        AnchorKey: anchor,
                        SuggestedVariableName: SanitizeVariableName(anchor, "valeur"),
                        PreviewValue: truncatedPreview,
                        BadgeText: kindTag,
                        DetailText: $"JSONPath · {path}",
                        SuggestsList: false,
                        MatchCount: 1,
                        Depth: depth,
                        IsContainer: false,
                        BasePriority: element.ValueKind == JsonValueKind.Null ? 55 : 90));
                }

                if (!string.IsNullOrEmpty(wildcardBasePath) &&
                    keyName is not null &&
                    !keyName.StartsWith('['))
                {
                    if (!wildcardCollector.TryGetValue(wildcardBasePath, out var proj))
                    {
                        proj = new WildcardProjectionStats(
                            wildcardBasePath,
                            keyName,
                            currentArrayLength ?? 0,
                            depth);
                        wildcardCollector[wildcardBasePath] = proj;
                    }

                    if (proj.Samples.Count < MaxWildcardSampleItems && !string.IsNullOrEmpty(truncatedPreview))
                    {
                        proj.Samples.Add(truncatedPreview);
                    }
                }

                if (keyName is not null && !keyName.StartsWith('['))
                {
                    if (!keyStats.TryGetValue(keyName, out var stats))
                    {
                        keyStats[keyName] = new JsonKeyStats(keyName, element.ValueKind, truncatedPreview, depth);
                    }
                    else
                    {
                        stats.Count++;
                    }
                }

                break;
            }
        }
    }

    private static void IndexMarkup(
        string body,
        ResponseFormat format,
        IExtractionSuggester suggester,
        List<IndexedSuggestionCandidate> regexCandidates,
        HashSet<string> seenRegexExpressions)
    {
        var analysis = suggester.Analyze(body, format);
        if (analysis.FallbackToText || analysis.RootNodes.Count == 0)
        {
            return;
        }

        var stack = new Stack<(ExtractorNode Node, int Depth)>();
        for (var i = analysis.RootNodes.Count - 1; i >= 0; i--)
        {
            stack.Push((analysis.RootNodes[i], 0));
        }

        var count = 0;
        while (stack.Count > 0 && count < 300)
        {
            var (node, depth) = stack.Pop();
            if (node.IsSelectable)
            {
                var suggestion = suggester.SuggestForNode(body, node);
                if (!string.IsNullOrWhiteSpace(suggestion.Regex) &&
                    seenRegexExpressions.Add(suggestion.Regex))
                {
                    var key = node.AttributeName ?? node.ElementTagName ?? "valeur";
                    var occ = Math.Max(1, suggestion.OccurrenceCount);
                    regexCandidates.Add(new IndexedSuggestionCandidate(
                        Expression: suggestion.Regex,
                        AnchorKey: key,
                        SuggestedVariableName: SanitizeVariableName(key, "valeur"),
                        PreviewValue: Truncate(node.RawScalarValue ?? string.Empty, PreviewMaxChars),
                        BadgeText: $"xml · {occ} corr.",
                        DetailText: node.AttributeName is not null
                            ? $"Attribut @{node.AttributeName} sur <{node.ElementTagName}>"
                            : $"Balise <{node.ElementTagName}>",
                        SuggestsList: occ > 1,
                        MatchCount: occ,
                        Depth: depth,
                        IsContainer: false,
                        BasePriority: occ == 1 ? 88 : 74));
                    count++;
                }
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((node.Children[i], depth + 1));
            }
        }
    }

    private static void IndexUniversalPatterns(
        string body,
        ResponseFormat detectedFormat,
        List<IndexedSuggestionCandidate> regexCandidates,
        HashSet<string> seenRegexExpressions)
    {
        var sample = body.Length > MaxTextScanChars ? body[..MaxTextScanChars] : body;

        TryAddPatternCandidate(
            sample,
            BearerRegex,
            pattern: @"Bearer\s+([A-Za-z0-9\-._~+/]+=*)",
            anchorKey: "Bearer token",
            suggestedVar: "token",
            badgePrefix: "bearer",
            detail: "Jeton Bearer détecté",
            priority: 86,
            regexCandidates,
            seenRegexExpressions);

        TryAddPatternCandidate(
            sample,
            JwtRegex,
            pattern: @"(eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})",
            anchorKey: "jwt token",
            suggestedVar: "jwt",
            badgePrefix: "jwt",
            detail: "Jeton JWT détecté",
            priority: 85,
            regexCandidates,
            seenRegexExpressions);

        TryAddPatternCandidate(
            sample,
            UrlRegex,
            pattern: @"(https?://[^\s""'<>\\]+)",
            anchorKey: "url http https",
            suggestedVar: "url",
            badgePrefix: "url",
            detail: "URL HTTP/HTTPS détectée",
            priority: 72,
            regexCandidates,
            seenRegexExpressions);

        TryAddPatternCandidate(
            sample,
            UuidRegex,
            pattern: @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            anchorKey: "uuid guid id",
            suggestedVar: "uuid",
            badgePrefix: "uuid",
            detail: "UUID / GUID détecté",
            priority: 70,
            regexCandidates,
            seenRegexExpressions);

        TryAddPatternCandidate(
            sample,
            EmailRegex,
            pattern: @"([a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,})",
            anchorKey: "email mail",
            suggestedVar: "email",
            badgePrefix: "email",
            detail: "Adresse e-mail détectée",
            priority: 68,
            regexCandidates,
            seenRegexExpressions);

        if (detectedFormat == ResponseFormat.Text)
        {
            IndexKeyValueTextPairs(sample, regexCandidates, seenRegexExpressions);
        }
    }

    private static void IndexKeyValueTextPairs(
        string sample,
        List<IndexedSuggestionCandidate> regexCandidates,
        HashSet<string> seenRegexExpressions)
    {
        try
        {
            var queryMatches = QueryPairRegex.Matches(sample);
            var addedPairs = 0;
            foreach (Match m in queryMatches)
            {
                if (addedPairs >= 25)
                {
                    break;
                }

                var key = m.Groups[1].Value;
                var val = m.Groups[2].Value;
                var pattern = $@"(?:^|[?&;\s]){Regex.Escape(key)}=([^&\s""'<>#]+)";
                if (!seenRegexExpressions.Add(pattern))
                {
                    continue;
                }

                regexCandidates.Add(new IndexedSuggestionCandidate(
                    Expression: pattern,
                    AnchorKey: key,
                    SuggestedVariableName: SanitizeVariableName(key, "valeur"),
                    PreviewValue: Truncate(val, PreviewMaxChars),
                    BadgeText: "param",
                    DetailText: $"Paramètre « {key}=... »",
                    SuggestsList: false,
                    MatchCount: 1,
                    Depth: 1,
                    IsContainer: false,
                    BasePriority: 84));
                addedPairs++;
            }

            var colonMatches = KeyColonLineRegex.Matches(sample);
            var addedLines = 0;
            foreach (Match m in colonMatches)
            {
                if (addedLines >= 25)
                {
                    break;
                }

                var key = m.Groups[1].Value;
                var val = m.Groups[2].Value.Trim();
                var pattern = $@"(?m)^{Regex.Escape(key)}:\s*([^\r\n]+)$";
                if (!seenRegexExpressions.Add(pattern))
                {
                    continue;
                }

                regexCandidates.Add(new IndexedSuggestionCandidate(
                    Expression: pattern,
                    AnchorKey: key,
                    SuggestedVariableName: SanitizeVariableName(key, "valeur"),
                    PreviewValue: Truncate(val, PreviewMaxChars),
                    BadgeText: "ligne",
                    DetailText: $"Ligne « {key}: ... »",
                    SuggestsList: false,
                    MatchCount: 1,
                    Depth: 1,
                    IsContainer: false,
                    BasePriority: 82));
                addedLines++;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Keep UI safe on pathological text.
        }
    }

    private static void TryAddPatternCandidate(
        string sample,
        Regex regex,
        string pattern,
        string anchorKey,
        string suggestedVar,
        string badgePrefix,
        string detail,
        int priority,
        List<IndexedSuggestionCandidate> regexCandidates,
        HashSet<string> seenRegexExpressions)
    {
        try
        {
            var matches = regex.Matches(sample);
            if (matches.Count == 0 || !seenRegexExpressions.Add(pattern))
            {
                return;
            }

            var firstVal = matches[0].Groups.Count > 1 ? matches[0].Groups[1].Value : matches[0].Value;
            regexCandidates.Add(new IndexedSuggestionCandidate(
                Expression: pattern,
                AnchorKey: anchorKey,
                SuggestedVariableName: suggestedVar,
                PreviewValue: Truncate(firstVal, PreviewMaxChars),
                BadgeText: $"{badgePrefix} · {matches.Count}",
                DetailText: $"{detail} ({matches.Count} corr.)",
                SuggestsList: matches.Count > 1,
                MatchCount: matches.Count,
                Depth: 0,
                IsContainer: false,
                BasePriority: priority));
        }
        catch (RegexMatchTimeoutException)
        {
            // Ignore timeout on huge inputs.
        }
    }

    private static void IndexHeaders(
        List<KeyValueItem>? headers,
        List<IndexedSuggestionCandidate> headerNameCandidates,
        List<IndexedSuggestionCandidate> headerRegexCandidates)
    {
        if (headers is null || headers.Count == 0)
        {
            return;
        }

        var groupedByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            if (!h.IsEnabled || string.IsNullOrWhiteSpace(h.Key))
            {
                continue;
            }

            var key = h.Key.Trim();
            if (!groupedByKey.TryGetValue(key, out var vals))
            {
                vals = [];
                groupedByKey[key] = vals;
            }
            vals.Add(h.Value ?? string.Empty);
        }

        foreach (var (key, vals) in groupedByKey)
        {
            var combinedPreview = vals.Count == 1
                ? Truncate(vals[0], PreviewMaxChars)
                : Truncate($"[{vals.Count} valeurs] {string.Join(" ; ", vals)}", PreviewMaxChars);

            headerNameCandidates.Add(new IndexedSuggestionCandidate(
                Expression: key,
                AnchorKey: key,
                SuggestedVariableName: SanitizeVariableName(key, "header"),
                PreviewValue: combinedPreview,
                BadgeText: vals.Count > 1 ? $"en-tête ({vals.Count})" : "en-tête",
                DetailText: $"En-tête HTTP {key}",
                SuggestsList: vals.Count > 1,
                MatchCount: vals.Count,
                Depth: 0,
                IsContainer: false,
                BasePriority: 85,
                TargetHeaderName: key));

            headerRegexCandidates.Add(new IndexedSuggestionCandidate(
                Expression: "(.+)",
                AnchorKey: key,
                SuggestedVariableName: SanitizeVariableName(key, "header"),
                PreviewValue: Truncate(vals[0], PreviewMaxChars),
                BadgeText: "valeur complète",
                DetailText: $"Toute la valeur de {key}",
                SuggestsList: vals.Count > 1,
                MatchCount: vals.Count,
                Depth: 0,
                IsContainer: false,
                BasePriority: 90,
                TargetHeaderName: key));

            foreach (var rawVal in vals)
            {
                if (key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    var cookieMatch = CookiePairRegex.Match(rawVal.TrimStart());
                    if (cookieMatch.Success)
                    {
                        var cookieName = cookieMatch.Groups[1].Value;
                        var cookieVal = cookieMatch.Groups[2].Value;
                        headerRegexCandidates.Add(new IndexedSuggestionCandidate(
                            Expression: $@"{Regex.Escape(cookieName)}=([^;\s]+)",
                            AnchorKey: $"{key} {cookieName}",
                            SuggestedVariableName: SanitizeVariableName(cookieName, "cookie"),
                            PreviewValue: Truncate(cookieVal, PreviewMaxChars),
                            BadgeText: "cookie",
                            DetailText: $"Cookie « {cookieName} » dans {key}",
                            SuggestsList: false,
                            MatchCount: 1,
                            Depth: 1,
                            IsContainer: false,
                            BasePriority: 96,
                            TargetHeaderName: key));
                    }
                }

                var bearer = BearerRegex.Match(rawVal);
                if (bearer.Success)
                {
                    headerRegexCandidates.Add(new IndexedSuggestionCandidate(
                        Expression: @"Bearer\s+([^\s]+)",
                        AnchorKey: $"{key} Bearer token",
                        SuggestedVariableName: "token",
                        PreviewValue: Truncate(bearer.Groups[1].Value, PreviewMaxChars),
                        BadgeText: "bearer",
                        DetailText: $"Jeton Bearer dans {key}",
                        SuggestsList: false,
                        MatchCount: 1,
                        Depth: 1,
                        IsContainer: false,
                        BasePriority: 95,
                        TargetHeaderName: key));
                }

                var urlMatch = UrlRegex.Match(rawVal);
                if (urlMatch.Success)
                {
                    headerRegexCandidates.Add(new IndexedSuggestionCandidate(
                        Expression: @"(https?://[^\s>]+)",
                        AnchorKey: $"{key} url",
                        SuggestedVariableName: SanitizeVariableName(key, "url"),
                        PreviewValue: Truncate(urlMatch.Groups[1].Value, PreviewMaxChars),
                        BadgeText: "url",
                        DetailText: $"URL dans {key}",
                        SuggestsList: false,
                        MatchCount: 1,
                        Depth: 1,
                        IsContainer: false,
                        BasePriority: 91,
                        TargetHeaderName: key));
                }

                foreach (Match qp in QueryPairRegex.Matches(rawVal))
                {
                    var pName = qp.Groups[1].Value;
                    var pVal = qp.Groups[2].Value;
                    headerRegexCandidates.Add(new IndexedSuggestionCandidate(
                        Expression: $@"{Regex.Escape(pName)}=([^&\s""'<>#]+)",
                        AnchorKey: $"{key} {pName}",
                        SuggestedVariableName: SanitizeVariableName(pName, "param"),
                        PreviewValue: Truncate(pVal, PreviewMaxChars),
                        BadgeText: "param",
                        DetailText: $"Paramètre « {pName} » dans {key}",
                        SuggestsList: false,
                        MatchCount: 1,
                        Depth: 1,
                        IsContainer: false,
                        BasePriority: 92,
                        TargetHeaderName: key));
                }
            }
        }
    }

    internal static string SanitizeVariableName(string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var sb = new StringBuilder(raw.Length);
        var prevUnderscore = false;
        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevUnderscore = false;
            }
            else if (!prevUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                prevUnderscore = true;
            }
        }

        var cleaned = sb.ToString().Trim('_');
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        if (char.IsDigit(cleaned[0]))
        {
            cleaned = $"v_{cleaned}";
        }

        return cleaned;
    }

    internal static string Truncate(string? value, int maxChars = PreviewMaxChars)
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

    internal static string FormatJsonPathChild(string parentPath, string propName)
    {
        if (IsSafeJsonPathSegment(propName))
        {
            return string.IsNullOrEmpty(parentPath)
                ? propName
                : $"{parentPath}.{propName}";
        }

        var escaped = propName
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"{parentPath}[\"{escaped}\"]";
    }

    private static bool IsSafeJsonPathSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch) || ch is '.' or '[' or ']' or '"' or '\'' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatElementShortPreview(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Object => "{...}",
        JsonValueKind.Array => "[...]",
        _ => el.GetRawText()
    };

    private sealed class JsonKeyStats
    {
        public JsonKeyStats(string key, JsonValueKind kind, string firstPreview, int firstDepth)
        {
            Key = key;
            Kind = kind;
            FirstPreview = firstPreview;
            FirstDepth = firstDepth;
            Count = 1;
        }

        public string Key { get; }
        public JsonValueKind Kind { get; }
        public string FirstPreview { get; }
        public int FirstDepth { get; }
        public int Count { get; set; }
    }

    private sealed class WildcardProjectionStats
    {
        public WildcardProjectionStats(string wildcardPath, string leafKey, int arrayLength, int depth)
        {
            WildcardPath = wildcardPath;
            LeafKey = leafKey;
            ArrayLength = arrayLength;
            Depth = depth;
        }

        public string WildcardPath { get; }
        public string LeafKey { get; }
        public int ArrayLength { get; }
        public int Depth { get; }
        public List<string> Samples { get; } = [];
    }
}
