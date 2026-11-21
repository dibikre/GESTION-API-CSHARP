using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

internal sealed record CompiledActionBranch(
    ExtractionConditionalAction Action,
    string TextValue,
    string Replacement,
    int RegexGroup,
    int SegmentIndex,
    bool IgnoreCase,
    Regex? CompiledRegex);

internal static class ExtractionBranchExecutor
{
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly char[] ListSeparators = [',', ';', '|', '\n', '\r'];

    public static bool TryCompileBranch(
        ExtractionConditionalAction action,
        string? rawValue,
        string? rawReplacement,
        int regexGroup,
        int segmentIndex,
        bool ignoreCase,
        string contextLabel,
        out CompiledActionBranch? branch,
        out string? error)
    {
        branch = null;
        error = null;

        var val = rawValue ?? string.Empty;
        var rep = rawReplacement ?? string.Empty;

        if (action == ExtractionConditionalAction.Between)
        {
            if (string.IsNullOrEmpty(val) && string.IsNullOrEmpty(rep))
            {
                error = $"{contextLabel} : indique au moins un délimiteur gauche ou droit.";
                return false;
            }
        }
        else if (action is not (
            ExtractionConditionalAction.Keep
            or ExtractionConditionalAction.Exclude
            or ExtractionConditionalAction.SetValue
            or ExtractionConditionalAction.Trim
            or ExtractionConditionalAction.ToLower
            or ExtractionConditionalAction.ToUpper)
            && string.IsNullOrEmpty(val))
        {
            error = $"{contextLabel} : valeur ou motif vide.";
            return false;
        }

        Regex? regex = null;
        if (action is ExtractionConditionalAction.RegexExtract
            or ExtractionConditionalAction.RegexExtractAll
            or ExtractionConditionalAction.RegexReplace)
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            try
            {
                regex = new Regex(val, options, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                error = $"{contextLabel} : Regex invalide ({ex.Message}).";
                return false;
            }

            if (action is ExtractionConditionalAction.RegexExtract or ExtractionConditionalAction.RegexExtractAll)
            {
                var grp = Math.Max(0, regexGroup);
                if (grp > 0 && !regex.GetGroupNumbers().Contains(grp))
                {
                    error = $"{contextLabel} : Groupe #{grp} absent du motif Regex — entoure la partie à capturer avec (...) ou mets Groupe à 0.";
                    return false;
                }
            }
        }

        branch = new CompiledActionBranch(
            action,
            val,
            rep,
            Math.Max(0, regexGroup),
            Math.Clamp(segmentIndex, -99, 99),
            ignoreCase,
            regex);
        return true;
    }

    public static bool TryMapTransformOperator(ExtractionConditionOperator op, out ExtractionConditionalAction action)
    {
        switch (op)
        {
            case ExtractionConditionOperator.RegexExtract: action = ExtractionConditionalAction.RegexExtract; return true;
            case ExtractionConditionOperator.RegexExtractAll: action = ExtractionConditionalAction.RegexExtractAll; return true;
            case ExtractionConditionOperator.RegexReplace: action = ExtractionConditionalAction.RegexReplace; return true;
            case ExtractionConditionOperator.ReplaceText: action = ExtractionConditionalAction.ReplaceText; return true;
            case ExtractionConditionOperator.PrependText: action = ExtractionConditionalAction.PrependText; return true;
            case ExtractionConditionOperator.AppendText: action = ExtractionConditionalAction.AppendText; return true;
            case ExtractionConditionOperator.SetValue: action = ExtractionConditionalAction.SetValue; return true;
            case ExtractionConditionOperator.Between: action = ExtractionConditionalAction.Between; return true;
            case ExtractionConditionOperator.SplitPart: action = ExtractionConditionalAction.SplitPart; return true;
            case ExtractionConditionOperator.Trim: action = ExtractionConditionalAction.Trim; return true;
            case ExtractionConditionOperator.ToLower: action = ExtractionConditionalAction.ToLower; return true;
            case ExtractionConditionOperator.ToUpper: action = ExtractionConditionalAction.ToUpper; return true;
            default: action = ExtractionConditionalAction.Keep; return false;
        }
    }

    public static void ExecuteBranchOnValue(CompiledActionBranch branch, string current, List<string> output)
    {
        switch (branch.Action)
        {
            case ExtractionConditionalAction.Keep:
                output.Add(current);
                break;

            case ExtractionConditionalAction.Exclude:
                break;

            case ExtractionConditionalAction.SetValue:
                output.Add(branch.TextValue);
                break;

            case ExtractionConditionalAction.PrependText:
                output.Add(string.Concat(branch.TextValue, current));
                break;

            case ExtractionConditionalAction.AppendText:
                output.Add(string.Concat(current, branch.TextValue));
                break;

            case ExtractionConditionalAction.RegexExtract:
                if (TryExtractRegexGroup(branch.CompiledRegex!, branch.RegexGroup, current, out var extracted))
                {
                    output.Add(extracted);
                }
                break;

            case ExtractionConditionalAction.RegexExtractAll:
                foreach (Match match in branch.CompiledRegex!.Matches(current))
                {
                    if (match.Success && branch.RegexGroup < match.Groups.Count)
                    {
                        var grp = match.Groups[branch.RegexGroup];
                        if (grp.Success)
                        {
                            output.Add(grp.Value);
                        }
                    }
                }
                break;

            case ExtractionConditionalAction.RegexReplace:
                output.Add(branch.CompiledRegex!.Replace(current, branch.Replacement));
                break;

            case ExtractionConditionalAction.ReplaceText:
                output.Add(current.Replace(
                    branch.TextValue,
                    branch.Replacement,
                    branch.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                break;

            case ExtractionConditionalAction.Between:
                if (TryExtractBetween(current, branch.TextValue, branch.Replacement, branch.IgnoreCase, out var sliced))
                {
                    output.Add(sliced);
                }
                break;

            case ExtractionConditionalAction.SplitPart:
                if (TrySplitPart(current, branch.TextValue, branch.SegmentIndex, branch.IgnoreCase, out var part))
                {
                    output.Add(part);
                }
                break;

            case ExtractionConditionalAction.Trim:
                output.Add(string.IsNullOrEmpty(branch.TextValue)
                    ? current.Trim()
                    : current.Trim(branch.TextValue.ToCharArray()));
                break;

            case ExtractionConditionalAction.ToLower:
                output.Add(current.ToLowerInvariant());
                break;

            case ExtractionConditionalAction.ToUpper:
                output.Add(current.ToUpperInvariant());
                break;
        }
    }

    public static IReadOnlyList<string> ParseListValues(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(ListSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool TryParseNumber(string? raw, out double number)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            number = 0d;
            return false;
        }

        return double.TryParse(
                   raw.Trim().Replace(',', '.'),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number)
               && double.IsFinite(number);
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

    private static bool TryExtractRegexGroup(Regex regex, int groupIdx, string input, out string extracted)
    {
        var match = regex.Match(input);
        if (!match.Success || groupIdx >= match.Groups.Count)
        {
            extracted = string.Empty;
            return false;
        }

        var grp = match.Groups[groupIdx];
        if (!grp.Success)
        {
            extracted = string.Empty;
            return false;
        }

        extracted = grp.Value;
        return true;
    }
}
