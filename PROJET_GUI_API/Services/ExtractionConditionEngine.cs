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
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private sealed record CompiledRule(
        ExtractionConditionOperator Operator,
        string TextValue,
        string Replacement,
        int RegexGroup,
        int SegmentIndex,
        bool IgnoreCase,
        Regex? CompiledRegex,
        int IntThreshold,
        double DoubleThreshold)
    {
        public bool IsExtractTransform => Operator is ExtractionConditionOperator.RegexExtract
            or ExtractionConditionOperator.RegexExtractAll;

        public bool IsFilterOnly => Operator is ExtractionConditionOperator.Contains
            or ExtractionConditionOperator.NotContains
            or ExtractionConditionOperator.StartsWith
            or ExtractionConditionOperator.EndsWith
            or ExtractionConditionOperator.Equals
            or ExtractionConditionOperator.NotEquals
            or ExtractionConditionOperator.RegexMatch
            or ExtractionConditionOperator.RegexNotMatch
            or ExtractionConditionOperator.NotEmpty
            or ExtractionConditionOperator.LengthMin
            or ExtractionConditionOperator.LengthMax
            or ExtractionConditionOperator.GreaterThan
            or ExtractionConditionOperator.LessThan;
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

        var hasExtractAll = conditions.Any(c => c.IsEnabled && c.Operator == ExtractionConditionOperator.RegexExtractAll);
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

            var val = c.Value ?? string.Empty;
            var rep = c.Replacement ?? string.Empty;
            var lineNum = i + 1;

            if (c.Operator == ExtractionConditionOperator.Between)
            {
                if (string.IsNullOrEmpty(val) && string.IsNullOrEmpty(rep))
                {
                    error = $"Condition #{lineNum} : indique au moins un délimiteur gauche ou droit.";
                    return false;
                }
            }
            else if (c.Operator is not (
                ExtractionConditionOperator.NotEmpty
                or ExtractionConditionOperator.Trim
                or ExtractionConditionOperator.ToLower
                or ExtractionConditionOperator.ToUpper)
                && string.IsNullOrEmpty(val))
            {
                error = $"Condition #{lineNum} : valeur ou motif vide.";
                return false;
            }

            Regex? regex = null;
            if (c.Operator is ExtractionConditionOperator.RegexMatch
                or ExtractionConditionOperator.RegexNotMatch
                or ExtractionConditionOperator.RegexExtract
                or ExtractionConditionOperator.RegexExtractAll
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

                if (c.Operator is ExtractionConditionOperator.RegexExtract or ExtractionConditionOperator.RegexExtractAll)
                {
                    var grp = Math.Max(0, c.RegexGroup);
                    if (grp > 0 && !regex.GetGroupNumbers().Contains(grp))
                    {
                        error = $"Condition #{lineNum} : Groupe #{grp} absent du motif Regex — entoure la partie à capturer avec (...) ou mets Groupe à 0.";
                        return false;
                    }
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
                rep,
                Math.Max(0, c.RegexGroup),
                Math.Clamp(c.SegmentIndex, -99, 99),
                c.IgnoreCase,
                regex,
                intThreshold,
                doubleThreshold));
        }

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

        if (filterRules.Count > 0 && !filterRules.Any(r => EvaluateFilter(r, initial)))
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
            switch (rule.Operator)
            {
                case ExtractionConditionOperator.RegexExtract:
                    if (TryExtractRegexGroup(rule, current, out var extracted))
                    {
                        next.Add(extracted);
                    }
                    break;

                case ExtractionConditionOperator.RegexExtractAll:
                    foreach (Match match in rule.CompiledRegex!.Matches(current))
                    {
                        if (match.Success && rule.RegexGroup < match.Groups.Count)
                        {
                            var grp = match.Groups[rule.RegexGroup];
                            if (grp.Success)
                            {
                                next.Add(grp.Value);
                            }
                        }
                    }
                    break;

                case ExtractionConditionOperator.RegexReplace:
                    next.Add(rule.CompiledRegex!.Replace(current, rule.Replacement));
                    break;

                case ExtractionConditionOperator.ReplaceText:
                    next.Add(current.Replace(
                        rule.TextValue,
                        rule.Replacement,
                        rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                    break;

                case ExtractionConditionOperator.Between:
                    if (TryExtractBetween(current, rule.TextValue, rule.Replacement, rule.IgnoreCase, out var sliced))
                    {
                        next.Add(sliced);
                    }
                    break;

                case ExtractionConditionOperator.SplitPart:
                    if (TrySplitPart(current, rule.TextValue, rule.SegmentIndex, rule.IgnoreCase, out var part))
                    {
                        next.Add(part);
                    }
                    break;

                case ExtractionConditionOperator.Trim:
                    next.Add(string.IsNullOrEmpty(rule.TextValue)
                        ? current.Trim()
                        : current.Trim(rule.TextValue.ToCharArray()));
                    break;

                case ExtractionConditionOperator.ToLower:
                    next.Add(current.ToLowerInvariant());
                    break;

                case ExtractionConditionOperator.ToUpper:
                    next.Add(current.ToUpperInvariant());
                    break;

                default:
                    if (EvaluateFilter(rule, current))
                    {
                        next.Add(current);
                    }
                    break;
            }
        }

        return next;
    }

    private static bool TryExtractBetween(
        string input,
        string leftDelimiter,
        string rightDelimiter,
        bool ignoreCase,
        out string result)
    {
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var startIdx = 0;

        if (!string.IsNullOrEmpty(leftDelimiter))
        {
            var foundLeft = input.IndexOf(leftDelimiter, cmp);
            if (foundLeft < 0)
            {
                result = string.Empty;
                return false;
            }
            startIdx = foundLeft + leftDelimiter.Length;
        }

        if (!string.IsNullOrEmpty(rightDelimiter))
        {
            var foundRight = input.IndexOf(rightDelimiter, startIdx, cmp);
            if (foundRight < 0)
            {
                result = string.Empty;
                return false;
            }
            result = input[startIdx..foundRight];
            return true;
        }

        result = input[startIdx..];
        return true;
    }

    private static bool TrySplitPart(
        string input,
        string separator,
        int segmentIndex,
        bool ignoreCase,
        out string part)
    {
        var parts = ignoreCase
            ? Regex.Split(input, Regex.Escape(separator), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout)
            : input.Split(separator, StringSplitOptions.None);

        var resolved = segmentIndex < 0 ? parts.Length + segmentIndex : segmentIndex;
        if (resolved < 0 || resolved >= parts.Length)
        {
            part = string.Empty;
            return false;
        }

        part = parts[resolved];
        return true;
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
