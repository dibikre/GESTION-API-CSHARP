using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed record ConditionEvaluationResult(
    bool IsSuccess,
    IReadOnlyList<string> Values,
    string? ErrorMessage);

public static class ExtractionConditionEngine
{
    private sealed record CompiledPredicate(
        ExtractionConditionOperator Operator,
        string TextValue,
        bool IgnoreCase,
        Regex? CompiledRegex,
        IReadOnlyList<string> ListValues,
        int IntThreshold,
        double DoubleThreshold);

    private sealed record CompiledRule(
        bool IsConditional,
        CompiledPredicate? Predicate,
        CompiledActionBranch? TransformBranch,
        CompiledActionBranch? ThenBranch,
        CompiledActionBranch? ElseBranch)
    {
        public bool IsFilterOnly => !IsConditional && TransformBranch is null;

        public bool IsExtractTransform => !IsConditional && TransformBranch?.Action is
            ExtractionConditionalAction.RegexExtract or ExtractionConditionalAction.RegexExtractAll;
    }

    public static ExtractionOutcome ApplyToScalar(
        string rawValue,
        IReadOnlyList<ChainExtractionCondition> conditions,
        ExtractionConditionMatchMode matchMode,
        bool deduplicate)
    {
        var listResult = ApplyToList([rawValue], conditions, matchMode, deduplicate);
        if (!listResult.IsSuccess)
        {
            return ExtractionOutcome.Failure(listResult.ErrorMessage ?? "La valeur extraite ne satisfait pas les conditions.");
        }

        if (listResult.Values.Count == 0)
        {
            return ExtractionOutcome.Failure("La valeur extraite ne satisfait pas les conditions.");
        }

        var hasExtractAll = conditions.Any(c =>
            c.IsEnabled && (
                (!c.IsConditional && c.Operator == ExtractionConditionOperator.RegexExtractAll) ||
                (c.IsConditional && (
                    c.ThenBranch.Action == ExtractionConditionalAction.RegexExtractAll ||
                    c.ElseBranch.Action == ExtractionConditionalAction.RegexExtractAll))));

        return hasExtractAll && listResult.Values.Count > 1
            ? ExtractionOutcome.Success(string.Join(", ", listResult.Values))
            : ExtractionOutcome.Success(listResult.Values[0]);
    }

