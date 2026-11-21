using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed record ManualConditionsParseResult(
    bool IsSuccess,
    List<ChainExtractionCondition> Conditions,
    ExtractionConditionMatchMode MatchMode,
    bool Deduplicate,
    string? ErrorMessage);

public static class ExtractionConditionSyntax
{
    public static string FormatManual(
        IReadOnlyList<ChainExtractionCondition> conditions,
        ExtractionConditionMatchMode matchMode,
        bool deduplicate,
        bool includeExamplesWhenEmpty = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(matchMode == ExtractionConditionMatchMode.Any ? "mode: ou" : "mode: et");
        sb.AppendLine(deduplicate ? "unique: true" : "unique: false");

        if (conditions.Count == 0 && includeExamplesWhenEmpty)
        {
            sb.AppendLine();
            sb.AppendLine("# Une règle par ligne (# ou // pour commenter) :");
            sb.AppendLine("# regex_extraire \"([0-9a-fA-F]{16,})\"");
            sb.AppendLine("# decouper \"/\" index=-1");
            sb.AppendLine("# dans_liste \"200, 201, 204\"");
            sb.AppendLine("# nombre >= 100");
            sb.AppendLine("# si contient \"-full\" alors regex_extraire \"([0-9a-fA-F]{16,})\" sinon exclure");
            sb.AppendLine("# si egal \"\" alors definir \"N/A\" sinon garder");
            return sb.ToString();
        }

        if (conditions.Count > 0)
        {
            sb.AppendLine();
        }

        foreach (var cond in conditions)
        {
            sb.AppendLine(FormatRuleLine(cond));
        }

        return sb.ToString();
    }

    public static ManualConditionsParseResult ParseManual(string? text)
    {
        var conditions = new List<ChainExtractionCondition>();
        var matchMode = ExtractionConditionMatchMode.All;
        var deduplicate = false;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ManualConditionsParseResult(true, conditions, matchMode, deduplicate, null);
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith('#') ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseDirective(trimmed, ref matchMode, ref deduplicate, out var directiveError))
            {
                if (directiveError is not null)
                {
                    return new ManualConditionsParseResult(false, [], matchMode, deduplicate, $"Ligne {lineNumber} : {directiveError}");
                }
                continue;
            }

            if (!TryParseRuleLine(trimmed, out var condition, out var ruleError))
            {
                return new ManualConditionsParseResult(false, [], matchMode, deduplicate, $"Ligne {lineNumber} : {ruleError}");
            }

