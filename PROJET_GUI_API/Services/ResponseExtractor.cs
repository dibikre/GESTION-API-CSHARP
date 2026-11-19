using System;
using System.Collections.Generic;
using System.Globalization;
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

        var expression = extraction.Expression?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(expression))
        {
            return ExtractionOutcome.Failure($"Expression vide pour la variable « {extraction.VariableName} ».");
        }

        if (extraction.Source == ChainExtractionSource.Header)
        {
            return ExtractFromHeader(response, extraction, expression);
        }

        return extraction.Kind == ExtractionKind.JsonPath
            ? ExtractJsonPathFromBody(response.Body ?? string.Empty, expression)
            : ExtractRegex(response.Body ?? string.Empty, expression, extraction.RegexGroup);
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

    private static ExtractionOutcome ExtractJsonPathFromBody(string body, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ExtractionOutcome.Failure("Le corps de la réponse est vide (JSON attendu).");
        }

        if (!TryParseJsonPath(rawPath, out var segments, out var pathError))
        {
            return ExtractionOutcome.Failure(pathError!);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.TrimStart('\uFEFF'), JsonParseOptions);
        }
        catch (JsonException)
        {
            return ExtractionOutcome.Failure("Le corps de la réponse n'est pas un JSON valide.");
        }

        using (document)
        {
            var current = document.RootElement;

            foreach (var segment in segments)
            {
                if (segment.PropertyName is not null)
                {
                    if (current.ValueKind != JsonValueKind.Object)
                    {
                        return ExtractionOutcome.Failure(
                            $"Impossible d'accéder à la propriété « {segment.PropertyName} » sur un élément de type {DescribeKind(current.ValueKind)}.");
                    }

                    if (!current.TryGetProperty(segment.PropertyName, out current))
                    {
                        return ExtractionOutcome.Failure(
                            $"Propriété JSON introuvable : « {segment.PropertyName} ».");
                    }
                }

                if (segment.ArrayIndex.HasValue)
                {
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

            var value = current.ValueKind switch
            {
                JsonValueKind.String => current.GetString() ?? string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(current, JsonSerializeOptions),
                _ => current.GetRawText()
            };

            return ExtractionOutcome.Success(value);
        }
    }

    private readonly record struct PathStep(string? PropertyName, int? ArrayIndex);

    private static bool TryParseJsonPath(string rawPath, out List<PathStep> steps, out string? error)
    {
        steps = [];
        error = null;

        var path = rawPath.Trim();
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

        var parts = path.Split('.');
        foreach (var rawPart in parts)
        {
            if (string.IsNullOrEmpty(rawPart))
            {
                error = $"Chemin JSONPath invalide « {rawPath} » (segment vide).";
                return false;
            }

            var firstBracket = rawPart.IndexOf('[');
            if (firstBracket < 0)
            {
                if (rawPart.Contains(']'))
                {
                    error = $"Chemin JSONPath invalide « {rawPath} » (']' inattendu).";
                    return false;
                }

                steps.Add(new PathStep(rawPart, null));
                continue;
            }

            var propName = firstBracket > 0 ? rawPart[..firstBracket] : null;
            if (propName is not null && propName.Contains(']'))
            {
                error = $"Chemin JSONPath invalide « {rawPath} ».";
                return false;
            }

            var pos = firstBracket;
            var isFirstIndex = true;
            while (pos < rawPart.Length)
            {
                if (rawPart[pos] != '[')
                {
                    error = $"Chemin JSONPath invalide « {rawPath} » près de « {rawPart[pos..]} ».";
                    return false;
                }

                var closeIdx = rawPart.IndexOf(']', pos + 1);
                if (closeIdx < 0)
                {
                    error = $"Chemin JSONPath invalide « {rawPath} » ('[' non fermé).";
                    return false;
                }

                var indexText = rawPart[(pos + 1)..closeIdx].Trim();
                if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
                {
                    error = $"Index de tableau invalide « [{indexText}] » dans « {rawPath} ».";
                    return false;
                }

                steps.Add(new PathStep(isFirstIndex ? propName : null, parsedIndex));
                isFirstIndex = false;
                pos = closeIdx + 1;
            }
        }

        return steps.Count > 0;
    }

    private static string DescribeKind(JsonValueKind kind) => kind switch
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
