using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class StepReturnEvaluator : IStepReturnEvaluator
{
    private static readonly Regex TemplateMarkerRegex = new(
        @"(?:\{\{\s*(\$?[\p{L}_][\p{L}\p{N}_]*)\s*\}\}|\[\[\s*([\p{L}_][\p{L}\p{N}_]*)\s*\]\])",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public StepReturnParseResult Parse(string? returnExpression) =>
        StepReturnParser.Parse(returnExpression);

    public StepReturnEvaluationResult Evaluate(
        string? returnExpression,
        IReadOnlyDictionary<string, string> scalarVariables,
        IReadOnlyDictionary<string, string>? activeListItemVariables,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? accumulatedLists,
        int globalIndex,
        string? requestGuid = null,
        long? requestTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(scalarVariables);

        var parsed = StepReturnParser.Parse(returnExpression);
        if (!parsed.IsSuccess || parsed.Root is null)
        {
            return new StepReturnEvaluationResult(false, string.Empty, false, parsed.ErrorMessage);
        }

        var guidText = string.IsNullOrEmpty(requestGuid) ? Guid.NewGuid().ToString("D") : requestGuid;
        var timestampText = (requestTimestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        var indexText = globalIndex.ToString(CultureInfo.InvariantCulture);

        if (!TryEvaluateNode(
            parsed.Root,
            scalarVariables,
            activeListItemVariables,
            accumulatedLists,
            indexText,
            guidText,
            timestampText,
            out var jsonNode,
            out var evalError))
        {
            return new StepReturnEvaluationResult(false, string.Empty, false, evalError);
        }

        return FormatOutput(jsonNode);
    }

    private static bool TryEvaluateNode(
        StepReturnNode node,
        IReadOnlyDictionary<string, string> scalars,
        IReadOnlyDictionary<string, string>? activeListItems,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? accumulatedLists,
        string indexText,
        string guidText,
        string timestampText,
        out JsonNode? result,
        out string? error)
    {
        switch (node)
        {
            case NullReturnNode:
                result = null;
                error = null;
                return true;

            case BooleanReturnNode b:
                result = JsonValue.Create(b.Value);
                error = null;
                return true;

            case NumberReturnNode n:
                result = ParseNumberNode(n.RawText);
                error = null;
                return true;

            case StringLiteralReturnNode s:
                if (!s.HasTemplateMarkers)
                {
                    result = JsonValue.Create(s.Value);
                    error = null;
                    return true;
                }

                if (!TryInterpolateString(
                    s.Value,
                    scalars,
                    activeListItems,
                    accumulatedLists,
                    indexText,
                    guidText,
                    timestampText,
                    out var interpolated,
                    out error))
                {
                    result = null;
                    return false;
                }

                result = JsonValue.Create(interpolated);
                error = null;
                return true;

            case IdentifierReturnNode id:
                return TryResolveIdentifier(
                    id,
                    scalars,
                    activeListItems,
                    accumulatedLists,
                    indexText,
                    guidText,
                    timestampText,
                    out result,
                    out error);

            case ArrayReturnNode arr:
            {
                var jsonArray = new JsonArray();
                foreach (var item in arr.Items)
                {
                    if (!TryEvaluateNode(
                        item,
                        scalars,
                        activeListItems,
                        accumulatedLists,
                        indexText,
                        guidText,
                        timestampText,
                        out var itemNode,
                        out error))
                    {
                        result = null;
                        return false;
                    }

                    jsonArray.Add(itemNode);
                }

                result = jsonArray;
                error = null;
                return true;
            }

            case ObjectReturnNode obj:
            {
                var jsonObj = new JsonObject();
                foreach (var entry in obj.Entries)
                {
                    if (!TryEvaluateNode(
                        entry.Value,
                        scalars,
                        activeListItems,
                        accumulatedLists,
                        indexText,
                        guidText,
                        timestampText,
                        out var valNode,
                        out error))
                    {
                        result = null;
                        return false;
                    }

                    jsonObj[entry.Key] = valNode;
                }

                result = jsonObj;
                error = null;
                return true;
            }

            case ConcatReturnNode concat:
            {
                var sb = new StringBuilder();
                foreach (var part in concat.Parts)
                {
                    if (!TryEvaluateNode(
                        part,
                        scalars,
                        activeListItems,
                        accumulatedLists,
                        indexText,
                        guidText,
                        timestampText,
                        out var partNode,
                        out error))
                    {
                        result = null;
                        return false;
                    }

                    sb.Append(NodeToInlineString(partNode));
                }

                result = JsonValue.Create(sb.ToString());
                error = null;
                return true;
            }

            default:
                result = null;
                error = "Nœud d'expression de retour non pris en charge.";
                return false;
        }
    }

    private static bool TryResolveIdentifier(
        IdentifierReturnNode id,
        IReadOnlyDictionary<string, string> scalars,
        IReadOnlyDictionary<string, string>? activeListItems,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? accumulatedLists,
        string indexText,
        string guidText,
        string timestampText,
        out JsonNode? result,
        out string? error)
    {
        switch (id.Name)
        {
            case "$index":
                result = JsonValue.Create(indexText);
                error = null;
                return true;
            case "$guid":
                result = JsonValue.Create(guidText);
                error = null;
                return true;
            case "$timestamp":
                result = JsonValue.Create(timestampText);
                error = null;
                return true;
        }

        if (id.ForceList)
        {
            if (activeListItems is not null && activeListItems.TryGetValue(id.Name, out var itemValue))
            {
                result = JsonValue.Create(itemValue ?? string.Empty);
                error = null;
                return true;
            }

            if (accumulatedLists is not null && accumulatedLists.TryGetValue(id.Name, out var listValues))
            {
                result = BuildJsonArray(listValues);
                error = null;
                return true;
            }

            result = null;
            error = $"Liste inconnue ou non définie : [[{id.Name}]].";
            return false;
        }

        if (id.ForceScalar)
        {
            if (scalars.TryGetValue(id.Name, out var scalarVal))
            {
                result = JsonValue.Create(scalarVal ?? string.Empty);
                error = null;
                return true;
            }

            result = null;
            error = $"Variable inconnue ou non définie : {{{{{id.Name}}}}}.";
            return false;
        }

        if (activeListItems is not null && activeListItems.TryGetValue(id.Name, out var activeItem))
        {
            result = JsonValue.Create(activeItem ?? string.Empty);
            error = null;
            return true;
        }

        if (scalars.TryGetValue(id.Name, out var varVal))
        {
            result = JsonValue.Create(varVal ?? string.Empty);
            error = null;
            return true;
        }

        if (accumulatedLists is not null && accumulatedLists.TryGetValue(id.Name, out var fullList))
        {
            result = BuildJsonArray(fullList);
            error = null;
            return true;
        }

        result = null;
        error = $"Variable ou liste inconnue : « {id.Name} ». Utilise une variable extraite, une variable de l'étape ou entoure le texte de guillemets.";
        return false;
    }

    private static bool TryInterpolateString(
        string template,
        IReadOnlyDictionary<string, string> scalars,
        IReadOnlyDictionary<string, string>? activeListItems,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? accumulatedLists,
        string indexText,
        string guidText,
        string timestampText,
        out string output,
        out string? error)
    {
        var matches = TemplateMarkerRegex.Matches(template);
        if (matches.Count == 0)
        {
            output = template;
            error = null;
            return true;
        }

        var sb = new StringBuilder(template.Length + 16);
        var lastIdx = 0;

        foreach (Match match in matches)
        {
            sb.Append(template, lastIdx, match.Index - lastIdx);

            var isList = match.Groups[2].Success;
            var name = isList ? match.Groups[2].Value : match.Groups[1].Value;
            if (!TryResolveIdentifier(
                new IdentifierReturnNode(name, ForceScalar: !isList, ForceList: isList),
                scalars,
                activeListItems,
                accumulatedLists,
                indexText,
                guidText,
                timestampText,
                out var resolvedNode,
                out error))
            {
                output = string.Empty;
                return false;
            }

            sb.Append(NodeToInlineString(resolvedNode));
            lastIdx = match.Index + match.Length;
        }

        sb.Append(template, lastIdx, template.Length - lastIdx);
        output = sb.ToString();
        error = null;
        return true;
    }

    private static JsonArray BuildJsonArray(IReadOnlyList<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
        {
            arr.Add(JsonValue.Create(v ?? string.Empty));
        }
        return arr;
    }

    private static JsonNode ParseNumberNode(string rawText)
    {
        if (!rawText.Contains('.', StringComparison.Ordinal) &&
            long.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
        {
            return JsonValue.Create(longVal)!;
        }

        if (decimal.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out var decVal))
        {
            return JsonValue.Create(decVal)!;
        }

        if (double.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out var dblVal))
        {
            return JsonValue.Create(dblVal)!;
        }

        return JsonValue.Create(rawText)!;
    }

    private static string NodeToInlineString(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue val && val.TryGetValue<string>(out var str))
        {
            return str;
        }

        return node.ToJsonString(CompactOptions);
    }

    private static StepReturnEvaluationResult FormatOutput(JsonNode? node)
    {
        if (node is null)
        {
            return new StepReturnEvaluationResult(true, "null", IsJson: true, null);
        }

        if (node is JsonValue val && val.TryGetValue<string>(out var str))
        {
            var isJson = LooksLikeValidJson(str);
            return new StepReturnEvaluationResult(true, str, isJson, null);
        }

        if (node is JsonValue)
        {
            return new StepReturnEvaluationResult(true, node.ToJsonString(CompactOptions), IsJson: true, null);
        }

        return new StepReturnEvaluationResult(true, node.ToJsonString(IndentedOptions), IsJson: true, null);
    }

    private static bool LooksLikeValidJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
