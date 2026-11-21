using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

internal static class RealtimeExtractionMatcher
{
    private const int MaxTextScanChars = 131_072;
    private static readonly TimeSpan LiveRegexTimeout = TimeSpan.FromMilliseconds(250);

    public static IReadOnlyList<RealtimeExtractionSuggestion> SuggestHeaderNames(
        ResponseModel response,
        string? query,
        IExtractionSuggester suggester,
        int maxResults)
    {
        var index = RealtimeExtractionCatalog.GetOrBuild(response, suggester);
        if (index.HeaderNameCandidates.Count == 0)
        {
            return [];
        }

        var normQuery = (query ?? string.Empty).Trim();
        var scored = new List<(IndexedSuggestionCandidate Candidate, int Score)>();

        foreach (var candidate in index.HeaderNameCandidates)
        {
            var score = ScoreCandidate(
                candidate,
                normQuery,
                variableName: null,
                isList: false,
                isJsonPath: false);
            if (score > 0)
            {
                scored.Add((candidate, score));
            }
        }

        return FinalizeSuggestions(scored, maxResults);
    }

    public static IReadOnlyList<RealtimeExtractionSuggestion> SuggestRealtime(
        ResponseModel response,
        ChainExtractionSource source,
        ExtractionKind kind,
        string? query,
        string? headerName,
        string? variableName,
        bool isList,
        IExtractionSuggester suggester,
        int maxResults)
    {
        if (kind == ExtractionKind.FullResponse && source != ChainExtractionSource.Header)
        {
            return [];
        }

        var index = RealtimeExtractionCatalog.GetOrBuild(response, suggester);
        var normQuery = (query ?? string.Empty).Trim();
        var normHeader = (headerName ?? string.Empty).Trim();
        var normVar = (variableName ?? string.Empty).Trim();

        if (source == ChainExtractionSource.Header)
        {
            return SuggestForHeader(response, index, normQuery, normHeader, normVar, isList, maxResults);
        }

        if (kind == ExtractionKind.JsonPath)
        {
            return SuggestForJsonPath(index, normQuery, normVar, isList, maxResults);
        }

        return SuggestForBodyRegex(index, normQuery, normVar, isList, maxResults);
    }

