using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class ResponseExtractor : IResponseExtractor
{
    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        MaxDepth = 1024
    };
    private static readonly JsonSerializerOptions JsonSerializeOptions = new()
    {
        MaxDepth = 1024
    };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex VariableNameRegex = new(
        @"^[\p{L}_][\p{L}\p{N}_]*$",
        RegexOptions.Compiled);

    public bool TryValidateVariableName(string? variableName, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            errorMessage = "Le nom de la variable extraite ne peut pas être vide.";
            return false;
        }

        var trimmed = variableName.Trim();
        if (trimmed.StartsWith('$'))
        {
            errorMessage = $"Le nom « {trimmed} » ne peut pas commencer par $ (réservé aux variables prédéfinies).";
            return false;
        }

        if (!VariableNameRegex.IsMatch(trimmed))
        {
            errorMessage = $"Nom de variable invalide « {trimmed} » (lettres, chiffres et _ uniquement).";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public ExtractionOutcome Extract(ResponseModel response, ChainExtraction extraction)
    {
        if (response is null)
        {
            return ExtractionOutcome.Failure("Aucune réponse disponible pour l'extraction.");
        }

        if (extraction is null)
        {
            return ExtractionOutcome.Failure("Règle d'extraction absente.");
        }

        if (!TryValidateVariableName(extraction.VariableName, out var nameError))
        {
            return ExtractionOutcome.Failure(nameError!);
        }

        var isFullResponse = extraction.Kind == ExtractionKind.FullResponse;
        var expression = extraction.Expression?.Trim() ?? string.Empty;
        if (!isFullResponse && string.IsNullOrEmpty(expression))
        {
            return ExtractionOutcome.Failure($"Expression vide pour la variable « {extraction.VariableName} ».");
        }

        if (extraction.Source == ChainExtractionSource.Header && isFullResponse)
        {
            return ExtractionOutcome.Failure(
                "Le mode Réponse complète ne s'applique qu'au corps ; utilise Regex pour les en-têtes.");
        }

        var outcome = extraction.Source == ChainExtractionSource.Header
            ? ExtractFromHeader(response, extraction, expression)
            : isFullResponse
                ? ExtractionOutcome.Success(response.Body ?? string.Empty)
                : extraction.Kind == ExtractionKind.JsonPath
                    ? ExtractJsonPathFromBody(response.Body ?? string.Empty, expression)
                    : ExtractRegex(response.Body ?? string.Empty, expression, extraction.RegexGroup);

        if (!outcome.IsSuccess || extraction.Conditions.Count == 0)
        {
            return outcome;
        }

        return ExtractionConditionEngine.ApplyToScalar(
            outcome.Value,
            extraction.Conditions,
            extraction.ConditionMatchMode,
            extraction.DeduplicateList);
    }

    public ListExtractionOutcome ExtractRawList(ResponseModel response, ChainExtraction extraction)
    {
        if (response is null)
        {
            return ListExtractionOutcome.Failure("Aucune réponse disponible pour l'extraction.");
        }

        if (extraction is null)
        {
            return ListExtractionOutcome.Failure("Règle d'extraction absente.");
        }

        if (!TryValidateVariableName(extraction.VariableName, out var nameError))
        {
            return ListExtractionOutcome.Failure(nameError!);
        }

        if (extraction.Kind == ExtractionKind.FullResponse)
        {
            return ListExtractionOutcome.Failure(
                "Le mode Réponse complète capture tout le corps dans une variable simple {{...}} ; décoche « En liste [[...]] » ou utilise JSONPath.");
        }

        var expression = extraction.Expression?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(expression))
        {
            return ListExtractionOutcome.Failure($"Expression vide pour la liste « {extraction.VariableName} ».");
        }

        if (extraction.Source == ChainExtractionSource.Header)
        {
            return ExtractListFromHeader(response, extraction, expression);
        }

        return extraction.Kind == ExtractionKind.JsonPath
            ? ExtractListJsonPathFromBody(response.Body ?? string.Empty, expression)
            : ExtractRegexList(response.Body ?? string.Empty, expression, extraction.RegexGroup);
    }

    public ListExtractionOutcome ExtractList(ResponseModel response, ChainExtraction extraction)
    {
        var rawOutcome = ExtractRawList(response, extraction);
        if (!rawOutcome.IsSuccess || (!extraction.HasConditions && extraction.Conditions.Count == 0))
        {
            return rawOutcome;
        }

        var filtered = ExtractionConditionEngine.ApplyToList(
            rawOutcome.Values,
            extraction.Conditions,
            extraction.ConditionMatchMode,
            extraction.DeduplicateList,
            failWhenEmpty: true);

        return filtered.IsSuccess
            ? ListExtractionOutcome.Success(filtered.Values)
            : ListExtractionOutcome.Failure(filtered.ErrorMessage ?? "Aucun élément extrait après application des conditions.");
    }

    public ExtractionOutcome ExtractFromVariable(IReadOnlyList<string> sourceValues, ChainExtraction extraction)
    {
        if (extraction is null)
        {
            return ExtractionOutcome.Failure("Règle d'extraction absente.");
        }

        if (!TryValidateVariableName(extraction.VariableName, out var nameError))
        {
            return ExtractionOutcome.Failure(nameError!);
        }

        var sourceVarName = extraction.SourceVariableName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(sourceVarName))
        {
            return ExtractionOutcome.Failure($"Nom de variable source vide pour « {extraction.VariableName} ».");
        }

        if (sourceValues is null || sourceValues.Count == 0)
        {
            return ExtractionOutcome.Failure($"La variable source « {sourceVarName} » est vide ou introuvable.");
        }

        var isFullResponse = extraction.Kind == ExtractionKind.FullResponse;
        var expression = extraction.Expression?.Trim() ?? string.Empty;
        if (!isFullResponse && string.IsNullOrEmpty(expression))
        {
            return ExtractionOutcome.Failure($"Expression vide pour la variable « {extraction.VariableName} ».");
        }

        ExtractionOutcome outcome;
        if (isFullResponse)
        {
            var rawVal = sourceValues.Count == 1
                ? sourceValues[0] ?? string.Empty
                : JsonSerializer.Serialize(sourceValues, JsonSerializeOptions);
            outcome = ExtractionOutcome.Success(rawVal);
        }
        else if (extraction.Kind == ExtractionKind.JsonPath)
        {
            outcome = ExtractJsonPathFromValues(sourceValues, expression);
        }
        else
        {
            ExtractionOutcome? firstMatch = null;
            ExtractionOutcome? lastFailure = null;
            foreach (var item in sourceValues)
            {
                var attempt = ExtractRegex(item ?? string.Empty, expression, extraction.RegexGroup);
                if (attempt.IsSuccess)
                {
                    firstMatch = attempt;
                    break;
                }

                lastFailure = attempt;
            }

            outcome = firstMatch ?? lastFailure ?? ExtractionOutcome.Failure("Aucune correspondance trouvée pour l'expression régulière.");
        }

        if (!outcome.IsSuccess || extraction.Conditions.Count == 0)
        {
            return outcome;
        }

        return ExtractionConditionEngine.ApplyToScalar(
            outcome.Value,
            extraction.Conditions,
            extraction.ConditionMatchMode,
            extraction.DeduplicateList);
    }

    public ListExtractionOutcome ExtractRawListFromVariable(IReadOnlyList<string> sourceValues, ChainExtraction extraction)
    {
        if (extraction is null)
        {
            return ListExtractionOutcome.Failure("Règle d'extraction absente.");
        }

        if (!TryValidateVariableName(extraction.VariableName, out var nameError))
        {
            return ListExtractionOutcome.Failure(nameError!);
        }

        var sourceVarName = extraction.SourceVariableName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(sourceVarName))
        {
            return ListExtractionOutcome.Failure($"Nom de variable source vide pour « {extraction.VariableName} ».");
        }

        if (extraction.Kind == ExtractionKind.FullResponse)
        {
            return ListExtractionOutcome.Failure(
                "Le mode Réponse complète capture la valeur dans une variable simple {{...}} ; décoche « En liste [[...]] » ou utilise JSONPath.");
        }

        var expression = extraction.Expression?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(expression))
        {
            return ListExtractionOutcome.Failure($"Expression vide pour la liste « {extraction.VariableName} ».");
        }

        if (sourceValues is null || sourceValues.Count == 0)
        {
            return ListExtractionOutcome.Failure($"La variable source « {sourceVarName} » est vide ou introuvable.");
        }

        if (extraction.Kind == ExtractionKind.JsonPath)
        {
            return ExtractListJsonPathFromValues(sourceValues, expression);
        }

        var collected = new List<string>();
        string? lastError = null;
        foreach (var item in sourceValues)
        {
            var attempt = ExtractRegexList(item ?? string.Empty, expression, extraction.RegexGroup);
            if (attempt.IsSuccess)
            {
                collected.AddRange(attempt.Values);
            }
            else
            {
                lastError = attempt.ErrorMessage;
            }
        }

        return collected.Count > 0
            ? ListExtractionOutcome.Success(collected)
            : ListExtractionOutcome.Failure(lastError ?? "Aucune correspondance trouvée pour l'expression régulière.");
    }

    public ListExtractionOutcome ExtractListFromVariable(IReadOnlyList<string> sourceValues, ChainExtraction extraction)
    {
        var rawOutcome = ExtractRawListFromVariable(sourceValues, extraction);
        if (!rawOutcome.IsSuccess || (!extraction.HasConditions && extraction.Conditions.Count == 0))
        {
            return rawOutcome;
        }

        var filtered = ExtractionConditionEngine.ApplyToList(
            rawOutcome.Values,
            extraction.Conditions,
            extraction.ConditionMatchMode,
            extraction.DeduplicateList,
            failWhenEmpty: true);

        return filtered.IsSuccess
            ? ListExtractionOutcome.Success(filtered.Values)
            : ListExtractionOutcome.Failure(filtered.ErrorMessage ?? "Aucun élément extrait après application des conditions.");
    }

    private static ExtractionOutcome ExtractFromHeader(
        ResponseModel response,
        ChainExtraction extraction,
        string expression)
    {
        if (extraction.Kind == ExtractionKind.JsonPath)
        {
            return ExtractionOutcome.Failure(
                "Le mode JSONPath ne s'applique pas aux en-têtes ; utilise Regex.");
        }

        var headerName = extraction.HeaderName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(headerName))
        {
            return ExtractionOutcome.Failure("Nom d'en-tête vide.");
        }

        if (response.Headers is null || response.Headers.Count == 0)
        {
            return ExtractionOutcome.Failure($"En-tête « {headerName} » absent de la réponse.");
        }

        var matchingValues = new List<string>();
        foreach (var header in response.Headers)
        {
            if (header.IsEnabled &&
                !string.IsNullOrWhiteSpace(header.Key) &&
                header.Key.Trim().Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                matchingValues.Add(header.Value ?? string.Empty);
            }
        }

        if (matchingValues.Count == 0)
        {
            return ExtractionOutcome.Failure($"En-tête « {headerName} » absent de la réponse.");
        }

        ExtractionOutcome? lastFailure = null;
        foreach (var headerValue in matchingValues)
        {
            var outcome = ExtractRegex(headerValue, expression, extraction.RegexGroup);
            if (outcome.IsSuccess)
            {
                return outcome;
            }

            lastFailure = outcome;
        }

        return lastFailure ?? ExtractionOutcome.Failure($"Aucune correspondance dans l'en-tête « {headerName} ».");
    }

    private static ListExtractionOutcome ExtractListFromHeader(
        ResponseModel response,
        ChainExtraction extraction,
        string expression)
    {
        if (extraction.Kind == ExtractionKind.JsonPath)
        {
            return ListExtractionOutcome.Failure(
                "Le mode JSONPath ne s'applique pas aux en-têtes ; utilise Regex.");
        }

        var headerName = extraction.HeaderName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(headerName))
        {
            return ListExtractionOutcome.Failure("Nom d'en-tête vide.");
        }

        if (response.Headers is null || response.Headers.Count == 0)
        {
            return ListExtractionOutcome.Failure($"En-tête « {headerName} » absent de la réponse.");
        }

        var matchingValues = new List<string>();
        foreach (var header in response.Headers)
        {
            if (header.IsEnabled &&
                !string.IsNullOrWhiteSpace(header.Key) &&
                header.Key.Trim().Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                matchingValues.Add(header.Value ?? string.Empty);
            }
        }

        if (matchingValues.Count == 0)
        {
            return ListExtractionOutcome.Failure($"En-tête « {headerName} » absent de la réponse.");
        }

        var collected = new List<string>();
        string? lastError = null;
        foreach (var headerValue in matchingValues)
        {
            var outcome = ExtractRegexList(headerValue, expression, extraction.RegexGroup);
            if (outcome.IsSuccess)
            {
                collected.AddRange(outcome.Values);
            }
            else
            {
                lastError = outcome.ErrorMessage;
            }
        }

        if (collected.Count > 0)
        {
            return ListExtractionOutcome.Success(collected);
        }

        return ListExtractionOutcome.Failure(lastError ?? $"Aucune correspondance dans l'en-tête « {headerName} ».");
    }

    private static ExtractionOutcome ExtractRegex(string input, string pattern, int groupIndex)
    {
        if (groupIndex < 0)
        {
            return ExtractionOutcome.Failure("L'index du groupe Regex doit être positif ou nul.");
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return ExtractionOutcome.Failure($"Expression régulière invalide : {ex.Message}");
        }

        Match match;
        try
        {
            match = regex.Match(input ?? string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            return ExtractionOutcome.Failure("L'évaluation de l'expression régulière a expiré (> 2 s).");
        }

        if (!match.Success)
        {
            return ExtractionOutcome.Failure("Aucune correspondance trouvée pour l'expression régulière.");
        }

        if (groupIndex >= match.Groups.Count)
        {
            return ExtractionOutcome.Failure(
                $"Groupe Regex #{groupIndex} inexistant (groupes disponibles : 0 à {match.Groups.Count - 1}).");
        }

        var group = match.Groups[groupIndex];
        if (!group.Success)
        {
            return ExtractionOutcome.Failure($"Le groupe Regex #{groupIndex} n'a capturé aucune valeur.");
        }

        return ExtractionOutcome.Success(group.Value);
    }

    private static ListExtractionOutcome ExtractRegexList(string input, string pattern, int groupIndex)
    {
        if (groupIndex < 0)
        {
            return ListExtractionOutcome.Failure("L'index du groupe Regex doit être positif ou nul.");
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return ListExtractionOutcome.Failure($"Expression régulière invalide : {ex.Message}");
        }

        var values = new List<string>();
        try
        {
            var matches = regex.Matches(input ?? string.Empty);
            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                if (groupIndex >= match.Groups.Count)
                {
                    return ListExtractionOutcome.Failure(
                        $"Groupe Regex #{groupIndex} inexistant (groupes disponibles : 0 à {match.Groups.Count - 1}).");
                }

                var group = match.Groups[groupIndex];
                if (group.Success)
                {
                    values.Add(group.Value);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return ListExtractionOutcome.Failure("L'évaluation de l'expression régulière a expiré (> 2 s).");
        }

        if (values.Count == 0)
        {
            return ListExtractionOutcome.Failure("Aucune correspondance trouvée pour l'expression régulière.");
        }

        return ListExtractionOutcome.Success(values);
    }

    internal static ExtractionOutcome ExtractJsonPathFromValues(IReadOnlyList<string> sourceValues, string rawPath)
    {
        if (sourceValues is null || sourceValues.Count == 0)
        {
            return ExtractionOutcome.Failure("La liste source est vide.");
        }

        if (!TryParseJsonPath(rawPath, out var segments, out var pathError))
        {
            return ExtractionOutcome.Failure(pathError!);
        }

        foreach (var s in segments)
        {
            if (s.IsWildcard)
            {
                return ExtractionOutcome.Failure("Le sélecteur [*] ou [] extrait plusieurs valeurs : coche « En liste [[...]] ».");
            }
        }

        if (segments.Count == 0)
        {
            return ExtractionOutcome.Success(
                sourceValues.Count == 1
                    ? sourceValues[0] ?? string.Empty
                    : JsonSerializer.Serialize(sourceValues, JsonSerializeOptions));
        }

        if (segments[0].PropertyName is null &&
            segments[0].ArrayIndex is int firstIndex &&
            (sourceValues.Count > 1 || !LooksLikeJsonArray(sourceValues[0])))
        {
            var resolved = firstIndex < 0 ? sourceValues.Count + firstIndex : firstIndex;
            if (resolved < 0 || resolved >= sourceValues.Count)
            {
                return ExtractionOutcome.Failure(
                    $"Index [{firstIndex}] hors limites (taille de la liste : {sourceValues.Count}).");
            }

            var selectedItem = sourceValues[resolved] ?? string.Empty;
            if (segments.Count == 1)
            {
                return ExtractionOutcome.Success(selectedItem);
            }

            return ExtractJsonPathFromBody(selectedItem, segments, startSegmentIndex: 1);
        }

        ExtractionOutcome? lastFailure = null;
        foreach (var item in sourceValues)
        {
            var attempt = ExtractJsonPathFromBody(item ?? string.Empty, segments, startSegmentIndex: 0);
            if (attempt.IsSuccess)
            {
                return attempt;
            }

            lastFailure = attempt;
        }

        return lastFailure ?? ExtractionOutcome.Failure("Aucune correspondance JSONPath dans la variable source.");
    }

    internal static ListExtractionOutcome ExtractListJsonPathFromValues(IReadOnlyList<string> sourceValues, string rawPath)
    {
        if (sourceValues is null || sourceValues.Count == 0)
        {
            return ListExtractionOutcome.Failure("La liste source est vide.");
        }

        if (!TryParseJsonPath(rawPath, out var segments, out var pathError))
        {
            return ListExtractionOutcome.Failure(pathError!);
        }

        if (segments.Count == 0)
        {
            return ListExtractionOutcome.Success(sourceValues);
        }

        if (segments[0].PropertyName is null &&
            (segments[0].IsWildcard || segments[0].ArrayIndex.HasValue) &&
            (sourceValues.Count > 1 || !LooksLikeJsonArray(sourceValues[0])))
        {
            if (segments[0].IsWildcard)
            {
                if (segments.Count == 1)
                {
                    return ListExtractionOutcome.Success(sourceValues);
                }

                var collected = new List<string>();
                string? lastErr = null;
                foreach (var item in sourceValues)
                {
                    var attempt = ExtractListJsonPathFromBody(item ?? string.Empty, segments, startSegmentIndex: 1);
                    if (attempt.IsSuccess)
                    {
                        collected.AddRange(attempt.Values);
                    }
                    else
                    {
                        lastErr = attempt.ErrorMessage;
                    }
                }

                return collected.Count > 0
                    ? ListExtractionOutcome.Success(collected)
                    : ListExtractionOutcome.Failure(lastErr ?? "Aucune valeur extraite pour l'expression JSONPath.");
            }

            var firstIndex = segments[0].ArrayIndex!.Value;
            var resolved = firstIndex < 0 ? sourceValues.Count + firstIndex : firstIndex;
            if (resolved < 0 || resolved >= sourceValues.Count)
            {
                return ListExtractionOutcome.Failure(
                    $"Index [{firstIndex}] hors limites (taille de la liste : {sourceValues.Count}).");
            }

            var chosen = sourceValues[resolved] ?? string.Empty;
            if (segments.Count == 1)
            {
                return ListExtractionOutcome.Success([chosen]);
            }

            return ExtractListJsonPathFromBody(chosen, segments, startSegmentIndex: 1);
        }

        var aggregated = new List<string>();
        string? lastFailure = null;
        foreach (var item in sourceValues)
        {
            var attempt = ExtractListJsonPathFromBody(item ?? string.Empty, segments, startSegmentIndex: 0);
            if (attempt.IsSuccess)
            {
                aggregated.AddRange(attempt.Values);
            }
            else
            {
                lastFailure = attempt.ErrorMessage;
            }
        }

        return aggregated.Count > 0
            ? ListExtractionOutcome.Success(aggregated)
            : ListExtractionOutcome.Failure(lastFailure ?? "Aucune valeur extraite pour l'expression JSONPath.");
    }

    internal static ExtractionOutcome ExtractJsonPathFromBody(string body, string rawPath)
    {
        if (!TryParseJsonPath(rawPath, out var segments, out var pathError))
        {
            return ExtractionOutcome.Failure(pathError!);
        }

        foreach (var s in segments)
        {
            if (s.IsWildcard)
            {
                return ExtractionOutcome.Failure("Le sélecteur [*], [] ou [n++] extrait plusieurs valeurs : coche « En liste [[...]] ».");
            }
        }

        return ExtractJsonPathFromBody(body, segments, startSegmentIndex: 0);
    }

    private static ExtractionOutcome ExtractJsonPathFromBody(
        string body,
        IReadOnlyList<PathStep> segments,
        int startSegmentIndex)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ExtractionOutcome.Failure("Le corps de la réponse est vide (JSON attendu).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.TrimStart('\uFEFF'), JsonParseOptions);
        }
        catch (JsonException)
        {
            return ExtractionOutcome.Failure("Le contenu ciblé n'est pas un JSON valide.");
        }

        var ownedDocuments = new List<JsonDocument>();
        try
        {
            var current = document.RootElement;

            for (var i = startSegmentIndex; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment.PropertyName is not null)
                {
                    if (current.ValueKind == JsonValueKind.String)
                    {
                        TryUnwrapJsonString(ref current, ownedDocuments);
                    }

                    if (current.ValueKind != JsonValueKind.Object)
                    {
                        return ExtractionOutcome.Failure(
                            $"Impossible d'accéder à la propriété « {segment.PropertyName} » sur un élément de type {DescribeKind(current.ValueKind)}.");
                    }

                    if (current.TryGetProperty(segment.PropertyName, out var exactProp))
                    {
                        current = exactProp;
                    }
                    else if (TryResolveDottedPropertyFallback(current, segments, ref i, out var dottedProp))
                    {
                        current = dottedProp;
                    }
                    else
                    {
                        return ExtractionOutcome.Failure(
                            $"Propriété JSON introuvable : « {segment.PropertyName} ».");
                    }
                }

                if (segment.ArrayIndex.HasValue)
                {
                    if (current.ValueKind == JsonValueKind.String)
                    {
                        TryUnwrapJsonString(ref current, ownedDocuments);
                    }

                    if (current.ValueKind != JsonValueKind.Array)
                    {
                        return ExtractionOutcome.Failure(
                            $"Index [{segment.ArrayIndex.Value}] inapplicable sur un élément de type {DescribeKind(current.ValueKind)}.");
                    }

                    var length = current.GetArrayLength();
                    var rawIndex = segment.ArrayIndex.Value;
                    var resolvedIndex = rawIndex < 0 ? length + rawIndex : rawIndex;

                    if (resolvedIndex < 0 || resolvedIndex >= length)
                    {
                        return ExtractionOutcome.Failure(
                            $"Index [{rawIndex}] hors limites (taille du tableau : {length}).");
                    }

                    current = current[resolvedIndex];
                }
            }

            return ExtractionOutcome.Success(FormatJsonElement(current));
        }
        finally
        {
            foreach (var extra in ownedDocuments)
            {
                extra.Dispose();
            }

            document.Dispose();
        }
    }

    internal static ListExtractionOutcome ExtractListJsonPathFromBody(string body, string rawPath)
    {
        if (!TryParseJsonPath(rawPath, out var segments, out var pathError))
        {
            return ListExtractionOutcome.Failure(pathError!);
        }

        return ExtractListJsonPathFromBody(body, segments, startSegmentIndex: 0);
    }

    private static ListExtractionOutcome ExtractListJsonPathFromBody(
        string body,
        IReadOnlyList<PathStep> segments,
        int startSegmentIndex)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ListExtractionOutcome.Failure("Le corps de la réponse est vide (JSON attendu).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.TrimStart('\uFEFF'), JsonParseOptions);
        }
        catch (JsonException)
        {
            return ListExtractionOutcome.Failure("Le contenu ciblé n'est pas un JSON valide.");
        }

        var ownedDocuments = new List<JsonDocument>();
        try
        {
            var currentElements = new List<JsonElement> { document.RootElement };
            var hasWildcard = false;

            for (var i = startSegmentIndex; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment.PropertyName is not null)
                {
                    if (!TryAdvancePropertyElements(
                        currentElements,
                        segments,
                        ref i,
                        hasWildcard,
                        ownedDocuments,
                        out var nextElements,
                        out var propError))
                    {
                        return ListExtractionOutcome.Failure(propError!);
                    }

                    currentElements = nextElements;
                    segment = segments[i];
                }

                if (segment.IsWildcard)
                {
                    var wasWildcard = hasWildcard;
                    hasWildcard = true;
                    var nextElements = new List<JsonElement>();

                    foreach (var rawEl in currentElements)
                    {
                        var el = rawEl;
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            TryUnwrapJsonString(ref el, ownedDocuments);
                        }

                        if (el.ValueKind != JsonValueKind.Array)
                        {
                            if (!wasWildcard)
                            {
                                return ListExtractionOutcome.Failure(
                                    $"Le sélecteur [*] est inapplicable sur un élément de type {DescribeKind(el.ValueKind)}.");
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
                        return ListExtractionOutcome.Failure("Le tableau JSON ciblé est vide.");
                    }

                    currentElements = nextElements;
                }
                else if (segment.ArrayIndex.HasValue)
                {
                    var rawIndex = segment.ArrayIndex.Value;
                    var nextElements = new List<JsonElement>(currentElements.Count);

                    foreach (var rawEl in currentElements)
                    {
                        var el = rawEl;
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            TryUnwrapJsonString(ref el, ownedDocuments);
                        }

                        if (el.ValueKind != JsonValueKind.Array)
                        {
                            if (!hasWildcard)
                            {
                                return ListExtractionOutcome.Failure(
                                    $"Index [{rawIndex}] inapplicable sur un élément de type {DescribeKind(el.ValueKind)}.");
                            }

                            continue;
                        }

                        var length = el.GetArrayLength();
                        var resolvedIndex = rawIndex < 0 ? length + rawIndex : rawIndex;
                        if (resolvedIndex < 0 || resolvedIndex >= length)
                        {
                            if (!hasWildcard)
                            {
                                return ListExtractionOutcome.Failure(
                                    $"Index [{rawIndex}] hors limites (taille du tableau : {length}).");
                            }

                            continue;
                        }

                        nextElements.Add(el[resolvedIndex]);
                    }

                    if (nextElements.Count == 0)
                    {
                        return ListExtractionOutcome.Failure($"Aucune valeur trouvée pour l'index [{rawIndex}].");
                    }

                    currentElements = nextElements;
                }
            }

            if (currentElements.Count == 0)
            {
                return ListExtractionOutcome.Failure("Aucune valeur extraite pour l'expression JSONPath.");
            }

            if (!hasWildcard && currentElements.Count == 1 && currentElements[0].ValueKind == JsonValueKind.Array)
            {
                var arrayItems = new List<string>();
                foreach (var item in currentElements[0].EnumerateArray())
                {
                    arrayItems.Add(FormatJsonElement(item));
                }

                if (arrayItems.Count == 0)
                {
                    return ListExtractionOutcome.Failure("Le tableau JSON ciblé est vide.");
                }

                return ListExtractionOutcome.Success(arrayItems);
            }

            var results = new List<string>(currentElements.Count);
            foreach (var el in currentElements)
            {
                results.Add(FormatJsonElement(el));
            }

            return ListExtractionOutcome.Success(results);
        }
        finally
        {
            foreach (var extra in ownedDocuments)
            {
                extra.Dispose();
            }

            document.Dispose();
        }
    }

    private static bool TryAdvancePropertyElements(
        List<JsonElement> currentElements,
        IReadOnlyList<PathStep> segments,
        ref int stepIndex,
        bool hasWildcard,
        List<JsonDocument> ownedDocuments,
        out List<JsonElement> nextElements,
        out string? error)
    {
        var propName = segments[stepIndex].PropertyName!;
        nextElements = new List<JsonElement>(currentElements.Count);
        var exactMatches = new List<JsonElement>(currentElements.Count);
        string? firstExactError = null;
        var allFoundExact = true;

        foreach (var rawEl in currentElements)
        {
            var el = rawEl;
            if (el.ValueKind == JsonValueKind.String)
            {
                TryUnwrapJsonString(ref el, ownedDocuments);
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                allFoundExact = false;
                firstExactError ??= $"Impossible d'accéder à la propriété « {propName} » sur un élément de type {DescribeKind(el.ValueKind)}.";
                continue;
            }

            if (!el.TryGetProperty(propName, out var propVal))
            {
                allFoundExact = false;
                firstExactError ??= $"Propriété JSON introuvable : « {propName} ».";
                continue;
            }

            exactMatches.Add(propVal);
        }

        if (exactMatches.Count > 0 && (allFoundExact || hasWildcard))
        {
            nextElements = exactMatches;
            error = null;
            return true;
        }

        var maxEnd = GetMaxContiguousPropertyIndex(segments, stepIndex);
        for (var endIdx = maxEnd; endIdx > stepIndex; endIdx--)
        {
            var joinedKey = BuildJoinedPropertyName(segments, stepIndex, endIdx);
            var joinedMatches = new List<JsonElement>(currentElements.Count);
            var allFoundJoined = true;

            foreach (var rawEl in currentElements)
            {
                var el = rawEl;
                if (el.ValueKind == JsonValueKind.String)
                {
                    TryUnwrapJsonString(ref el, ownedDocuments);
                }

                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(joinedKey, out var propVal))
                {
                    allFoundJoined = false;
                    continue;
                }

                joinedMatches.Add(propVal);
            }

            if (joinedMatches.Count > 0 && (allFoundJoined || hasWildcard))
            {
                stepIndex = endIdx;
                nextElements = joinedMatches;
                error = null;
                return true;
            }
        }

        if (exactMatches.Count > 0)
        {
            nextElements = exactMatches;
            error = null;
            return true;
        }

        error = currentElements.Count == 0
            ? "Aucune valeur extraite pour l'expression JSONPath."
            : firstExactError ?? $"Propriété JSON introuvable : « {propName} ».";
        return false;
    }

    private static bool TryResolveDottedPropertyFallback(
        JsonElement currentObj,
        IReadOnlyList<PathStep> segments,
        ref int stepIndex,
        out JsonElement matched)
    {
        var maxEnd = GetMaxContiguousPropertyIndex(segments, stepIndex);
        for (var endIdx = maxEnd; endIdx > stepIndex; endIdx--)
        {
            var joinedKey = BuildJoinedPropertyName(segments, stepIndex, endIdx);
            if (currentObj.TryGetProperty(joinedKey, out matched))
            {
                stepIndex = endIdx;
                return true;
            }
        }

        matched = default;
        return false;
    }

    private static int GetMaxContiguousPropertyIndex(IReadOnlyList<PathStep> segments, int startIndex)
    {
        var end = startIndex;
        while (end + 1 < segments.Count)
        {
            var next = segments[end + 1];
            if (next.PropertyName is null || next.ArrayIndex.HasValue || next.IsWildcard)
            {
                break;
            }

            var curr = segments[end];
            if (curr.ArrayIndex.HasValue || curr.IsWildcard)
            {
                break;
            }

            end++;
        }

        return end;
    }

    private static string BuildJoinedPropertyName(IReadOnlyList<PathStep> segments, int startIndex, int endIndex)
    {
        var sb = new StringBuilder();
        for (var i = startIndex; i <= endIndex; i++)
        {
            if (i > startIndex)
            {
                sb.Append('.');
            }

            sb.Append(segments[i].PropertyName);
        }

        return sb.ToString();
    }

    private static bool TryUnwrapJsonString(ref JsonElement element, List<JsonDocument> ownedDocuments)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return false;
        }

        try
        {
            var doc = JsonDocument.Parse(trimmed, JsonParseOptions);
            ownedDocuments.Add(doc);
            element = doc.RootElement;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeJsonArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('[');
    }

    private static string FormatJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(element, JsonSerializeOptions),
        _ => element.GetRawText()
    };

    internal readonly record struct PathStep(string? PropertyName, int? ArrayIndex, bool IsWildcard = false);

    internal static bool JsonPathHasWildcard(string rawPath)
    {
        if (!TryParseJsonPath(rawPath, out var steps, out _))
        {
            return false;
        }

        foreach (var step in steps)
        {
            if (step.IsWildcard)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryParseJsonPath(string rawPath, out List<PathStep> steps, out string? error)
    {
        steps = [];
        error = null;

        var path = rawPath.Trim();
        if (path == "$")
        {
            return true;
        }

        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        else if (path.StartsWith('$'))
        {
            path = path[1..];
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Chemin JSONPath vide.";
            return false;
        }

        var pos = 0;
        var len = path.Length;
        while (pos < len)
        {
            if (path[pos] == '.')
            {
                if (pos == 0 || pos + 1 >= len || path[pos + 1] == '.')
                {
                    error = $"Chemin JSONPath invalide « {rawPath} » (segment vide).";
                    return false;
                }

                pos++;
                continue;
            }

            if (path[pos] == '[')
            {
                while (pos < len && path[pos] == '[')
                {
                    var cursor = pos + 1;
                    while (cursor < len && char.IsWhiteSpace(path[cursor]))
                    {
                        cursor++;
                    }

                    if (cursor < len && (path[cursor] == '"' || path[cursor] == '\''))
                    {
                        var quoteChar = path[cursor];
                        cursor++;
                        var keySb = new StringBuilder();
                        var closedQuote = false;
                        while (cursor < len)
                        {
                            var ch = path[cursor++];
                            if (ch == '\\' && cursor < len)
                            {
                                var esc = path[cursor++];
                                keySb.Append(esc switch
                                {
                                    '\\' => '\\',
                                    '"' => '"',
                                    '\'' => '\'',
                                    'n' => '\n',
                                    'r' => '\r',
                                    't' => '\t',
                                    _ => esc
                                });
                                continue;
                            }

                            if (ch == quoteChar)
                            {
                                closedQuote = true;
                                break;
                            }

                            keySb.Append(ch);
                        }

                        if (!closedQuote)
                        {
                            error = $"Chemin JSONPath invalide « {rawPath} » (guillemet non fermé dans [...]).";
                            return false;
                        }

                        while (cursor < len && char.IsWhiteSpace(path[cursor]))
                        {
                            cursor++;
                        }

                        if (cursor >= len || path[cursor] != ']')
                        {
                            error = $"Chemin JSONPath invalide « {rawPath} » ('[' non fermé).";
                            return false;
                        }

                        steps.Add(new PathStep(keySb.ToString(), null));
                        pos = cursor + 1;
                        continue;
                    }

                    var closeIdx = path.IndexOf(']', pos + 1);
                    if (closeIdx < 0)
                    {
                        error = $"Chemin JSONPath invalide « {rawPath} » ('[' non fermé).";
                        return false;
                    }

                    var indexText = path[(pos + 1)..closeIdx].Trim();
                    if (IsWildcardIndexToken(indexText))
                    {
                        steps.Add(new PathStep(null, null, IsWildcard: true));
                    }
                    else if (int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
                    {
                        steps.Add(new PathStep(null, parsedIndex));
                    }
                    else
                    {
                        error = $"Index de tableau invalide « [{indexText}] » dans « {rawPath} ».";
                        return false;
                    }

                    pos = closeIdx + 1;
                }

                if (pos < len && path[pos] != '.')
                {
                    error = $"Chemin JSONPath invalide « {rawPath} » près de « {path[pos..]} ».";
                    return false;
                }

                continue;
            }

            var segStart = pos;
            while (pos < len && path[pos] != '.' && path[pos] != '[' && path[pos] != ']')
            {
                pos++;
            }

            if (pos < len && path[pos] == ']')
            {
                error = $"Chemin JSONPath invalide « {rawPath} » (']' inattendu).";
                return false;
            }

            var propName = path[segStart..pos];
            if (string.IsNullOrEmpty(propName))
            {
                error = $"Chemin JSONPath invalide « {rawPath} » (segment vide).";
                return false;
            }

            steps.Add(new PathStep(propName, null));
        }

        return true;
    }

    private static bool IsWildcardIndexToken(string indexText)
    {
        if (indexText == "*" || indexText.Length == 0 || indexText == "++")
        {
            return true;
        }

        if (indexText.EndsWith("++", StringComparison.Ordinal) && indexText.Length > 2)
        {
            return IsSimpleIdentifier(indexText[..^2]);
        }

        if (indexText.StartsWith("++", StringComparison.Ordinal) && indexText.Length > 2)
        {
            return IsSimpleIdentifier(indexText[2..]);
        }

        return false;
    }

    private static bool IsSimpleIdentifier(string text)
    {
        if (text.Length == 0 || char.IsDigit(text[0]))
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    internal static string DescribeKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "objet",
        JsonValueKind.Array => "tableau",
        JsonValueKind.String => "chaîne",
        JsonValueKind.Number => "nombre",
        JsonValueKind.True or JsonValueKind.False => "booléen",
        JsonValueKind.Null => "null",
        _ => kind.ToString()
    };
}