            conditions.Add(condition!);
        }

        return new ManualConditionsParseResult(true, conditions, matchMode, deduplicate, null);
    }

    private static bool TryParseDirective(
        string line,
        ref ExtractionConditionMatchMode matchMode,
        ref bool deduplicate,
        out string? error)
    {
        error = null;
        if (line.Equals("dedoublonner", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("distinct", StringComparison.OrdinalIgnoreCase))
        {
            deduplicate = true;
            return true;
        }

        if (line.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = line[5..].Trim().ToLowerInvariant();
            switch (raw)
            {
                case "et" or "all" or "and":
                    matchMode = ExtractionConditionMatchMode.All;
                    return true;
                case "ou" or "any" or "or":
                    matchMode = ExtractionConditionMatchMode.Any;
                    return true;
                default:
                    error = $"mode inconnu « {raw} » (utilise « et » ou « ou »).";
                    return true;
            }
        }

        if (line.StartsWith("unique:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = line[7..].Trim().ToLowerInvariant();
            switch (raw)
            {
                case "true" or "oui" or "1":
                    deduplicate = true;
                    return true;
                case "false" or "non" or "0":
                    deduplicate = false;
                    return true;
                default:
                    error = $"valeur unique invalide « {raw} » (true/false).";
                    return true;
            }
        }

        return false;
    }

    private static bool TryParseRuleLine(
        string line,
        out ChainExtractionCondition? condition,
        out string? error)
    {
        condition = null;
        error = null;

        var rawTokens = TokenizeLine(line, out var tokError);
        if (tokError is not null)
        {
            error = tokError;
            return false;
        }

        if (rawTokens.Count == 0)
        {
            error = "ligne vide.";
            return false;
        }

        var isEnabled = true;
        var tokensWithoutDisabled = new List<(string Text, bool Quoted)>(rawTokens.Count);
        foreach (var tok in rawTokens)
        {
            if (!tok.Quoted && (tok.Text.Equals("[inactif]", StringComparison.OrdinalIgnoreCase) ||
                                tok.Text.Equals("[off]", StringComparison.OrdinalIgnoreCase)))
            {
                isEnabled = false;
            }
            else
            {
                tokensWithoutDisabled.Add(tok);
            }
        }

        if (tokensWithoutDisabled.Count == 0)
        {
            error = "opérateur manquant.";
            return false;
        }

        if (!tokensWithoutDisabled[0].Quoted &&
            (tokensWithoutDisabled[0].Text.Equals("si", StringComparison.OrdinalIgnoreCase) ||
             tokensWithoutDisabled[0].Text.Equals("if", StringComparison.OrdinalIgnoreCase)))
        {
            return TryParseConditionalRule(tokensWithoutDisabled, isEnabled, out condition, out error);
        }

        return TryParseSimpleRule(tokensWithoutDisabled, isEnabled, out condition, out error);
    }

    private static bool TryParseConditionalRule(
        List<(string Text, bool Quoted)> tokens,
        bool isEnabled,
        out ChainExtractionCondition? condition,
        out string? error)
    {
        condition = null;
        error = null;

        var alorsIdx = -1;
        var sinonIdx = -1;
        for (var i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Quoted)
            {
                continue;
            }

            if (alorsIdx < 0 && (tokens[i].Text.Equals("alors", StringComparison.OrdinalIgnoreCase) ||
                                 tokens[i].Text.Equals("then", StringComparison.OrdinalIgnoreCase)))
            {
                alorsIdx = i;
            }
            else if (alorsIdx > 0 && sinonIdx < 0 &&
                     (tokens[i].Text.Equals("sinon", StringComparison.OrdinalIgnoreCase) ||
                      tokens[i].Text.Equals("else", StringComparison.OrdinalIgnoreCase)))
            {
                sinonIdx = i;
            }
        }

        if (alorsIdx < 0)
        {
            error = "mot-clé « alors » manquant dans la règle « si ... alors ... ».";
            return false;
        }

        var siTokens = tokens.GetRange(1, alorsIdx - 1);
        var alorsEnd = sinonIdx > 0 ? sinonIdx : tokens.Count;
        var alorsTokens = tokens.GetRange(alorsIdx + 1, alorsEnd - (alorsIdx + 1));
        var sinonTokens = sinonIdx > 0 ? tokens.GetRange(sinonIdx + 1, tokens.Count - (sinonIdx + 1)) : [];

        if (siTokens.Count == 0)
        {
            error = "condition manquante après « si ».";
            return false;
        }

        if (alorsTokens.Count == 0)
        {
            error = "action manquante après « alors ».";
            return false;
        }

        if (!TryParseSimpleRule(siTokens, isEnabled, out var predRule, out var siErr))
        {
            error = $"clause SI : {siErr}";
            return false;
        }

        if (!ChainExtractionCondition.IsFilterOperator(predRule!.Operator))
        {
            error = "la clause SI attend un test (contient, egal, dans_liste, nombre, regex, etc.).";
            return false;
        }

        if (!TryParseBranchTokens(alorsTokens, out var thenBranch, out var thenErr))
        {
            error = $"clause ALORS : {thenErr}";
            return false;
        }

        var elseBranch = new ChainConditionalBranch { Action = ExtractionConditionalAction.Keep };
        if (sinonTokens.Count > 0)
        {
            if (!TryParseBranchTokens(sinonTokens, out var parsedElse, out var elseErr))
            {
                error = $"clause SINON : {elseErr}";
                return false;
            }
            elseBranch = parsedElse!;
        }

        condition = new ChainExtractionCondition
        {
            IsEnabled = isEnabled,
            IsConditional = true,
            Operator = predRule.Operator,
            Value = predRule.Value,
            IgnoreCase = predRule.IgnoreCase
        };
        condition.ReplaceBranches(thenBranch!, elseBranch);
        return true;
    }

    private static bool TryParseSimpleRule(
        List<(string Text, bool Quoted)> tokens,
        bool isEnabled,
        out ChainExtractionCondition? condition,
        out string? error)
    {
        condition = null;
        error = null;

        var ignoreCase = ExtractCaseFlags(tokens, out var cleaned);
        if (cleaned.Count == 0)
        {
            error = "opérateur manquant.";
            return false;
        }

        var idx = 0;
        if (!TryMatchOperator(cleaned, ref idx, out var op, out var opError))
        {
            error = opError;
            return false;
        }

        if (op is ExtractionConditionOperator.IsEmpty
            or ExtractionConditionOperator.NotEmpty
            or ExtractionConditionOperator.ToLower
            or ExtractionConditionOperator.ToUpper)
        {
            condition = new ChainExtractionCondition
            {
                IsEnabled = isEnabled,
                Operator = op
            };
            return true;
        }

        if (op == ExtractionConditionOperator.Trim)
        {
            var trimChars = idx < cleaned.Count ? cleaned[idx++].Text : string.Empty;
            condition = new ChainExtractionCondition
            {
                IsEnabled = isEnabled,
                Operator = op,
                Value = trimChars
            };
            return true;
        }

        if (op == ExtractionConditionOperator.SetValue)
        {
            var newVal = idx < cleaned.Count ? cleaned[idx++].Text : string.Empty;
            condition = new ChainExtractionCondition
            {
                IsEnabled = isEnabled,
                Operator = op,
                Value = newVal
            };
            return true;
        }

        if (idx >= cleaned.Count)
        {
            error = "valeur ou motif manquant après l'opérateur.";
            return false;
        }

        var value = cleaned[idx++].Text;
        var replacement = string.Empty;
        var regexGroup = 1;
        var segmentIndex = 0;

        if (op is ExtractionConditionOperator.RegexReplace
            or ExtractionConditionOperator.ReplaceText
            or ExtractionConditionOperator.Between)
        {
            if (idx < cleaned.Count && !cleaned[idx].Quoted && cleaned[idx].Text == "->")
            {
                idx++;
            }

            if (idx < cleaned.Count)
            {
                replacement = cleaned[idx++].Text;
            }
        }
        else if (op is ExtractionConditionOperator.RegexExtract or ExtractionConditionOperator.RegexExtractAll
                 && idx < cleaned.Count)
        {
            if (!TryParseGroupToken(cleaned[idx].Text, out regexGroup, out error))
            {
                return false;
            }
        }
        else if (op == ExtractionConditionOperator.SplitPart && idx < cleaned.Count)
        {
            if (!TryParseIndexToken(cleaned[idx].Text, out segmentIndex, out error))
            {
                return false;
            }
        }

        condition = new ChainExtractionCondition
        {
            IsEnabled = isEnabled,
            Operator = op,
            Value = value,
            Replacement = replacement,
            RegexGroup = regexGroup,
            SegmentIndex = segmentIndex,
            IgnoreCase = ignoreCase
        };
        return true;
    }

    private static bool TryParseBranchTokens(
        List<(string Text, bool Quoted)> tokens,
        out ChainConditionalBranch? branch,
        out string? error)
    {
        branch = null;
        error = null;

        var ignoreCase = ExtractCaseFlags(tokens, out var cleaned);
        if (cleaned.Count == 0)
        {
            error = "action manquante.";
            return false;
        }

        var keyword = cleaned[0].Text.ToLowerInvariant();
        if (keyword is "garder" or "keep")
        {
            branch = new ChainConditionalBranch { Action = ExtractionConditionalAction.Keep };
            return true;
        }

        if (keyword is "exclure" or "exclude" or "supprimer" or "drop")
        {
            branch = new ChainConditionalBranch { Action = ExtractionConditionalAction.Exclude };
            return true;
        }

        var idx = 0;
        if (!TryMatchOperator(cleaned, ref idx, out var op, out var opErr) ||
            !ExtractionBranchExecutor.TryMapTransformOperator(op, out var action))
        {
            error = opErr ?? $"action inconnue « {cleaned[0].Text} ».";
            return false;
        }

        if (action is ExtractionConditionalAction.ToLower or ExtractionConditionalAction.ToUpper)
        {
            branch = new ChainConditionalBranch { Action = action };
            return true;
        }

        if (action is ExtractionConditionalAction.Trim or ExtractionConditionalAction.SetValue)
        {
            var val = idx < cleaned.Count ? cleaned[idx++].Text : string.Empty;
            branch = new ChainConditionalBranch { Action = action, Value = val };
            return true;
        }

        if (idx >= cleaned.Count)
        {
            error = "valeur manquante après l'action.";
            return false;
        }

        var value = cleaned[idx++].Text;
        var replacement = string.Empty;
        var regexGroup = 1;
        var segmentIndex = 0;

        if (action is ExtractionConditionalAction.RegexReplace
            or ExtractionConditionalAction.ReplaceText
            or ExtractionConditionalAction.Between)
        {
            if (idx < cleaned.Count && !cleaned[idx].Quoted && cleaned[idx].Text == "->")
            {
                idx++;
            }

            if (idx < cleaned.Count)
            {
                replacement = cleaned[idx++].Text;
            }
        }
        else if (action is ExtractionConditionalAction.RegexExtract or ExtractionConditionalAction.RegexExtractAll
                 && idx < cleaned.Count)
        {
            if (!TryParseGroupToken(cleaned[idx].Text, out regexGroup, out error))
            {
                return false;
            }
        }
        else if (action == ExtractionConditionalAction.SplitPart && idx < cleaned.Count)
        {
            if (!TryParseIndexToken(cleaned[idx].Text, out segmentIndex, out error))
            {
                return false;
            }
        }

        branch = new ChainConditionalBranch
        {
            Action = action,
            Value = value,
            Replacement = replacement,
            RegexGroup = regexGroup,
            SegmentIndex = segmentIndex,
            IgnoreCase = ignoreCase
        };
        return true;
    }

    private static bool ExtractCaseFlags(
        List<(string Text, bool Quoted)> tokens,
        out List<(string Text, bool Quoted)> cleaned)
    {
        var ignoreCase = false;
        cleaned = new List<(string Text, bool Quoted)>(tokens.Count);
        foreach (var tok in tokens)
        {
            if (!tok.Quoted && (tok.Text.Equals("[icase]", StringComparison.OrdinalIgnoreCase) ||
                                tok.Text.Equals("[i]", StringComparison.OrdinalIgnoreCase)))
            {
                ignoreCase = true;
            }
            else if (!tok.Quoted && tok.Text.Equals("[casse]", StringComparison.OrdinalIgnoreCase))
            {
                ignoreCase = false;
            }
            else
            {
                cleaned.Add(tok);
            }
        }

        return ignoreCase;
    }

    private static bool TryParseGroupToken(string raw, out int regexGroup, out string? error)
    {
        error = null;
        var grpTok = raw;
        if (grpTok.StartsWith("groupe=", StringComparison.OrdinalIgnoreCase))
        {
            grpTok = grpTok[7..];
        }
        else if (grpTok.StartsWith("group=", StringComparison.OrdinalIgnoreCase))
        {
            grpTok = grpTok[6..];
        }

        if (!int.TryParse(grpTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out regexGroup) || regexGroup < 0)
        {
            error = $"index de groupe invalide « {raw} ».";
            return false;
        }

        return true;
    }

    private static bool TryParseIndexToken(string raw, out int segmentIndex, out string? error)
    {
        error = null;
        var idxTok = raw;
        if (idxTok.StartsWith("index=", StringComparison.OrdinalIgnoreCase))
        {
            idxTok = idxTok[6..];
        }
        else if (idxTok.StartsWith("part=", StringComparison.OrdinalIgnoreCase))
        {
            idxTok = idxTok[5..];
        }

        if (!int.TryParse(idxTok, NumberStyles.Integer, CultureInfo.InvariantCulture, out segmentIndex))
        {
            error = $"index de découpage invalide « {raw} ».";
            return false;
        }

        segmentIndex = Math.Clamp(segmentIndex, -99, 99);
        return true;
    }

    private static bool TryMatchOperator(
        List<(string Text, bool Quoted)> tokens,
        ref int idx,
        out ExtractionConditionOperator op,
        out string? error)
    {
        op = ExtractionConditionOperator.Contains;
        error = null;
        var first = tokens[idx].Text.ToLowerInvariant();

        if (first is "longueur" or "len" or "length")
        {
            if (idx + 1 >= tokens.Count)
            {
                error = "opérateur ==, >= ou <= attendu après « longueur ».";
                return false;
            }

            var cmp = tokens[idx + 1].Text;
            if (cmp is "==" or "=" or "egal")
            {
                op = ExtractionConditionOperator.LengthEquals;
                idx += 2;
                return true;
            }
            if (cmp is ">=" or "min")
            {
                op = ExtractionConditionOperator.LengthMin;
                idx += 2;
                return true;
            }
            if (cmp is "<=" or "max")
            {
                op = ExtractionConditionOperator.LengthMax;
                idx += 2;
                return true;
            }

            error = $"comparateur inconnu « {cmp} » pour « longueur » (==, >= ou <=).";
            return false;
        }

        if (first is "nombre" or "num" or "number")
        {
            if (idx + 1 >= tokens.Count)
            {
                error = "opérateur ==, !=, >, >=, < ou <= attendu après « nombre ».";
                return false;
            }

            var cmp = tokens[idx + 1].Text;
            if (cmp is "==" or "=") { op = ExtractionConditionOperator.NumberEquals; idx += 2; return true; }
            if (cmp == "!=") { op = ExtractionConditionOperator.NumberNotEquals; idx += 2; return true; }
            if (cmp == ">") { op = ExtractionConditionOperator.GreaterThan; idx += 2; return true; }
            if (cmp == ">=") { op = ExtractionConditionOperator.GreaterOrEqual; idx += 2; return true; }
            if (cmp == "<") { op = ExtractionConditionOperator.LessThan; idx += 2; return true; }
            if (cmp == "<=") { op = ExtractionConditionOperator.LessOrEqual; idx += 2; return true; }

            error = $"comparateur inconnu « {cmp} » pour « nombre » (==, !=, >, >=, < ou <=).";
            return false;
        }

        idx++;
        switch (first)
        {
            case "contient" or "contains": op = ExtractionConditionOperator.Contains; return true;
            case "!contient" or "ne_contient_pas" or "not_contains" or "!contains": op = ExtractionConditionOperator.NotContains; return true;
            case "commence" or "commence_par" or "starts_with": op = ExtractionConditionOperator.StartsWith; return true;
            case "!commence" or "ne_commence_pas" or "not_starts_with": op = ExtractionConditionOperator.NotStartsWith; return true;
            case "finit" or "finit_par" or "ends_with": op = ExtractionConditionOperator.EndsWith; return true;
            case "!finit" or "ne_finit_pas" or "not_ends_with": op = ExtractionConditionOperator.NotEndsWith; return true;
            case "egal" or "equals" or "==": op = ExtractionConditionOperator.Equals; return true;
            case "different" or "not_equals" or "!=": op = ExtractionConditionOperator.NotEquals; return true;
            case "dans_liste" or "in_list" or "dans": op = ExtractionConditionOperator.InList; return true;
            case "!dans_liste" or "pas_dans_liste" or "not_in_list" or "hors_liste": op = ExtractionConditionOperator.NotInList; return true;
            case "regex" or "regex_correspond" or "regex_match" or "=~": op = ExtractionConditionOperator.RegexMatch; return true;
            case "!regex" or "regex_exclut" or "regex_not_match" or "!~": op = ExtractionConditionOperator.RegexNotMatch; return true;
            case "regex_extraire" or "extract" or "regex_extract": op = ExtractionConditionOperator.RegexExtract; return true;
            case "regex_extraire_tout" or "extract_all" or "regex_extract_all": op = ExtractionConditionOperator.RegexExtractAll; return true;
            case "regex_remplacer" or "regex_replace": op = ExtractionConditionOperator.RegexReplace; return true;
            case "remplacer" or "replace_text" or "replace": op = ExtractionConditionOperator.ReplaceText; return true;
            case "prefixer" or "prepend": op = ExtractionConditionOperator.PrependText; return true;
            case "suffixer" or "append": op = ExtractionConditionOperator.AppendText; return true;
            case "definir" or "set" or "set_value" or "valeur": op = ExtractionConditionOperator.SetValue; return true;
            case "entre" or "between": op = ExtractionConditionOperator.Between; return true;
            case "decouper" or "split" or "split_part": op = ExtractionConditionOperator.SplitPart; return true;
            case "nettoyer" or "trim": op = ExtractionConditionOperator.Trim; return true;
            case "minuscules" or "lower" or "to_lower": op = ExtractionConditionOperator.ToLower; return true;
            case "majuscules" or "upper" or "to_upper": op = ExtractionConditionOperator.ToUpper; return true;
            case "est_vide" or "vide" or "is_empty": op = ExtractionConditionOperator.IsEmpty; return true;
            case "non_vide" or "not_empty": op = ExtractionConditionOperator.NotEmpty; return true;
            case "longueur==": op = ExtractionConditionOperator.LengthEquals; return true;
            case "longueur>=" or "min_length": op = ExtractionConditionOperator.LengthMin; return true;
            case "longueur<=" or "max_length": op = ExtractionConditionOperator.LengthMax; return true;
            case "nombre==": op = ExtractionConditionOperator.NumberEquals; return true;
            case "nombre!=": op = ExtractionConditionOperator.NumberNotEquals; return true;
            case "nombre>=" or ">=": op = ExtractionConditionOperator.GreaterOrEqual; return true;
            case "nombre<=" or "<=": op = ExtractionConditionOperator.LessOrEqual; return true;
            case ">": op = ExtractionConditionOperator.GreaterThan; return true;
            case "<": op = ExtractionConditionOperator.LessThan; return true;
            default:
                error = $"opérateur inconnu « {tokens[0].Text} ».";
                return false;
        }
    }

    private static string FormatRuleLine(ChainExtractionCondition cond)
    {
        var sb = new StringBuilder();
        if (cond.IsConditional)
        {
            sb.Append("si ");
            AppendOperatorAndArgs(sb, cond.Operator, cond.Value, cond.Replacement, cond.RegexGroup, cond.SegmentIndex, cond.IgnoreCase && cond.SupportsIgnoreCase);
            sb.Append(" alors ");
            AppendBranch(sb, cond.ThenBranch);
            if (cond.ElseBranch.Action != ExtractionConditionalAction.Keep)
            {
                sb.Append(" sinon ");
                AppendBranch(sb, cond.ElseBranch);
            }
        }
        else
        {
            AppendOperatorAndArgs(sb, cond.Operator, cond.Value, cond.Replacement, cond.RegexGroup, cond.SegmentIndex, cond.IgnoreCase && cond.SupportsIgnoreCase);
        }

        if (!cond.IsEnabled)
        {
            sb.Append(" [inactif]");
        }

        return sb.ToString();
    }

    private static void AppendBranch(StringBuilder sb, ChainConditionalBranch branch)
    {
        if (branch.Action == ExtractionConditionalAction.Keep)
        {
            sb.Append("garder");
            return;
        }

        if (branch.Action == ExtractionConditionalAction.Exclude)
        {
            sb.Append("exclure");
            return;
        }

        var mappedOp = branch.Action switch
        {
            ExtractionConditionalAction.SetValue => ExtractionConditionOperator.SetValue,
            ExtractionConditionalAction.PrependText => ExtractionConditionOperator.PrependText,
            ExtractionConditionalAction.AppendText => ExtractionConditionOperator.AppendText,
            ExtractionConditionalAction.ReplaceText => ExtractionConditionOperator.ReplaceText,
            ExtractionConditionalAction.RegexExtract => ExtractionConditionOperator.RegexExtract,
            ExtractionConditionalAction.RegexExtractAll => ExtractionConditionOperator.RegexExtractAll,
            ExtractionConditionalAction.RegexReplace => ExtractionConditionOperator.RegexReplace,
            ExtractionConditionalAction.Between => ExtractionConditionOperator.Between,
            ExtractionConditionalAction.SplitPart => ExtractionConditionOperator.SplitPart,
            ExtractionConditionalAction.Trim => ExtractionConditionOperator.Trim,
            ExtractionConditionalAction.ToLower => ExtractionConditionOperator.ToLower,
            ExtractionConditionalAction.ToUpper => ExtractionConditionOperator.ToUpper,
            _ => ExtractionConditionOperator.SetValue
        };

        AppendOperatorAndArgs(
            sb,
            mappedOp,
            branch.Value,
            branch.Replacement,
            branch.RegexGroup,
            branch.SegmentIndex,
            branch.IgnoreCase && branch.SupportsIgnoreCase);
    }

    private static void AppendOperatorAndArgs(
        StringBuilder sb,
        ExtractionConditionOperator op,
        string? value,
        string? replacement,
        int regexGroup,
        int segmentIndex,
        bool appendIgnoreCase)
    {
        var opWord = op switch
        {
            ExtractionConditionOperator.Contains => "contient",
            ExtractionConditionOperator.NotContains => "!contient",
            ExtractionConditionOperator.StartsWith => "commence",
            ExtractionConditionOperator.NotStartsWith => "!commence",
            ExtractionConditionOperator.EndsWith => "finit",
            ExtractionConditionOperator.NotEndsWith => "!finit",
            ExtractionConditionOperator.Equals => "egal",
            ExtractionConditionOperator.NotEquals => "!=",
            ExtractionConditionOperator.InList => "dans_liste",
            ExtractionConditionOperator.NotInList => "!dans_liste",
            ExtractionConditionOperator.RegexMatch => "regex",
            ExtractionConditionOperator.RegexNotMatch => "!regex",
            ExtractionConditionOperator.RegexExtract => "regex_extraire",
            ExtractionConditionOperator.RegexExtractAll => "regex_extraire_tout",
            ExtractionConditionOperator.RegexReplace => "regex_remplacer",
            ExtractionConditionOperator.ReplaceText => "remplacer",
            ExtractionConditionOperator.PrependText => "prefixer",
            ExtractionConditionOperator.AppendText => "suffixer",
            ExtractionConditionOperator.SetValue => "definir",
            ExtractionConditionOperator.Between => "entre",
            ExtractionConditionOperator.SplitPart => "decouper",
            ExtractionConditionOperator.Trim => "nettoyer",
            ExtractionConditionOperator.ToLower => "minuscules",
            ExtractionConditionOperator.ToUpper => "majuscules",
            ExtractionConditionOperator.IsEmpty => "est_vide",
            ExtractionConditionOperator.NotEmpty => "non_vide",
            ExtractionConditionOperator.LengthEquals => "longueur ==",
            ExtractionConditionOperator.LengthMin => "longueur >=",
            ExtractionConditionOperator.LengthMax => "longueur <=",
            ExtractionConditionOperator.NumberEquals => "nombre ==",
            ExtractionConditionOperator.NumberNotEquals => "nombre !=",
            ExtractionConditionOperator.GreaterThan => "nombre >",
            ExtractionConditionOperator.GreaterOrEqual => "nombre >=",
            ExtractionConditionOperator.LessThan => "nombre <",
            ExtractionConditionOperator.LessOrEqual => "nombre <=",
            _ => "contient"
        };

        sb.Append(opWord);
        if (op is not (
            ExtractionConditionOperator.IsEmpty
            or ExtractionConditionOperator.NotEmpty
            or ExtractionConditionOperator.ToLower
            or ExtractionConditionOperator.ToUpper))
        {
            if (op != ExtractionConditionOperator.Trim || !string.IsNullOrEmpty(value))
            {
                sb.Append(' ');
                if (op is ExtractionConditionOperator.LengthEquals
                    or ExtractionConditionOperator.LengthMin
                    or ExtractionConditionOperator.LengthMax
                    or ExtractionConditionOperator.NumberEquals
                    or ExtractionConditionOperator.NumberNotEquals
                    or ExtractionConditionOperator.GreaterThan
                    or ExtractionConditionOperator.GreaterOrEqual
                    or ExtractionConditionOperator.LessThan
                    or ExtractionConditionOperator.LessOrEqual)
                {
                    sb.Append((value ?? string.Empty).Trim());
                }
                else
                {
                    sb.Append(Quote(value));
                }
            }
        }

        if (op is ExtractionConditionOperator.RegexReplace
            or ExtractionConditionOperator.ReplaceText
            or ExtractionConditionOperator.Between)
        {
            sb.Append(" -> ");
            sb.Append(Quote(replacement));
        }
        else if (op is ExtractionConditionOperator.RegexExtract or ExtractionConditionOperator.RegexExtractAll
                 && regexGroup != 1)
        {
            sb.Append(" groupe=");
            sb.Append(regexGroup.ToString(CultureInfo.InvariantCulture));
        }
        else if (op == ExtractionConditionOperator.SplitPart)
        {
            sb.Append(" index=");
            sb.Append(segmentIndex.ToString(CultureInfo.InvariantCulture));
        }

        if (appendIgnoreCase)
        {
            sb.Append(" [icase]");
        }
    }

    private static string Quote(string? value)
    {
        var text = value ?? string.Empty;
        var sb = new StringBuilder(text.Length + 4);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static List<(string Text, bool Quoted)> TokenizeLine(string line, out string? error)
    {
        var tokens = new List<(string Text, bool Quoted)>();
        error = null;
        var i = 0;

        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            if (line[i] == '"' || line[i] == '\'')
            {
                var quote = line[i++];
                var sb = new StringBuilder();
                var closed = false;
                while (i < line.Length)
                {
                    var ch = line[i++];
                    if (ch == '\\' && i < line.Length)
                    {
                        var esc = line[i++];
                        sb.Append(esc switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '\\' => '\\',
                            '"' => '"',
                            '\'' => '\'',
                            _ => esc
                        });
                    }
                    else if (ch == quote)
                    {
                        closed = true;
                        break;
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }

                if (!closed)
                {
                    error = "chaîne entre guillemets non fermée.";
                    return tokens;
                }

                tokens.Add((sb.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                tokens.Add((line[start..i], false));
            }
        }

        return tokens;
    }
}
