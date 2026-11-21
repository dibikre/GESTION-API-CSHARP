using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApiTester.Services;

public sealed class ReturnExpressionService : IReturnExpressionService
{
    private static readonly Regex ReturnKeywordPrefixRegex = new(
        @"^\s*return(?:\s+|(?=[{\[\(\""'])|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareIdentifierRegex = new(
        @"^\$?[\p{L}_][\p{L}\p{N}_]*$",
        RegexOptions.Compiled);

    private static readonly Regex ExactScalarMarkerRegex = new(
        @"^\{\{\s*(\$?[\p{L}_][\p{L}\p{N}_]*)\s*\}\}$",
        RegexOptions.Compiled);

    private static readonly Regex ExactListMarkerRegex = new(
        @"^\[\[\s*([\p{L}_][\p{L}\p{N}_]*)\s*\]\]$",
        RegexOptions.Compiled);

    private static readonly Regex AnchoredScalarMarkerRegex = new(
        @"\G\{\{\s*(\$?[\p{L}_][\p{L}\p{N}_]*)\s*\}\}",
        RegexOptions.Compiled);

    private static readonly Regex AnchoredListMarkerRegex = new(
        @"\G\[\[\s*([\p{L}_][\p{L}\p{N}_]*)\s*\]\]",
        RegexOptions.Compiled);

    private static readonly Regex AnchoredIdentifierRegex = new(
        @"\G\$?[\p{L}_][\p{L}\p{N}_]*",
        RegexOptions.Compiled);

    private static readonly Regex AnyMarkerRegex = new(
        @"(?:\{\{\s*(\$?[\p{L}_][\p{L}\p{N}_]*)\s*\}\}|\[\[\s*([\p{L}_][\p{L}\p{N}_]*)\s*\]\])",
        RegexOptions.Compiled);

    private static readonly Regex JsonNumberRegex = new(
        @"^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$",
        RegexOptions.Compiled);

    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        MaxDepth = 1024
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 1024,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool IsReturnStatement(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ReturnKeywordPrefixRegex.IsMatch(text);

    public bool TryParse(string? rawExpression, out ReturnAstNode? ast, out string? errorMessage)
    {
        ast = null;
        var expr = StripReturnPrefix(rawExpression);
        if (string.IsNullOrWhiteSpace(expr))
        {
            errorMessage = "Saisis une expression de retour (ex. return nom_variable ou return { a, b }).";
            return false;
        }

        if (TrySplitTopLevelCommas(expr, out var commaItems))
        {
            var elements = new List<ReturnAstNode>(commaItems.Count);
            foreach (var item in commaItems)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    errorMessage = "Élément vide dans la liste de retour séparée par des virgules.";
                    return false;
                }

                if (!TryParseSingleExpression(item, isTopLevel: false, out var elemNode, out errorMessage))
                {
                    return false;
                }

                elements.Add(elemNode!);
            }

            ast = new ReturnAstNode(ReturnAstKind.Array, Elements: elements);
            errorMessage = null;
            return true;
        }