    public static ConditionEvaluationResult ApplyToList(
        IReadOnlyList<string> rawValues,
        IReadOnlyList<ChainExtractionCondition> conditions,
        ExtractionConditionMatchMode matchMode,
        bool deduplicate,
        bool failWhenEmpty = true)
    {
        if (!TryCompileRules(conditions, out var rules, out var compileError))
        {
            return new ConditionEvaluationResult(false, [], compileError);
        }

        if (rules.Count == 0 && !deduplicate)
        {
            return new ConditionEvaluationResult(true, rawValues, null);
        }

        var output = new List<string>(rawValues.Count);
        var seen = deduplicate ? new HashSet<string>(StringComparer.Ordinal) : null;

        try
        {
            foreach (var raw in rawValues)
            {
                var items = ProcessItem(raw ?? string.Empty, rules, matchMode);
                foreach (var processed in items)
                {
                    if (seen is null || seen.Add(processed))
                    {
                        output.Add(processed);
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new ConditionEvaluationResult(false, [], "Une condition Regex a expiré (> 2 s).");
        }

        if (output.Count == 0 && failWhenEmpty && rawValues.Count > 0)
        {
            return new ConditionEvaluationResult(
                false,
                [],
                $"Aucun élément ne satisfait les conditions ({rawValues.Count} brut(s) filtré(s)).");
        }

        return new ConditionEvaluationResult(true, output, null);
    }

    public static bool ValidateConditions(
        IReadOnlyList<ChainExtractionCondition> conditions,
        out string? error)
    {
        return TryCompileRules(conditions, out _, out error);
    }

    private static bool TryCompileRules(
        IReadOnlyList<ChainExtractionCondition> conditions,
        out List<CompiledRule> compiled,
        out string? error)
    {
        compiled = new List<CompiledRule>(conditions.Count);
        error = null;

        for (var i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            if (!c.IsEnabled)
            {
                continue;
            }

            var lineNum = i + 1;
            var label = $"Condition #{lineNum}";

            if (c.IsConditional)
            {
                if (!ChainExtractionCondition.IsFilterOperator(c.Operator))
                {
                    error = $"{label} : l'opérateur « SI » doit être un test (Contient, Égal à, Nombre, etc.).";
                    return false;
                }

                if (!TryCompilePredicate(c.Operator, c.Value, c.IgnoreCase, $"{label} (SI)", out var pred, out error))
                {
                    return false;
                }

                if (!ExtractionBranchExecutor.TryCompileBranch(
                        c.ThenBranch.Action,
                        c.ThenBranch.Value,
                        c.ThenBranch.Replacement,
                        c.ThenBranch.RegexGroup,
                        c.ThenBranch.SegmentIndex,
                        c.ThenBranch.IgnoreCase,
                        $"{label} (ALORS)",
                        out var thenCompiled,
                        out error))
                {
                    return false;
                }

                if (!ExtractionBranchExecutor.TryCompileBranch(
                        c.ElseBranch.Action,
                        c.ElseBranch.Value,
                        c.ElseBranch.Replacement,
                        c.ElseBranch.RegexGroup,
                        c.ElseBranch.SegmentIndex,
                        c.ElseBranch.IgnoreCase,
                        $"{label} (SINON)",
                        out var elseCompiled,
                        out error))
                {
                    return false;
                }

                compiled.Add(new CompiledRule(true, pred, null, thenCompiled, elseCompiled));
                continue;
            }

            if (ExtractionBranchExecutor.TryMapTransformOperator(c.Operator, out var action))
            {
                if (!ExtractionBranchExecutor.TryCompileBranch(
                        action,
                        c.Value,
                        c.Replacement,
                        c.RegexGroup,
                        c.SegmentIndex,
                        c.IgnoreCase,
                        label,
                        out var trBranch,
                        out error))
                {
                    return false;
                }

                compiled.Add(new CompiledRule(false, null, trBranch, null, null));
            }
            else
            {
                if (!TryCompilePredicate(c.Operator, c.Value, c.IgnoreCase, label, out var pred, out error))
                {
                    return false;
                }

                compiled.Add(new CompiledRule(false, pred, null, null, null));
            }
        }

        return true;
    }

    private static bool TryCompilePredicate(
        ExtractionConditionOperator op,
        string? rawValue,
        bool ignoreCase,
        string contextLabel,
        out CompiledPredicate? predicate,
        out string? error)
    {
        predicate = null;
        error = null;
        var val = rawValue ?? string.Empty;

        if (op is not (ExtractionConditionOperator.IsEmpty or ExtractionConditionOperator.NotEmpty)
            && string.IsNullOrEmpty(val))
        {
            error = $"{contextLabel} : valeur ou motif vide.";
            return false;
        }

        IReadOnlyList<string> listValues = [];
        if (op is ExtractionConditionOperator.InList or ExtractionConditionOperator.NotInList)
        {
            listValues = ExtractionBranchExecutor.ParseListValues(val);
            if (listValues.Count == 0)
            {
                error = $"{contextLabel} : indique au moins une valeur séparée par , ou |.";
                return false;
            }
        }

        Regex? regex = null;
        if (op is ExtractionConditionOperator.RegexMatch or ExtractionConditionOperator.RegexNotMatch)
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            try
            {
                regex = new Regex(val, options, ExtractionBranchExecutor.RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                error = $"{contextLabel} : Regex invalide ({ex.Message}).";
                return false;
            }
        }

        var intThreshold = 0;
        if (op is ExtractionConditionOperator.LengthEquals
            or ExtractionConditionOperator.LengthMin
            or ExtractionConditionOperator.LengthMax)
        {
            if (!int.TryParse(val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intThreshold) || intThreshold < 0)
            {
                error = $"{contextLabel} : longueur attendue entière >= 0 (reçu « {val} »).";
                return false;
            }
        }

        var doubleThreshold = 0d;
        if (op is ExtractionConditionOperator.NumberEquals
            or ExtractionConditionOperator.NumberNotEquals
            or ExtractionConditionOperator.GreaterThan
            or ExtractionConditionOperator.GreaterOrEqual
            or ExtractionConditionOperator.LessThan
            or ExtractionConditionOperator.LessOrEqual)
        {
            if (!ExtractionBranchExecutor.TryParseNumber(val, out doubleThreshold))
            {
                error = $"{contextLabel} : nombre attendu (reçu « {val} »).";
                return false;
            }
        }

        predicate = new CompiledPredicate(op, val, ignoreCase, regex, listValues, intThreshold, doubleThreshold);
        return true;
    }

    private static List<string> ProcessItem(
        string initial,
        List<CompiledRule> rules,
        ExtractionConditionMatchMode matchMode)
    {
        if (rules.Count == 0)
        {
            return [initial];
        }

        var currentItems = new List<string> { initial };

        if (matchMode == ExtractionConditionMatchMode.All)
        {
            foreach (var rule in rules)
            {
                currentItems = ApplySingleRuleBatch(rule, currentItems);
                if (currentItems.Count == 0)
                {
                    return currentItems;
                }
            }

            return currentItems;
        }

        var filterRules = new List<CompiledRule>();
        var extractRules = new List<CompiledRule>();
        var transformRules = new List<CompiledRule>();

        foreach (var rule in rules)
        {
            if (rule.IsFilterOnly)
            {
                filterRules.Add(rule);
            }
            else if (rule.IsExtractTransform)
            {
                extractRules.Add(rule);
            }
            else
            {
                transformRules.Add(rule);
            }
        }

        if (filterRules.Count > 0 && !filterRules.Any(r => EvaluatePredicate(r.Predicate!, initial)))
        {
            return [];
        }

        if (extractRules.Count > 0)
        {
            if (filterRules.Count == 0 && extractRules.Count > 1)
            {
                var matched = false;
                foreach (var extRule in extractRules)
                {
                    var candidate = ApplySingleRuleBatch(extRule, currentItems);
                    if (candidate.Count > 0)
                    {
                        currentItems = candidate;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    return [];
                }
            }
            else
            {
                foreach (var extRule in extractRules)
                {
                    currentItems = ApplySingleRuleBatch(extRule, currentItems);
                    if (currentItems.Count == 0)
                    {
                        return [];
                    }
                }
            }
        }

        foreach (var trRule in transformRules)
        {
            currentItems = ApplySingleRuleBatch(trRule, currentItems);
            if (currentItems.Count == 0)
            {
                return [];
            }
        }

        return currentItems;
    }

    private static List<string> ApplySingleRuleBatch(CompiledRule rule, List<string> inputs)
    {
        var next = new List<string>(inputs.Count);

        foreach (var current in inputs)
        {
            if (rule.IsConditional)
            {
                var branch = EvaluatePredicate(rule.Predicate!, current)
                    ? rule.ThenBranch!
                    : rule.ElseBranch!;
                ExtractionBranchExecutor.ExecuteBranchOnValue(branch, current, next);
            }
            else if (rule.TransformBranch is not null)
            {
                ExtractionBranchExecutor.ExecuteBranchOnValue(rule.TransformBranch, current, next);
            }
            else if (EvaluatePredicate(rule.Predicate!, current))
            {
                next.Add(current);
            }
        }

        return next;
    }

    private static bool EvaluatePredicate(CompiledPredicate pred, string value)
    {
        var cmp = pred.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return pred.Operator switch
        {
            ExtractionConditionOperator.Contains => value.Contains(pred.TextValue, cmp),
            ExtractionConditionOperator.NotContains => !value.Contains(pred.TextValue, cmp),
            ExtractionConditionOperator.StartsWith => value.StartsWith(pred.TextValue, cmp),
            ExtractionConditionOperator.NotStartsWith => !value.StartsWith(pred.TextValue, cmp),
            ExtractionConditionOperator.EndsWith => value.EndsWith(pred.TextValue, cmp),
            ExtractionConditionOperator.NotEndsWith => !value.EndsWith(pred.TextValue, cmp),
            ExtractionConditionOperator.Equals => string.Equals(value, pred.TextValue, cmp),
            ExtractionConditionOperator.NotEquals => !string.Equals(value, pred.TextValue, cmp),
            ExtractionConditionOperator.InList => pred.ListValues.Any(item => string.Equals(value, item, cmp)),
            ExtractionConditionOperator.NotInList => !pred.ListValues.Any(item => string.Equals(value, item, cmp)),
            ExtractionConditionOperator.RegexMatch => pred.CompiledRegex!.IsMatch(value),
            ExtractionConditionOperator.RegexNotMatch => !pred.CompiledRegex!.IsMatch(value),
            ExtractionConditionOperator.IsEmpty => string.IsNullOrWhiteSpace(value),
            ExtractionConditionOperator.NotEmpty => !string.IsNullOrWhiteSpace(value),
            ExtractionConditionOperator.LengthEquals => value.Length == pred.IntThreshold,
            ExtractionConditionOperator.LengthMin => value.Length >= pred.IntThreshold,
            ExtractionConditionOperator.LengthMax => value.Length <= pred.IntThreshold,
            ExtractionConditionOperator.NumberEquals =>
                ExtractionBranchExecutor.TryParseNumber(value, out var eqVal) && eqVal == pred.DoubleThreshold,
            ExtractionConditionOperator.NumberNotEquals =>
                ExtractionBranchExecutor.TryParseNumber(value, out var neqVal) && neqVal != pred.DoubleThreshold,
            ExtractionConditionOperator.GreaterThan =>
                ExtractionBranchExecutor.TryParseNumber(value, out var gtVal) && gtVal > pred.DoubleThreshold,
            ExtractionConditionOperator.GreaterOrEqual =>
                ExtractionBranchExecutor.TryParseNumber(value, out var gteVal) && gteVal >= pred.DoubleThreshold,
            ExtractionConditionOperator.LessThan =>
                ExtractionBranchExecutor.TryParseNumber(value, out var ltVal) && ltVal < pred.DoubleThreshold,
            ExtractionConditionOperator.LessOrEqual =>
                ExtractionBranchExecutor.TryParseNumber(value, out var lteVal) && lteVal <= pred.DoubleThreshold,
            _ => true
        };
    }
}
