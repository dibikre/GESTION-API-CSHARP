using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed record ConditionEvaluationResult(
    bool IsSuccess,
    IReadOnlyList<string> Values,
    string? ErrorMessage);

public static class ExtractionConditionEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private sealed record CompiledRule(
        ExtractionConditionOperator Operator,
        string TextValue,
        string Replacement,
        int RegexGroup,
        bool IgnoreCase,
        Regex? CompiledRegex,
        int IntThreshold,
        double DoubleThreshold)
    {
        public bool IsTransform => Operator is ExtractionConditionOperator.RegexExtract
            or ExtractionConditionOperator.RegexReplace;
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

        return listResult.Values.Count > 0
            ? ExtractionOutcome.Success(listResult.Values[0])
            : ExtractionOutcome.Failure("La valeur extraite ne satisfait pas les conditions.");
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
                var item = raw ?? string.Empty;
                if (!TryProcessItem(item, rules, matchMode, out var processed))
                {
                    continue;
                }

                if (seen is null || seen.Add(processed))
                {
                    output.Add(processed);
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

            var val = c.Value ?? string.Empty;
            var lineNum = i + 1;

            if (c.Operator != ExtractionConditionOperator.NotEmpty && string.IsNullOrEmpty(val))
            {
                error = $"Condition #{lineNum} : valeur ou motif vide.";
                return false;
            }

            Regex? regex = null;
            if (c.Operator is ExtractionConditionOperator.RegexMatch
                or ExtractionConditionOperator.RegexNotMatch
                or ExtractionConditionOperator.RegexExtract
                or ExtractionConditionOperator.RegexReplace)
            {
                var options = RegexOptions.CultureInvariant;
                if (c.IgnoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                try
                {
                    regex = new Regex(val, options, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    error = $"Condition #{lineNum} : Regex invalide ({ex.Message}).";
                    return false;
                }
            }

            var intThreshold = 0;
            if (c.Operator is ExtractionConditionOperator.LengthMin or ExtractionConditionOperator.LengthMax)
            {
                if (!int.TryParse(val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intThreshold) || intThreshold < 0)
                {
                    error = $"Condition #{lineNum} : longueur attendue entière >= 0 (reçu « {val} »).";
                    return false;
                }
            }

            var doubleThreshold = 0d;
            if (c.Operator is ExtractionConditionOperator.GreaterThan or ExtractionConditionOperator.LessThan)
            {
                if (!double.TryParse(val.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out doubleThreshold)
                    || !double.IsFinite(doubleThreshold))
                {
                    error = $"Condition #{lineNum} : nombre attendu (reçu « {val} »).";
                    return false;
                }
            }

            compiled.Add(new CompiledRule(
                c.Operator,
                val,
                c.Replacement ?? string.Empty,
                Math.Max(0, c.RegexGroup),
                c.IgnoreCase,
                regex,
                intThreshold,
                doubleThreshold));
        }

        return true;
    }

    private static bool TryProcessItem(
        string initial,
        List<CompiledRule> rules,
        ExtractionConditionMatchMode matchMode,
        out string result)
    {
        var current = initial;
        if (rules.Count == 0)
        {
            result = current;
            return true;
        }

        if (matchMode == ExtractionConditionMatchMode.All)
        {
            foreach (var rule in rules)
            {
                if (!TryApplySingleRuleAllMode(rule, ref current))
                {
                    result = string.Empty;
                    return false;
                }
            }

            result = current;
            return true;
        }

        var hasFilter = false;
        var filterPassed = false;
        var extractRules = new List<CompiledRule>();
        var replaceRules = new List<CompiledRule>();

        foreach (var rule in rules)
        {
            if (!rule.IsTransform)
            {
                hasFilter = true;
                if (!filterPassed && EvaluateFilter(rule, current))
                {
                    filterPassed = true;
                }
            }
            else if (rule.Operator == ExtractionConditionOperator.RegexExtract)
            {
                extractRules.Add(rule);
            }
            else
            {
                replaceRules.Add(rule);
            }
        }

        if (hasFilter && !filterPassed)
        {
            result = string.Empty;
            return false;
        }

        if (extractRules.Count > 0)
        {
            if (!hasFilter && extractRules.Count > 1)
            {
                var anyExtracted = false;
                foreach (var extRule in extractRules)
                {
                    if (TryExtractRegexGroup(extRule, current, out var extracted))
                    {
                        current = extracted;
                        anyExtracted = true;
                        break;
                    }
                }

                if (!anyExtracted)
                {
                    result = string.Empty;
                    return false;
                }
            }
            else
            {
                foreach (var extRule in extractRules)
                {
                    if (!TryExtractRegexGroup(extRule, current, out var extracted))
                    {
                        result = string.Empty;
                        return false;
                    }

                    current = extracted;
                }
            }
        }

        foreach (var repRule in replaceRules)
        {
            current = repRule.CompiledRegex!.Replace(current, repRule.Replacement);
        }

        result = current;
        return true;
    }

    private static bool TryApplySingleRuleAllMode(CompiledRule rule, ref string current)
    {
        switch (rule.Operator)
        {
            case ExtractionConditionOperator.RegexExtract:
                if (!TryExtractRegexGroup(rule, current, out var extracted))
                {
                    return false;
                }

                current = extracted;
                return true;

            case ExtractionConditionOperator.RegexReplace:
                current = rule.CompiledRegex!.Replace(current, rule.Replacement);
                return true;

            default:
                return EvaluateFilter(rule, current);
        }
    }

    private static bool TryExtractRegexGroup(CompiledRule rule, string input, out string extracted)
    {
        var match = rule.CompiledRegex!.Match(input);
        if (!match.Success || rule.RegexGroup >= match.Groups.Count)
        {
            extracted = string.Empty;
            return false;
        }

        var grp = match.Groups[rule.RegexGroup];
        if (!grp.Success)
        {
            extracted = string.Empty;
            return false;
        }

        extracted = grp.Value;
        return true;
    }

    private static bool EvaluateFilter(CompiledRule rule, string value)
    {
        var cmp = rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return rule.Operator switch
        {
            ExtractionConditionOperator.Contains => value.Contains(rule.TextValue, cmp),
            ExtractionConditionOperator.NotContains => !value.Contains(rule.TextValue, cmp),
            ExtractionConditionOperator.StartsWith => value.StartsWith(rule.TextValue, cmp),
            ExtractionConditionOperator.EndsWith => value.EndsWith(rule.TextValue, cmp),
            ExtractionConditionOperator.Equals => string.Equals(value, rule.TextValue, cmp),
            ExtractionConditionOperator.NotEquals => !string.Equals(value, rule.TextValue, cmp),
            ExtractionConditionOperator.RegexMatch => rule.CompiledRegex!.IsMatch(value),
            ExtractionConditionOperator.RegexNotMatch => !rule.CompiledRegex!.IsMatch(value),
            ExtractionConditionOperator.NotEmpty => !string.IsNullOrWhiteSpace(value),
            ExtractionConditionOperator.LengthMin => value.Length >= rule.IntThreshold,
            ExtractionConditionOperator.LengthMax => value.Length <= rule.IntThreshold,
            ExtractionConditionOperator.GreaterThan =>
                double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var gtVal)
                && gtVal > rule.DoubleThreshold,
            ExtractionConditionOperator.LessThan =>
                double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ltVal)
                && ltVal < rule.DoubleThreshold,
            _ => true
        };
    }
}