        return TryParseSingleExpression(expr, isTopLevel: true, out ast, out errorMessage);
    }

    public IReadOnlyList<ReturnVariableRef> CollectVariableReferences(ReturnAstNode ast)
    {
        ArgumentNullException.ThrowIfNull(ast);

        var refs = new List<ReturnVariableRef>();
        var seen = new HashSet<(string Name, ReturnRefRequirement Req)>();

        void AddRef(string name, ReturnRefRequirement req)
        {
            if (seen.Add((name, req)))
            {
                refs.Add(new ReturnVariableRef(name, req));
            }
        }

        void Walk(ReturnAstNode node)
        {
            switch (node.Kind)
            {
                case ReturnAstKind.ScalarVariable when !string.IsNullOrWhiteSpace(node.TextValue):
                    AddRef(node.TextValue, ReturnRefRequirement.ScalarOnly);
                    break;

                case ReturnAstKind.ListVariable when !string.IsNullOrWhiteSpace(node.TextValue):
                    AddRef(node.TextValue, ReturnRefRequirement.ListOnly);
                    break;

                case ReturnAstKind.AutoVariable when !string.IsNullOrWhiteSpace(node.TextValue):
                    AddRef(node.TextValue, ReturnRefRequirement.ScalarOrList);
                    break;

                case ReturnAstKind.TemplateString when !string.IsNullOrEmpty(node.TextValue):
                    foreach (Match match in AnyMarkerRegex.Matches(node.TextValue))
                    {
                        if (match.Groups[2].Success)
                        {
                            AddRef(match.Groups[2].Value, ReturnRefRequirement.ListOnly);
                        }
                        else if (match.Groups[1].Success)
                        {
                            AddRef(match.Groups[1].Value, ReturnRefRequirement.ScalarOnly);
                        }
                    }
                    break;

                case ReturnAstKind.Array when node.Elements is not null:
                    foreach (var elem in node.Elements)
                    {
                        Walk(elem);
                    }
                    break;

                case ReturnAstKind.Object when node.Properties is not null:
                    foreach (var prop in node.Properties)
                    {
                        Walk(prop.Value);
                    }
                    break;
            }
        }

        Walk(ast);
        return refs;
    }

    public IReadOnlyList<string> ValidateReferences(
        ReturnAstNode ast,
        IReadOnlySet<string> availableScalars,
        IReadOnlySet<string> availableLists)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(availableScalars);
        ArgumentNullException.ThrowIfNull(availableLists);

        var errors = new List<string>();
        foreach (var variableRef in CollectVariableReferences(ast))
        {
            var name = variableRef.Name;
            if (name.StartsWith('$'))
            {
                if (name is not ("$index" or "$guid" or "$timestamp"))
                {
                    errors.Add($"{{{{{name}}}}} : variable prédéfinie inconnue ($index, $guid, $timestamp).");
                    continue;
                }

                if (variableRef.Requirement == ReturnRefRequirement.ListOnly)
                {
                    errors.Add($"[[{name}]] : variable prédéfinie utilisable uniquement comme valeur simple {{{{{name}}}}}.");
                }

                continue;
            }

            switch (variableRef.Requirement)
            {
                case ReturnRefRequirement.ListOnly:
                    if (!availableLists.Contains(name))
                    {
                        errors.Add(availableScalars.Contains(name)
                            ? $"[[{name}]] : correspond à une variable simple ; coche « En liste [[...]] » ou utilise {{{{{name}}}}}."
                            : $"[[{name}]] : aucune étape précédente n'extrait cette liste.");
                    }
                    break;

                case ReturnRefRequirement.ScalarOnly:
                    if (!availableScalars.Contains(name))
                    {
                        errors.Add(availableLists.Contains(name)
                            ? $"{{{{{name}}}}} : extraite en liste ; utilise [[{name}]] pour la retourner sous forme de liste."
                            : $"{{{{{name}}}}} : aucune étape précédente n'extrait cette variable.");
                    }
                    break;

                case ReturnRefRequirement.ScalarOrList:
                    if (!availableScalars.Contains(name) && !availableLists.Contains(name))
                    {
                        errors.Add($"« {name} » : aucune étape précédente n'extrait cette variable ou liste.");
                    }
                    break;
            }
        }

        return errors;
    }

    public ReturnEvaluationResult Evaluate(
        ReturnAstNode ast,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lists,
        int globalIndex,
        string? guid = null,
        string? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(lists);

        var reqGuid = string.IsNullOrEmpty(guid) ? Guid.NewGuid().ToString("D") : guid;
        var reqTs = string.IsNullOrEmpty(timestamp)
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            : timestamp;

        switch (ast.Kind)
        {
            case ReturnAstKind.ScalarVariable:
            {
                if (!TryResolveScalar(ast.TextValue ?? string.Empty, variables, globalIndex, reqGuid, reqTs, out var scalarVal, out var err))
                {
                    return new ReturnEvaluationResult(false, string.Empty, false, err);
                }

                return FormatTopLevelScalar(scalarVal);
            }

            case ReturnAstKind.ListVariable:
            {
                var name = ast.TextValue ?? string.Empty;
                if (!lists.TryGetValue(name, out var listValues))
                {
                    return new ReturnEvaluationResult(false, string.Empty, false, $"Liste inconnue ou non définie : [[{name}]].");
                }

                var jsonArr = BuildJsonArray(listValues);
                return new ReturnEvaluationResult(true, jsonArr.ToJsonString(IndentedJsonOptions), IsJson: true);
            }

            case ReturnAstKind.AutoVariable:
            {
                var name = ast.TextValue ?? string.Empty;
                if (TryResolveScalar(name, variables, globalIndex, reqGuid, reqTs, out var scalarVal, out _))
                {
                    return FormatTopLevelScalar(scalarVal);
                }

                if (lists.TryGetValue(name, out var listValues))
                {
                    var jsonArr = BuildJsonArray(listValues);
                    return new ReturnEvaluationResult(true, jsonArr.ToJsonString(IndentedJsonOptions), IsJson: true);
                }

                return new ReturnEvaluationResult(false, string.Empty, false, $"Variable inconnue ou non définie : {name}.");
            }

            case ReturnAstKind.LiteralString:
                return new ReturnEvaluationResult(true, ast.TextValue ?? string.Empty, IsJson: false);

            case ReturnAstKind.TemplateString:
            {
                if (!TrySubstituteTemplateString(ast.TextValue ?? string.Empty, variables, lists, globalIndex, reqGuid, reqTs, out var text, out var err))
                {
                    return new ReturnEvaluationResult(false, string.Empty, false, err);
                }

                return new ReturnEvaluationResult(true, text, IsJson: false);
            }

            case ReturnAstKind.LiteralNumber:
                return new ReturnEvaluationResult(true, ast.TextValue ?? "0", IsJson: true);

            case ReturnAstKind.LiteralBool:
                return new ReturnEvaluationResult(true, (ast.BoolValue ?? false) ? "true" : "false", IsJson: true);

            case ReturnAstKind.LiteralNull:
                return new ReturnEvaluationResult(true, "null", IsJson: true);

            case ReturnAstKind.Object:
            case ReturnAstKind.Array:
            {
                if (!TryBuildJsonNode(ast, variables, lists, globalIndex, reqGuid, reqTs, out var jsonNode, out var err))
                {
                    return new ReturnEvaluationResult(false, string.Empty, false, err);
                }

                var jsonText = jsonNode is null
                    ? "null"
                    : jsonNode.ToJsonString(IndentedJsonOptions);
                return new ReturnEvaluationResult(true, jsonText, IsJson: true);
            }

            default:
                return new ReturnEvaluationResult(false, string.Empty, false, "Type d'expression de retour inconnu.");
        }
    }

    private static string StripReturnPrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        if (ReturnKeywordPrefixRegex.IsMatch(trimmed))
        {
            trimmed = trimmed[6..].TrimStart();
        }

        if (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed;
    }

    private static bool TryParseSingleExpression(
        string text,
        bool isTopLevel,
        out ReturnAstNode? ast,
        out string? error)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            ast = null;
            error = "Expression de retour vide.";
            return false;
        }

        if (ExactListMarkerRegex.Match(trimmed) is { Success: true } listMatch)
        {
            ast = new ReturnAstNode(ReturnAstKind.ListVariable, TextValue: listMatch.Groups[1].Value);
            error = null;
            return true;
        }

        if (ExactScalarMarkerRegex.Match(trimmed) is { Success: true } scalarMatch)
        {
            ast = new ReturnAstNode(ReturnAstKind.ScalarVariable, TextValue: scalarMatch.Groups[1].Value);
            error = null;
            return true;
        }

        if ((trimmed.StartsWith('{') && !trimmed.StartsWith("{{", StringComparison.Ordinal)) ||
            (trimmed.StartsWith('[') && !trimmed.StartsWith("[[", StringComparison.Ordinal)))
        {
            var pos = 0;
            if (!TryParseValueAt(trimmed, ref pos, out ast, out error))
            {
                return false;
            }

            SkipWhitespace(trimmed, ref pos);
            if (pos < trimmed.Length)
            {
                ast = null;
                error = $"Caractères inattendus après l'expression : « {trimmed[pos..]} ».";
                return false;
            }

            return true;
        }

        if (trimmed[0] is '"' or '\'')
        {
            var pos = 0;
            if (!TryReadQuotedString(trimmed, ref pos, out var unescaped, out error))
            {
                ast = null;
                return false;
            }

            SkipWhitespace(trimmed, ref pos);
            if (pos < trimmed.Length)
            {
                if (isTopLevel && AnyMarkerRegex.IsMatch(trimmed))
                {
                    ast = new ReturnAstNode(ReturnAstKind.TemplateString, TextValue: trimmed);
                    error = null;
                    return true;
                }

                ast = null;
                error = $"Caractères inattendus après la chaîne : « {trimmed[pos..]} ».";
                return false;
            }

            ast = AnyMarkerRegex.IsMatch(unescaped)
                ? new ReturnAstNode(ReturnAstKind.TemplateString, TextValue: unescaped)
                : new ReturnAstNode(ReturnAstKind.LiteralString, TextValue: unescaped);
            error = null;
            return true;
        }

        if (trimmed == "true")
        {
            ast = new ReturnAstNode(ReturnAstKind.LiteralBool, BoolValue: true);
            error = null;
            return true;
        }

        if (trimmed == "false")
        {
            ast = new ReturnAstNode(ReturnAstKind.LiteralBool, BoolValue: false);
            error = null;
            return true;
        }

        if (trimmed == "null")
        {
            ast = new ReturnAstNode(ReturnAstKind.LiteralNull);
            error = null;
            return true;
        }

        if (JsonNumberRegex.IsMatch(trimmed))
        {
            ast = new ReturnAstNode(ReturnAstKind.LiteralNumber, TextValue: trimmed);
            error = null;
            return true;
        }

        if (BareIdentifierRegex.IsMatch(trimmed))
        {
            ast = new ReturnAstNode(ReturnAstKind.AutoVariable, TextValue: trimmed);
            error = null;
            return true;
        }

        if (isTopLevel && AnyMarkerRegex.IsMatch(trimmed))
        {
            ast = new ReturnAstNode(ReturnAstKind.TemplateString, TextValue: trimmed);
            error = null;
            return true;
        }

        ast = null;
        error = $"Syntaxe de retour invalide près de « {trimmed} ». Utilise un nom de variable, {{...}}, [...], ou des guillemets \"...\".";
        return false;
    }

    private static bool TryParseValueAt(string s, ref int pos, out ReturnAstNode? node, out string? error)
    {
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length)
        {
            node = null;
            error = "Valeur attendue dans l'expression de retour.";
            return false;
        }

        var scalarMatch = AnchoredScalarMarkerRegex.Match(s, pos);
        if (scalarMatch.Success)
        {
            pos += scalarMatch.Length;
            node = new ReturnAstNode(ReturnAstKind.ScalarVariable, TextValue: scalarMatch.Groups[1].Value);
            error = null;
            return true;
        }

        var listMatch = AnchoredListMarkerRegex.Match(s, pos);
        if (listMatch.Success)
        {
            pos += listMatch.Length;
            node = new ReturnAstNode(ReturnAstKind.ListVariable, TextValue: listMatch.Groups[1].Value);
            error = null;
            return true;
        }

        var ch = s[pos];
        if (ch == '{')
        {
            return TryParseObjectAt(s, ref pos, out node, out error);
        }

        if (ch == '[')
        {
            return TryParseArrayAt(s, ref pos, out node, out error);
        }

        if (ch is '"' or '\'')
        {
            if (!TryReadQuotedString(s, ref pos, out var text, out error))
            {
                node = null;
                return false;
            }

            node = AnyMarkerRegex.IsMatch(text)
                ? new ReturnAstNode(ReturnAstKind.TemplateString, TextValue: text)
                : new ReturnAstNode(ReturnAstKind.LiteralString, TextValue: text);
            error = null;
            return true;
        }

        if (ch == '-' || char.IsAsciiDigit(ch))
        {
            var start = pos;
            if (s[pos] == '-')
            {
                pos++;
            }

            while (pos < s.Length && (char.IsAsciiDigit(s[pos]) || s[pos] is '.' or 'e' or 'E' or '+' or '-'))
            {
                if (s[pos] is '+' or '-' && s[pos - 1] is not ('e' or 'E'))
                {
                    break;
                }
                pos++;
            }

            var numToken = s[start..pos];
            if (!JsonNumberRegex.IsMatch(numToken))
            {
                node = null;
                error = $"Nombre invalide dans l'expression de retour : « {numToken} ».";
                return false;
            }

            node = new ReturnAstNode(ReturnAstKind.LiteralNumber, TextValue: numToken);
            error = null;
            return true;
        }

        var identMatch = AnchoredIdentifierRegex.Match(s, pos);
        if (identMatch.Success)
        {
            var ident = identMatch.Value;
            pos += identMatch.Length;
            node = ident switch
            {
                "true" => new ReturnAstNode(ReturnAstKind.LiteralBool, BoolValue: true),
                "false" => new ReturnAstNode(ReturnAstKind.LiteralBool, BoolValue: false),
                "null" => new ReturnAstNode(ReturnAstKind.LiteralNull),
                _ => new ReturnAstNode(ReturnAstKind.AutoVariable, TextValue: ident)
            };
            error = null;
            return true;
        }

        node = null;
        error = $"Jeton inattendu près de « {s[pos..Math.Min(s.Length, pos + 20)]} ».";
        return false;
    }

    private static bool TryParseObjectAt(string s, ref int pos, out ReturnAstNode? node, out string? error)
    {
        pos++; // '{'
        var properties = new List<ReturnObjectEntry>();

        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                node = null;
                error = "Accolade fermante « } » manquante dans l'objet de retour.";
                return false;
            }

            if (s[pos] == '}')
            {
                pos++;
                node = new ReturnAstNode(ReturnAstKind.Object, Properties: properties);
                error = null;
                return true;
            }

            string key;
            if (s[pos] is '"' or '\'')
            {
                if (!TryReadQuotedString(s, ref pos, out key, out error))
                {
                    node = null;
                    return false;
                }
            }
            else
            {
                var keyStart = pos;
                while (pos < s.Length && s[pos] is not (':' or ',' or '}' or '{' or '[' or ']') && !char.IsWhiteSpace(s[pos]))
                {
                    pos++;
                }

                key = s[keyStart..pos].Trim();
                if (string.IsNullOrEmpty(key))
                {
                    node = null;
                    error = "Nom de propriété attendu dans l'objet de retour.";
                    return false;
                }
            }

            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ':')
            {
                pos++;
                if (!TryParseValueAt(s, ref pos, out var valueNode, out error))
                {
                    node = null;
                    return false;
                }

                properties.Add(new ReturnObjectEntry(key, valueNode!));
            }
            else if (pos < s.Length && s[pos] is ',' or '}' && BareIdentifierRegex.IsMatch(key))
            {
                // Raccourci { nom_var1, nom_var2 }
                properties.Add(new ReturnObjectEntry(key, new ReturnAstNode(ReturnAstKind.AutoVariable, TextValue: key)));
            }
            else
            {
                node = null;
                error = $"Attendu « : » après la propriété « {key} ».";
                return false;
            }

            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                node = null;
                error = "Accolade fermante « } » manquante dans l'objet de retour.";
                return false;
            }

            if (s[pos] == ',')
            {
                pos++;
                continue;
            }

            if (s[pos] == '}')
            {
                pos++;
                node = new ReturnAstNode(ReturnAstKind.Object, Properties: properties);
                error = null;
                return true;
            }

            node = null;
            error = $"Attendu « , » ou « }} » après la valeur de « {key} ».";
            return false;
        }
    }

    private static bool TryParseArrayAt(string s, ref int pos, out ReturnAstNode? node, out string? error)
    {
        pos++; // '['
        var elements = new List<ReturnAstNode>();

        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                node = null;
                error = "Crochet fermant « ] » manquant dans la liste de retour.";
                return false;
            }

            if (s[pos] == ']')
            {
                pos++;
                node = new ReturnAstNode(ReturnAstKind.Array, Elements: elements);
                error = null;
                return true;
            }

            if (!TryParseValueAt(s, ref pos, out var elementNode, out error))
            {
                node = null;
                return false;
            }

            elements.Add(elementNode!);

            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
            {
                node = null;
                error = "Crochet fermant « ] » manquant dans la liste de retour.";
                return false;
            }

            if (s[pos] == ',')
            {
                pos++;
                continue;
            }

            if (s[pos] == ']')
            {
                pos++;
                node = new ReturnAstNode(ReturnAstKind.Array, Elements: elements);
                error = null;
                return true;
            }

            node = null;
            error = "Attendu « , » ou « ] » dans la liste de retour.";
            return false;
        }
    }

    private static bool TryReadQuotedString(string s, ref int pos, out string value, out string? error)
    {
        var quote = s[pos++];
        var sb = new StringBuilder();
        while (pos < s.Length)
        {
            var ch = s[pos++];
            if (ch == '\\')
            {
                if (pos >= s.Length)
                {
                    value = string.Empty;
                    error = "Échappement incomplet dans une chaîne de caractères.";
                    return false;
                }

                var esc = s[pos++];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    '/' => '/',
                    _ => esc
                });
                continue;
            }

            if (ch == quote)
            {
                value = sb.ToString();
                error = null;
                return true;
            }

            sb.Append(ch);
        }

        value = string.Empty;
        error = $"Guillemet fermant « {quote} » manquant.";
        return false;
    }

    private static bool TrySplitTopLevelCommas(string s, out List<string> items)
    {
        items = [];
        var depthBrace = 0;
        var depthBracket = 0;
        var inQuote = '\0';
        var escaped = false;
        var start = 0;
        var foundComma = false;

        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inQuote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == inQuote)
                {
                    inQuote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                inQuote = ch;
            }
            else if (ch == '{')
            {
                depthBrace++;
            }
            else if (ch == '}')
            {
                depthBrace = Math.Max(0, depthBrace - 1);
            }
            else if (ch == '[')
            {
                depthBracket++;
            }
            else if (ch == ']')
            {
                depthBracket = Math.Max(0, depthBracket - 1);
            }
            else if (ch == ',' && depthBrace == 0 && depthBracket == 0)
            {
                foundComma = true;
                items.Add(s[start..i].Trim());
                start = i + 1;
            }
        }

        if (!foundComma)
        {
            return false;
        }

        items.Add(s[start..].Trim());
        return true;
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }

    private static bool TryBuildJsonNode(
        ReturnAstNode node,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lists,
        int globalIndex,
        string guid,
        string timestamp,
        out JsonNode? jsonNode,
        out string? error)
    {
        switch (node.Kind)
        {
            case ReturnAstKind.ScalarVariable:
            {
                if (!TryResolveScalar(node.TextValue ?? string.Empty, variables, globalIndex, guid, timestamp, out var val, out error))
                {
                    jsonNode = null;
                    return false;
                }

                jsonNode = ToJsonValueOrStructured(val);
                return true;
            }

            case ReturnAstKind.ListVariable:
            {
                var name = node.TextValue ?? string.Empty;
                if (!lists.TryGetValue(name, out var listValues))
                {
                    jsonNode = null;
                    error = $"Liste inconnue ou non définie : [[{name}]].";
                    return false;
                }

                jsonNode = BuildJsonArray(listValues);
                error = null;
                return true;
            }

            case ReturnAstKind.AutoVariable:
            {
                var name = node.TextValue ?? string.Empty;
                if (TryResolveScalar(name, variables, globalIndex, guid, timestamp, out var scalarVal, out _))
                {
                    jsonNode = ToJsonValueOrStructured(scalarVal);
                    error = null;
                    return true;
                }

                if (lists.TryGetValue(name, out var listValues))
                {
                    jsonNode = BuildJsonArray(listValues);
                    error = null;
                    return true;
                }

                jsonNode = null;
                error = $"Variable inconnue ou non définie : {name}.";
                return false;
            }

            case ReturnAstKind.LiteralString:
                jsonNode = JsonValue.Create(node.TextValue ?? string.Empty);
                error = null;
                return true;

            case ReturnAstKind.TemplateString:
            {
                if (!TrySubstituteTemplateString(node.TextValue ?? string.Empty, variables, lists, globalIndex, guid, timestamp, out var text, out error))
                {
                    jsonNode = null;
                    return false;
                }

                jsonNode = JsonValue.Create(text);
                return true;
            }

            case ReturnAstKind.LiteralNumber:
                jsonNode = JsonNode.Parse(node.TextValue ?? "0");
                error = null;
                return true;

            case ReturnAstKind.LiteralBool:
                jsonNode = JsonValue.Create(node.BoolValue ?? false);
                error = null;
                return true;

            case ReturnAstKind.LiteralNull:
                jsonNode = null;
                error = null;
                return true;

            case ReturnAstKind.Array:
            {
                var arr = new JsonArray();
                if (node.Elements is not null)
                {
                    foreach (var elem in node.Elements)
                    {
                        if (!TryBuildJsonNode(elem, variables, lists, globalIndex, guid, timestamp, out var child, out error))
                        {
                            jsonNode = null;
                            return false;
                        }

                        arr.Add(child);
                    }
                }

                jsonNode = arr;
                error = null;
                return true;
            }

            case ReturnAstKind.Object:
            {
                var obj = new JsonObject();
                if (node.Properties is not null)
                {
                    foreach (var prop in node.Properties)
                    {
                        if (!TryBuildJsonNode(prop.Value, variables, lists, globalIndex, guid, timestamp, out var child, out error))
                        {
                            jsonNode = null;
                            return false;
                        }

                        obj[prop.Key] = child;
                    }
                }

                jsonNode = obj;
                error = null;
                return true;
            }

            default:
                jsonNode = null;
                error = "Nœud de retour inconnu.";
                return false;
        }
    }

    private static bool TryResolveScalar(
        string name,
        IReadOnlyDictionary<string, string> variables,
        int globalIndex,
        string guid,
        string timestamp,
        out string value,
        out string? error)
    {
        if (variables.TryGetValue(name, out var found))
        {
            value = found ?? string.Empty;
            error = null;
            return true;
        }

        switch (name)
        {
            case "$index":
                value = globalIndex.ToString(CultureInfo.InvariantCulture);
                error = null;
                return true;
            case "$guid":
                value = guid;
                error = null;
                return true;
            case "$timestamp":
                value = timestamp;
                error = null;
                return true;
            default:
                value = string.Empty;
                error = $"Variable inconnue ou non définie : {{{{{name}}}}}.";
                return false;
        }
    }

    private static bool TrySubstituteTemplateString(
        string template,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lists,
        int globalIndex,
        string guid,
        string timestamp,
        out string output,
        out string? error)
    {
        var matches = AnyMarkerRegex.Matches(template);
        if (matches.Count == 0)
        {
            output = template;
            error = null;
            return true;
        }

        var sb = new StringBuilder(template.Length + 16);
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            sb.Append(template, lastIndex, match.Index - lastIndex);

            if (match.Groups[2].Success)
            {
                var listName = match.Groups[2].Value;
                if (!lists.TryGetValue(listName, out var listValues))
                {
                    output = string.Empty;
                    error = $"Liste inconnue ou non définie : [[{listName}]].";
                    return false;
                }

                sb.Append(string.Join(", ", listValues));
            }
            else
            {
                var scalarName = match.Groups[1].Value;
                if (!TryResolveScalar(scalarName, variables, globalIndex, guid, timestamp, out var scalarVal, out error))
                {
                    output = string.Empty;
                    return false;
                }

                sb.Append(scalarVal);
            }

            lastIndex = match.Index + match.Length;
        }

        sb.Append(template, lastIndex, template.Length - lastIndex);
        output = sb.ToString();
        error = null;
        return true;
    }

    private static ReturnEvaluationResult FormatTopLevelScalar(string value)
    {
        var trimmed = value.TrimStart('\uFEFF').Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed, JsonParseOptions);
                var pretty = JsonSerializer.Serialize(doc.RootElement, IndentedJsonOptions);
                return new ReturnEvaluationResult(true, pretty, IsJson: true);
            }
            catch (JsonException)
            {
            }
        }

        return new ReturnEvaluationResult(true, value, IsJson: false);
    }

    private static JsonArray BuildJsonArray(IReadOnlyList<string> values)
    {
        var arr = new JsonArray();
        foreach (var item in values)
        {
            arr.Add(ToJsonValueOrStructured(item));
        }
        return arr;
    }

    private static JsonNode? ToJsonValueOrStructured(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var trimmed = raw.TrimStart('\uFEFF').Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(trimmed, nodeOptions: null, documentOptions: JsonParseOptions);
            }
            catch (JsonException)
            {
            }
        }

        return JsonValue.Create(raw);
    }
}
