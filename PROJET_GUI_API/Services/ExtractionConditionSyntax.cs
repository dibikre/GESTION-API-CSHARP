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
            sb.AppendLine("# Une condition par ligne (# ou // pour commenter) :");
            sb.AppendLine("# regex_extraire \"([0-9a-fA-F]{16,})\"");
            sb.AppendLine("# regex_extraire_tout \"([0-9a-fA-F]{16,})\"");
            sb.AppendLine("# decouper \"/\" index=-1");
            sb.AppendLine("# entre \"/avatars/\" -> \"-full\"");
            sb.AppendLine("# remplacer \"-full\" -> \"\"");
            sb.AppendLine("# contient \"master.m3u8\" [icase]");
            sb.AppendLine("# nettoyer");
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

        var tokens = TokenizeLine(line, out var tokError);
        if (tokError is not null)
        {
            error = tokError;
            return false;
        }

        if (tokens.Count == 0)
        {
            error = "ligne vide.";
            return false;
        }

        var isEnabled = true;
        var ignoreCase = false;
        var cleaned = new List<(string Text, bool Quoted)>(tokens.Count);
        foreach (var tok in tokens)
        {
            if (!tok.Quoted && tok.Text.Equals("[inactif]", StringComparison.OrdinalIgnoreCase))
            {
                isEnabled = false;
            }
            else if (!tok.Quoted && tok.Text.Equals("[off]", StringComparison.OrdinalIgnoreCase))
            {
                isEnabled = false;
            }
            else if (!tok.Quoted && (tok.Text.Equals("[icase]", StringComparison.OrdinalIgnoreCase) ||
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

        if (op is ExtractionConditionOperator.NotEmpty
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
            var grpTok = cleaned[idx].Text;
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
                error = $"index de groupe invalide « {cleaned[idx].Text} ».";
                return false;
            }
        }
        else if (op == ExtractionConditionOperator.SplitPart && idx < cleaned.Count)
        {
            var idxTok = cleaned[idx].Text;
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
                error = $"index de découpage invalide « {cleaned[idx].Text} ».";
                return false;
            }

            segmentIndex = Math.Clamp(segmentIndex, -99, 99);
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
                error = "opérateur >= ou <= attendu après « longueur ».";
                return false;
            }

            var cmp = tokens[idx + 1].Text;
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

            error = $"comparateur inconnu « {cmp} » pour « longueur » (>= ou <=).";
            return false;
        }

        if (first is "nombre" or "num" or "number")
        {
            if (idx + 1 >= tokens.Count)
            {
                error = "opérateur > ou < attendu après « nombre ».";
                return false;
            }

            var cmp = tokens[idx + 1].Text;
            if (cmp == ">")
            {
                op = ExtractionConditionOperator.GreaterThan;
                idx += 2;
                return true;
            }
            if (cmp == "<")
            {
                op = ExtractionConditionOperator.LessThan;
                idx += 2;
                return true;
            }

            error = $"comparateur inconnu « {cmp} » pour « nombre » (> ou <).";
            return false;
        }

        idx++;
        switch (first)
        {
            case "contient" or "contains": op = ExtractionConditionOperator.Contains; return true;
            case "!contient" or "ne_contient_pas" or "not_contains" or "!contains": op = ExtractionConditionOperator.NotContains; return true;
            case "commence" or "commence_par" or "starts_with": op = ExtractionConditionOperator.StartsWith; return true;
            case "finit" or "finit_par" or "ends_with": op = ExtractionConditionOperator.EndsWith; return true;
            case "egal" or "equals" or "==": op = ExtractionConditionOperator.Equals; return true;
            case "different" or "not_equals" or "!=": op = ExtractionConditionOperator.NotEquals; return true;
            case "regex" or "regex_correspond" or "regex_match" or "=~": op = ExtractionConditionOperator.RegexMatch; return true;
            case "!regex" or "regex_exclut" or "regex_not_match" or "!~": op = ExtractionConditionOperator.RegexNotMatch; return true;
            case "regex_extraire" or "extract" or "regex_extract": op = ExtractionConditionOperator.RegexExtract; return true;
            case "regex_extraire_tout" or "extract_all" or "regex_extract_all": op = ExtractionConditionOperator.RegexExtractAll; return true;
            case "regex_remplacer" or "regex_replace": op = ExtractionConditionOperator.RegexReplace; return true;
            case "remplacer" or "replace_text" or "replace": op = ExtractionConditionOperator.ReplaceText; return true;
            case "entre" or "between": op = ExtractionConditionOperator.Between; return true;
            case "decouper" or "split" or "split_part": op = ExtractionConditionOperator.SplitPart; return true;
            case "nettoyer" or "trim": op = ExtractionConditionOperator.Trim; return true;
            case "minuscules" or "lower" or "to_lower": op = ExtractionConditionOperator.ToLower; return true;
            case "majuscules" or "upper" or "to_upper": op = ExtractionConditionOperator.ToUpper; return true;
            case "non_vide" or "not_empty": op = ExtractionConditionOperator.NotEmpty; return true;
            case "longueur>=" or "min_length": op = ExtractionConditionOperator.LengthMin; return true;
            case "longueur<=" or "max_length": op = ExtractionConditionOperator.LengthMax; return true;
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
        var opWord = cond.Operator switch
        {
            ExtractionConditionOperator.Contains => "contient",
            ExtractionConditionOperator.NotContains => "!contient",
            ExtractionConditionOperator.StartsWith => "commence",
            ExtractionConditionOperator.EndsWith => "finit",
            ExtractionConditionOperator.Equals => "egal",
            ExtractionConditionOperator.NotEquals => "!=",
            ExtractionConditionOperator.RegexMatch => "regex",
            ExtractionConditionOperator.RegexNotMatch => "!regex",
            ExtractionConditionOperator.RegexExtract => "regex_extraire",
            ExtractionConditionOperator.RegexExtractAll => "regex_extraire_tout",
            ExtractionConditionOperator.RegexReplace => "regex_remplacer",
            ExtractionConditionOperator.ReplaceText => "remplacer",
            ExtractionConditionOperator.Between => "entre",
            ExtractionConditionOperator.SplitPart => "decouper",
            ExtractionConditionOperator.Trim => "nettoyer",
            ExtractionConditionOperator.ToLower => "minuscules",
            ExtractionConditionOperator.ToUpper => "majuscules",
            ExtractionConditionOperator.NotEmpty => "non_vide",
            ExtractionConditionOperator.LengthMin => "longueur >=",
            ExtractionConditionOperator.LengthMax => "longueur <=",
            ExtractionConditionOperator.GreaterThan => "nombre >",
            ExtractionConditionOperator.LessThan => "nombre <",
            _ => "contient"
        };

        sb.Append(opWord);
        if (cond.Operator is not (
            ExtractionConditionOperator.NotEmpty
            or ExtractionConditionOperator.ToLower
            or ExtractionConditionOperator.ToUpper))
        {
            if (cond.Operator != ExtractionConditionOperator.Trim || !string.IsNullOrEmpty(cond.Value))
            {
                sb.Append(' ');
                if (cond.Operator is ExtractionConditionOperator.LengthMin
                    or ExtractionConditionOperator.LengthMax
                    or ExtractionConditionOperator.GreaterThan
                    or ExtractionConditionOperator.LessThan)
                {
                    sb.Append(cond.Value.Trim());
                }
                else
                {
                    sb.Append(Quote(cond.Value));
                }
            }
        }

        if (cond.Operator is ExtractionConditionOperator.RegexReplace
            or ExtractionConditionOperator.ReplaceText
            or ExtractionConditionOperator.Between)
        {
            sb.Append(" -> ");
            sb.Append(Quote(cond.Replacement));
        }
        else if (cond.Operator is ExtractionConditionOperator.RegexExtract or ExtractionConditionOperator.RegexExtractAll
                 && cond.RegexGroup != 1)
        {
            sb.Append(" groupe=");
            sb.Append(cond.RegexGroup.ToString(CultureInfo.InvariantCulture));
        }
        else if (cond.Operator == ExtractionConditionOperator.SplitPart)
        {
            sb.Append(" index=");
            sb.Append(cond.SegmentIndex.ToString(CultureInfo.InvariantCulture));
        }

        if (cond.IgnoreCase && cond.SupportsIgnoreCase)
        {
            sb.Append(" [icase]");
        }

        if (!cond.IsEnabled)
        {
            sb.Append(" [inactif]");
        }

        return sb.ToString();
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