    private static IReadOnlyList<RealtimeExtractionSuggestion> SuggestForHeader(
        ResponseModel response,
        ResponseSuggestionIndex index,
        string query,
        string headerName,
        string variableName,
        bool isList,
        int maxResults)
    {
        var scored = new List<(IndexedSuggestionCandidate Candidate, int Score)>();
        var hasHeaderFilter = !string.IsNullOrEmpty(headerName);

        foreach (var candidate in index.HeaderRegexCandidates)
        {
            if (hasHeaderFilter && candidate.TargetHeaderName is not null)
            {
                if (!candidate.TargetHeaderName.Equals(headerName, StringComparison.OrdinalIgnoreCase) &&
                    !candidate.TargetHeaderName.StartsWith(headerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var score = ScoreCandidate(candidate, query, variableName, isList, isJsonPath: false);
            if (score <= 0)
            {
                continue;
            }

            if (hasHeaderFilter &&
                candidate.TargetHeaderName is not null &&
                candidate.TargetHeaderName.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                score += 180;
            }

            scored.Add((candidate, score));
        }

        if (query.Length >= 2 && hasHeaderFilter && response.Headers is not null)
        {
            foreach (var h in response.Headers)
            {
                if (!h.IsEnabled ||
                    !string.Equals(h.Key?.Trim(), headerName, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(h.Value))
                {
                    continue;
                }

                AddLiveRegexSuggestionsForText(
                    h.Value,
                    query,
                    defaultVarName: RealtimeExtractionCatalog.SanitizeVariableName(headerName, "header"),
                    targetHeaderName: h.Key?.Trim(),
                    scored);
                break;
            }
        }

        return FinalizeSuggestions(scored, maxResults);
    }

    private static IReadOnlyList<RealtimeExtractionSuggestion> SuggestForJsonPath(
        ResponseSuggestionIndex index,
        string query,
        string variableName,
        bool isList,
        int maxResults)
    {
        if (index.JsonPathCandidates.Count == 0)
        {
            return [];
        }

        var normQuery = StripJsonPathRootPrefix(query);
        var scored = new List<(IndexedSuggestionCandidate Candidate, int Score)>(index.JsonPathCandidates.Count);

        foreach (var candidate in index.JsonPathCandidates)
        {
            var score = ScoreCandidate(candidate, normQuery, variableName, isList, isJsonPath: true);
            if (score > 0)
            {
                scored.Add((candidate, score));
            }
        }

        return FinalizeSuggestions(scored, maxResults);
    }

    private static IReadOnlyList<RealtimeExtractionSuggestion> SuggestForBodyRegex(
        ResponseSuggestionIndex index,
        string query,
        string variableName,
        bool isList,
        int maxResults)
    {
        var scored = new List<(IndexedSuggestionCandidate Candidate, int Score)>(index.BodyRegexCandidates.Count + 4);

        foreach (var candidate in index.BodyRegexCandidates)
        {
            var score = ScoreCandidate(candidate, query, variableName, isList, isJsonPath: false);
            if (score > 0)
            {
                scored.Add((candidate, score));
            }
        }

        if (query.Length >= 2 && !string.IsNullOrEmpty(index.Body))
        {
            var sample = index.Body.Length > MaxTextScanChars ? index.Body[..MaxTextScanChars] : index.Body;
            AddLiveRegexSuggestionsForText(
                sample,
                query,
                defaultVarName: !string.IsNullOrWhiteSpace(variableName)
                    ? RealtimeExtractionCatalog.SanitizeVariableName(variableName, "valeur")
                    : "valeur",
                targetHeaderName: null,
                scored);
        }

        return FinalizeSuggestions(scored, maxResults);
    }

    private static int ScoreCandidate(
        IndexedSuggestionCandidate candidate,
        string query,
        string? variableName,
        bool isList,
        bool isJsonPath)
    {
        var score = candidate.BasePriority;

        if (isList)
        {
            score += candidate.SuggestsList ? 130 : (candidate.IsContainer ? 10 : -15);
        }
        else if (candidate.SuggestsList)
        {
            score -= 20;
        }

        if (!string.IsNullOrEmpty(variableName))
        {
            if (candidate.AnchorKey.Equals(variableName, StringComparison.OrdinalIgnoreCase) ||
                candidate.SuggestedVariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase))
            {
                score += string.IsNullOrEmpty(query) ? 160 : 50;
            }
            else if (candidate.AnchorKey.Contains(variableName, StringComparison.OrdinalIgnoreCase))
            {
                score += string.IsNullOrEmpty(query) ? 90 : 25;
            }
        }

        if (string.IsNullOrEmpty(query))
        {
            if (candidate.IsContainer && !isList)
            {
                score -= 25;
            }
            return Math.Max(1, score - candidate.Depth * 3);
        }

        var matched = false;

        if (candidate.Expression.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 620;
            matched = true;
        }
        else if (candidate.Expression.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
            if (isJsonPath && query.EndsWith('.'))
            {
                var remainder = candidate.Expression[query.Length..];
                if (!remainder.Contains('.'))
                {
                    score += 180;
                }
            }
            matched = true;
        }
        else if (candidate.AnchorKey.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 440;
            matched = true;
        }
        else if (candidate.AnchorKey.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 370;
            matched = true;
        }
        else if (candidate.AnchorKey.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
            matched = true;
        }
        else if (candidate.Expression.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 260;
            matched = true;
        }
        else if (!string.IsNullOrEmpty(candidate.PreviewValue) &&
                 candidate.PreviewValue.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 200;
            matched = true;
        }
        else if (!string.IsNullOrEmpty(candidate.DetailText) &&
                 candidate.DetailText.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 130;
            matched = true;
        }

        if (!matched)
        {
            return 0;
        }

        return Math.Max(1, score - candidate.Depth * 2);
    }

    private static void AddLiveRegexSuggestionsForText(
        string text,
        string query,
        string defaultVarName,
        string? targetHeaderName,
        List<(IndexedSuggestionCandidate Candidate, int Score)> scored)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return;
        }

        var addedPatterns = new HashSet<string>(StringComparer.Ordinal);

        // 1) Si le texte saisi est déjà une regex valide qui capture ou matche
        if (TryBuildValidRegexFeedback(text, query, defaultVarName, targetHeaderName, addedPatterns, out var liveCandidate, out var liveScore))
        {
            scored.Add((liveCandidate, liveScore));
        }

        // 2) Synthèse contextuelle si la saisie correspond à un passage littéral dans la réponse
        var literalIdx = text.IndexOf(query, StringComparison.Ordinal);
        if (literalIdx < 0)
        {
            literalIdx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        }

        if (literalIdx >= 0)
        {
            TrySynthesizeContextualRegex(
                text,
                literalIdx,
                query.Length,
                defaultVarName,
                targetHeaderName,
                addedPatterns,
                scored);
        }
    }

    private static bool TryBuildValidRegexFeedback(
        string text,
        string pattern,
        string defaultVarName,
        string? targetHeaderName,
        HashSet<string> addedPatterns,
        out IndexedSuggestionCandidate candidate,
        out int score)
    {
        candidate = null!;
        score = 0;

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, LiveRegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            var matches = regex.Matches(text);
            if (matches.Count == 0)
            {
                return false;
            }

            if (regex.GetGroupNumbers().Length > 1)
            {
                var firstMatch = matches[0];
                var captured = firstMatch.Groups.Count > 1 && firstMatch.Groups[1].Success
                    ? firstMatch.Groups[1].Value
                    : firstMatch.Value;

                if (addedPatterns.Add(pattern))
                {
                    candidate = new IndexedSuggestionCandidate(
                        Expression: pattern,
                        AnchorKey: pattern,
                        SuggestedVariableName: defaultVarName,
                        PreviewValue: RealtimeExtractionCatalog.Truncate(captured),
                        BadgeText: $"valide · {matches.Count} corr.",
                        DetailText: "Expression actuelle valide sur la réponse",
                        SuggestsList: matches.Count > 1,
                        MatchCount: matches.Count,
                        Depth: 0,
                        IsContainer: false,
                        BasePriority: 640,
                        TargetHeaderName: targetHeaderName);
                    score = 720;
                    return true;
                }

                return false;
            }

            // Regex valide sans groupe de capture : proposer des complétions de capture
            string[] suffixes =
            [
                @"\s*""([^""\\]*(?:\\.[^""\\]*)*)""",
                @"\s*([^\s""'<>\\,\}\]]+)",
                @"([^\r\n]+)"
            ];

            foreach (var suffix in suffixes)
            {
                var extended = pattern + suffix;
                if (!addedPatterns.Add(extended))
                {
                    continue;
                }

                if (TryEvaluateRegex(text, extended, out var capturedVal, out var count) &&
                    !string.IsNullOrEmpty(capturedVal))
                {
                    candidate = new IndexedSuggestionCandidate(
                        Expression: extended,
                        AnchorKey: pattern,
                        SuggestedVariableName: defaultVarName,
                        PreviewValue: RealtimeExtractionCatalog.Truncate(capturedVal),
                        BadgeText: $"capture · {count} corr.",
                        DetailText: "Complétion avec groupe de capture",
                        SuggestsList: count > 1,
                        MatchCount: count,
                        Depth: 0,
                        IsContainer: false,
                        BasePriority: 610,
                        TargetHeaderName: targetHeaderName);
                    score = 690;
                    return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return false;
    }

    private static void TrySynthesizeContextualRegex(
        string text,
        int matchStart,
        int matchLength,
        string defaultVarName,
        string? targetHeaderName,
        HashSet<string> addedPatterns,
        List<(IndexedSuggestionCandidate Candidate, int Score)> scored)
    {
        var afterStart = matchStart + matchLength;
        var matchedSlice = text.Substring(matchStart, matchLength);

        var lineStart = matchStart <= 0 ? 0 : text.LastIndexOf('\n', matchStart - 1) + 1;
        var lineEnd = afterStart >= text.Length ? text.Length : text.IndexOf('\n', afterStart);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        // Cas A : l'utilisateur tape un préfixe/ancre (ex: `"tracking": ` ou `Bearer ` ou `token=`)
        if (afterStart < lineEnd)
        {
            var restOfLine = text[afterStart..lineEnd];
            var trimmedRest = restOfLine.TrimStart();
            var anchor = ExtractionSuggester.EscapeLiteralWithWhitespace(matchedSlice);
            var hasTrailingSpace = matchedSlice.Length > 0 && char.IsWhiteSpace(matchedSlice[^1]);
            var ws = hasTrailingSpace ? string.Empty : @"\s*";

            string? candidatePattern = null;
            if (trimmedRest.StartsWith('"'))
            {
                candidatePattern = $@"{anchor}{ws}""([^""\\]*(?:\\.[^""\\]*)*)""";
            }
            else if (trimmedRest.StartsWith('\''))
            {
                candidatePattern = $@"{anchor}{ws}'([^']*)'";
            }
            else if (trimmedRest.Length > 0)
            {
                var token = matchedSlice.EndsWith('"')
                    ? @"([^""\\]*(?:\\.[^""\\]*)*)"""
                    : @"([^\s""'<>\\,\}\];&]+)";
                candidatePattern = $"{anchor}{ws}{token}";
            }

            if (candidatePattern is not null &&
                addedPatterns.Add(candidatePattern) &&
                TryEvaluateRegex(text, candidatePattern, out var captured, out var count) &&
                !string.IsNullOrEmpty(captured))
            {
                scored.Add((new IndexedSuggestionCandidate(
                    Expression: candidatePattern,
                    AnchorKey: matchedSlice.Trim(),
                    SuggestedVariableName: defaultVarName,
                    PreviewValue: RealtimeExtractionCatalog.Truncate(captured),
                    BadgeText: $"contexte · {count} corr.",
                    DetailText: "Suggéré d'après le texte autour dans la réponse",
                    SuggestsList: count > 1,
                    MatchCount: count,
                    Depth: 0,
                    IsContainer: false,
                    BasePriority: 620,
                    TargetHeaderName: targetHeaderName), 680));
            }
        }

        // Cas B : l'utilisateur tape une portion de la valeur elle-même (ex: `SponProd`) -> ancrer à gauche
        if (matchStart > lineStart)
        {
            var contextStart = Math.Max(lineStart, matchStart - 28);
            var leftContext = text[contextStart..matchStart];
            var separatorIdx = leftContext.LastIndexOfAny([':', '=', '"', '\'', '>']);
            if (separatorIdx >= 0)
            {
                var anchorStart = Math.Max(0, separatorIdx - 18);
                var rawLeft = leftContext[anchorStart..];
                if (!string.IsNullOrWhiteSpace(rawLeft))
                {
                    var leftAnchor = ExtractionSuggester.EscapeLiteralWithWhitespace(rawLeft);
                    var boundaryChar = leftContext[separatorIdx];
                    var capture = boundaryChar switch
                    {
                        '"' => @"([^""\\]*(?:\\.[^""\\]*)*)",
                        '\'' => @"([^']*)",
                        '>' => @"([^<]+)",
                        _ => @"([^\s""'<>\\,\}\];&]+)"
                    };
                    var leftPattern = $"{leftAnchor}{capture}";

                    if (addedPatterns.Add(leftPattern) &&
                        TryEvaluateRegex(text, leftPattern, out var captured, out var count) &&
                        !string.IsNullOrEmpty(captured))
                    {
                        scored.Add((new IndexedSuggestionCandidate(
                            Expression: leftPattern,
                            AnchorKey: matchedSlice.Trim(),
                            SuggestedVariableName: defaultVarName,
                            PreviewValue: RealtimeExtractionCatalog.Truncate(captured),
                            BadgeText: $"valeur · {count} corr.",
                            DetailText: "Motif généré autour de la valeur trouvée",
                            SuggestsList: count > 1,
                            MatchCount: count,
                            Depth: 0,
                            IsContainer: false,
                            BasePriority: 590,
                            TargetHeaderName: targetHeaderName), 650));
                    }
                }
            }
        }
    }

    private static bool TryEvaluateRegex(
        string text,
        string pattern,
        out string capturedValue,
        out int matchCount)
    {
        capturedValue = string.Empty;
        matchCount = 0;

        try
        {
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, LiveRegexTimeout);
            var matches = regex.Matches(text);
            if (matches.Count == 0)
            {
                return false;
            }

            matchCount = matches.Count;
            var first = matches[0];
            if (first.Groups.Count <= 1 || !first.Groups[1].Success)
            {
                return false;
            }

            capturedValue = first.Groups[1].Value;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string StripJsonPathRootPrefix(string query)
    {
        if (query.StartsWith("$.", StringComparison.Ordinal))
        {
            return query[2..];
        }

        if (query.StartsWith('$'))
        {
            return query[1..];
        }

        return query;
    }

    private static IReadOnlyList<RealtimeExtractionSuggestion> FinalizeSuggestions(
        List<(IndexedSuggestionCandidate Candidate, int Score)> scored,
        int maxResults)
    {
        if (scored.Count == 0 || maxResults <= 0)
        {
            return [];
        }

        scored.Sort((left, right) =>
        {
            var scoreCmp = right.Score.CompareTo(left.Score);
            if (scoreCmp != 0)
            {
                return scoreCmp;
            }

            var depthCmp = left.Candidate.Depth.CompareTo(right.Candidate.Depth);
            if (depthCmp != 0)
            {
                return depthCmp;
            }

            return string.Compare(left.Candidate.Expression, right.Candidate.Expression, StringComparison.OrdinalIgnoreCase);
        });

        var results = new List<RealtimeExtractionSuggestion>(Math.Min(maxResults, scored.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (candidate, _) in scored)
        {
            var dedupKey = candidate.TargetHeaderName is not null
                ? $"{candidate.TargetHeaderName}\u0001{candidate.Expression}"
                : candidate.Expression;
            if (!seen.Add(dedupKey))
            {
                continue;
            }

            results.Add(new RealtimeExtractionSuggestion(
                candidate.Expression,
                candidate.SuggestedVariableName,
                candidate.PreviewValue,
                candidate.BadgeText,
                candidate.DetailText,
                candidate.SuggestsList,
                candidate.MatchCount,
                candidate.TargetHeaderName));

            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }
}
