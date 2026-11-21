using System;
using System.Collections.Generic;
using System.Linq;
using ApiTester.Models;

namespace ApiTester.Services;

internal static class ChainDeclaredVariableBuilder
{
    private const int MaxPreviewItems = 250;

    public static List<ChainDeclaredVariableItem> Build(
        IReadOnlyList<ChainStep> steps,
        IReadOnlyDictionary<string, string> runtimeVariables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> runtimeLists,
        IReadOnlyDictionary<string, bool>? previousExpandedStates = null)
    {
        var items = new List<ChainDeclaredVariableItem>();
        var extractedSoFar = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var rank = i + 1;

            foreach (var variable in step.Variables)
            {
                if ((!variable.IsUsed && !variable.IsConfigured) || string.IsNullOrWhiteSpace(variable.Name))
                {
                    continue;
                }

                // Une variable non configurée qui consomme une extraction précédente est déjà listée à l'étape source.
                if (!variable.IsConfigured && extractedSoFar.Contains(variable.Name))
                {
                    continue;
                }

                var key = $"{step.Id}:var:{variable.Name}";
                var item = BuildStepVariableItem(key, rank, variable);
                RestoreExpandedState(item, previousExpandedStates);
                items.Add(item);
            }

            var seenExtKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var extraction in step.Extractions)
            {
                if (string.IsNullOrWhiteSpace(extraction.VariableName))
                {
                    continue;
                }

                var name = extraction.VariableName.Trim();
                var baseKey = $"{step.Id}:ext:{(extraction.IsList ? "list" : "scalar")}:{name}";
                seenExtKeys.TryGetValue(baseKey, out var occ);
                seenExtKeys[baseKey] = occ + 1;
                var key = occ == 0 ? baseKey : $"{baseKey}#{occ}";

                var item = BuildExtractionItem(key, rank, name, extraction, runtimeVariables, runtimeLists);
                RestoreExpandedState(item, previousExpandedStates);
                items.Add(item);
                extractedSoFar.Add(name);
            }
        }

        return items;
    }

    private static void RestoreExpandedState(
        ChainDeclaredVariableItem item,
        IReadOnlyDictionary<string, bool>? previousExpandedStates)
    {
        if (previousExpandedStates is not null &&
            previousExpandedStates.TryGetValue(item.Key, out var wasExpanded))
        {
            item.IsExpanded = wasExpanded;
        }
    }

    private static ChainDeclaredVariableItem CreateItem(
        string key,
        string tokenName,
        string rawName,
        string sourceBadge,
        string contentText,
        string rawContentForCopy = "",
        bool hasValue = false,
        bool isSensitive = false) => new()
    {
        Key = key,
        TokenName = tokenName,
        RawName = rawName,
        SourceBadge = sourceBadge,
        ContentText = contentText,
        RawContentForCopy = rawContentForCopy,
        HasValue = hasValue,
        IsSensitive = isSensitive
    };

    private static ChainDeclaredVariableItem BuildStepVariableItem(string key, int rank, BatchVariable variable)
    {
        var name = variable.Name;
        var token = $"{{{{{name}}}}}";
        var isSensitive = BatchExecutionHelper.IsSensitiveName(name);

        if (!variable.IsConfigured)
        {
            return CreateItem(key, token, name, $"Ét. {rank} · À configurer",
                "Aucune valeur configurée dans l'onglet Variables.", isSensitive: isSensitive);
        }

        if (variable.HasError)
        {
            return CreateItem(key, token, name, $"Ét. {rank} · Erreur",
                variable.ErrorText ?? "Configuration invalide.", isSensitive: isSensitive);
        }

        if (!variable.IsVarying)
        {
            return CreateItem(
                key, token, name, $"Ét. {rank} · Fixe",
                isSensitive ? "*** (valeur sensible masquée)" : variable.FixedValue,
                isSensitive ? string.Empty : variable.FixedValue,
                hasValue: true,
                isSensitive: isSensitive);
        }

        var modeLabel = variable.Mode switch
        {
            VariableMode.Range => $"Plage ({variable.ValueCount})",
            VariableMode.List => $"Liste ({variable.ValueCount})",
            VariableMode.Random => $"Aléatoire ({variable.ValueCount})",
            _ => $"{variable.ValueCount} val."
        };
        var badge = $"Ét. {rank} · {modeLabel}";

        if (isSensitive)
        {
            return CreateItem(key, token, name, badge, "*** (valeurs sensibles masquées)",
                hasValue: variable.ValueCount > 0, isSensitive: true);
        }

        IReadOnlyList<string> values = variable.Mode switch
        {
            VariableMode.List => BatchVariable.ParseListValues(variable.ListText),
            VariableMode.Random => variable.GetRandomValuesSnapshot(),
            _ => variable.EnumerateValues().Take(MaxPreviewItems).ToList()
        };

        var displayText = FormatListLines(values, variable.ValueCount);
        var copyText = variable.Mode == VariableMode.Range && variable.ValueCount > MaxPreviewItems
            ? displayText
            : string.Join(Environment.NewLine, values);

        return CreateItem(key, token, name, badge, displayText, copyText, hasValue: variable.ValueCount > 0);
    }

    private static ChainDeclaredVariableItem BuildExtractionItem(
        string key,
        int rank,
        string name,
        ChainExtraction extraction,
        IReadOnlyDictionary<string, string> runtimeVariables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> runtimeLists)
    {
        var isSensitive = BatchExecutionHelper.IsSensitiveName(name);
        var token = extraction.IsList ? $"[[{name}]]" : $"{{{{{name}}}}}";

        if (extraction.IsList)
        {
            IReadOnlyList<string>? resolvedList = null;
            var isTest = false;
            if (runtimeLists.TryGetValue(name, out var runList))
            {
                resolvedList = runList;
            }
            else if (extraction.LastExtractedList is { Count: > 0 } testedList)
            {
                resolvedList = testedList;
                isTest = true;
            }

            if (resolvedList is null)
            {
                return CreateItem(key, token, name, $"Ét. {rank} · Liste",
                    BuildPendingDescription(extraction), isSensitive: isSensitive);
            }

            var badge = $"Ét. {rank} · Liste ({resolvedList.Count}{(isTest ? " · test" : string.Empty)})";
            if (isSensitive)
            {
                return CreateItem(key, token, name, badge,
                    $"[{resolvedList.Count} élément(s)] *** (valeur sensible masquée)",
                    hasValue: resolvedList.Count > 0, isSensitive: true);
            }

            if (resolvedList.Count == 0)
            {
                return CreateItem(key, token, name, badge, "(liste vide après exécution)");
            }

            return CreateItem(key, token, name, badge,
                FormatListLines(resolvedList, resolvedList.Count),
                string.Join(Environment.NewLine, resolvedList),
                hasValue: true);
        }

        string? resolvedScalar = null;
        var scalarIsTest = false;
        if (runtimeVariables.TryGetValue(name, out var runValue))
        {
            resolvedScalar = runValue;
        }
        else if (extraction.LastExtractedValue is not null)
        {
            resolvedScalar = extraction.LastExtractedValue;
            scalarIsTest = true;
        }

        if (resolvedScalar is null)
        {
            return CreateItem(key, token, name, $"Ét. {rank} · Extraction",
                BuildPendingDescription(extraction), isSensitive: isSensitive);
        }

        var scalarBadge = scalarIsTest ? $"Ét. {rank} · Extr. (test)" : $"Ét. {rank} · Extraction";
        if (isSensitive)
        {
            return CreateItem(key, token, name, scalarBadge, "*** (valeur sensible masquée)",
                hasValue: true, isSensitive: true);
        }

        return CreateItem(key, token, name, scalarBadge,
            string.IsNullOrEmpty(resolvedScalar) ? "(chaîne vide)" : resolvedScalar,
            resolvedScalar, hasValue: true);
    }

    private static string FormatListLines(IReadOnlyList<string> values, long totalCount)
    {
        if (values.Count == 0)
        {
            return "(vide)";
        }

        var shown = values.Count > MaxPreviewItems ? values.Take(MaxPreviewItems) : values;
        var text = string.Join(Environment.NewLine, shown);
        if (totalCount > MaxPreviewItems)
        {
            text += $"{Environment.NewLine}… ({totalCount:N0} valeurs au total ; clique sur « Voir » pour tout afficher)";
        }

        return text;
    }

    private static string BuildPendingDescription(ChainExtraction extraction)
    {
        string desc;
        if (extraction.Kind == ExtractionKind.FullResponse && extraction.Source != ChainExtractionSource.Header)
        {
            var varName = string.IsNullOrWhiteSpace(extraction.VariableName)
                ? "variable"
                : extraction.VariableName.Trim();
            var sourceDesc = extraction.Source == ChainExtractionSource.Variable
                ? $"Variable « {extraction.SourceVariableName?.Trim()} »"
                : "Corps";
            desc = $"En attente d'exécution (Valeur complète sur {sourceDesc} ; ex. {{{{{varName}.champ}}}})";
        }
        else
        {
            var kind = extraction.Kind == ExtractionKind.Regex ? "Regex" : "JSONPath";
            var source = extraction.Source switch
            {
                ChainExtractionSource.Header => $"En-tête « {extraction.HeaderName?.Trim()} »",
                ChainExtractionSource.Variable => $"Variable « {extraction.SourceVariableName?.Trim()} »",
                _ => "Corps"
            };
            var expr = string.IsNullOrWhiteSpace(extraction.Expression)
                ? "(expression vide)"
                : extraction.Expression.Trim();

            desc = $"En attente d'exécution ({kind} sur {source} : {expr})";
        }
        if (extraction.HasConditions)
        {
            desc += $"{Environment.NewLine}{extraction.ConditionsSummaryText}";
        }
        if (!string.IsNullOrEmpty(extraction.DefaultValueIfMissing))
        {
            desc += $"{Environment.NewLine}Valeur par défaut : {extraction.DefaultValueIfMissing}";
        }

        return desc;
    }
}
