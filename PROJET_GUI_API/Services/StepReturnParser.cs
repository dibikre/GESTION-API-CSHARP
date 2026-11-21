using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

internal static class StepReturnParser
{
    private enum TokenKind
    {
        EndOfInput,
        LeftBrace,
        RightBrace,
        LeftBracket,
        RightBracket,
        LeftParen,
        RightParen,
        Colon,
        Comma,
        Plus,
        Semicolon,
        String,
        Number,
        Identifier,
        TemplateVar,
        TemplateList
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static readonly Regex ScalarInnerRegex = new(
        @"^" + VariablePathHelper.ScalarInnerPattern + @"$",
        RegexOptions.Compiled);

    private static readonly Regex ListInnerRegex = new(
        @"^" + VariablePathHelper.ListInnerPattern + @"$",
        RegexOptions.Compiled);

    private static readonly Regex TemplateMarkerInsideStringRegex = VariablePathHelper.AnyMarkerRegex;

    public static StepReturnParseResult Parse(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new StepReturnParseResult(
                false,
                null,
                "Expression de retour vide.",
                new StepReturnDependencies([], [], []));
        }

        if (!TryTokenize(rawInput, out var tokens, out var lexError))
        {
            return new StepReturnParseResult(
                false,
                null,
                lexError,
                new StepReturnDependencies([], [], []));
        }

        var cursor = 0;
        if (tokens.Count > 1 &&
            tokens[0].Kind == TokenKind.Identifier &&
            tokens[0].Text.Equals("return", StringComparison.OrdinalIgnoreCase))
        {
            cursor = 1;
        }

        if (tokens[cursor].Kind == TokenKind.EndOfInput ||
            tokens[cursor].Kind == TokenKind.Semicolon)
        {
            return new StepReturnParseResult(
                false,
                null,
                "Aucune valeur spécifiée après « return ».",
                new StepReturnDependencies([], [], []));
        }

        var items = new List<StepReturnNode>();
        if (!TryParseExpression(tokens, ref cursor, out var firstExpr, out var parseError))
        {
            return FailParse(parseError);
        }

        items.Add(firstExpr!);

        while (tokens[cursor].Kind == TokenKind.Comma)
        {
            cursor++;
            if (tokens[cursor].Kind is TokenKind.EndOfInput or TokenKind.Semicolon)
            {
                break;
            }

            if (!TryParseExpression(tokens, ref cursor, out var nextExpr, out parseError))
            {
                return FailParse(parseError);
            }

            items.Add(nextExpr!);
        }

        while (tokens[cursor].Kind == TokenKind.Semicolon)
        {
            cursor++;
        }

        if (tokens[cursor].Kind != TokenKind.EndOfInput)
        {
            var unexpected = tokens[cursor].Text;
            return FailParse(
                $"Symbole inattendu « {unexpected} » en position {tokens[cursor].Position + 1}. " +
                "Entoure un texte libre de guillemets \"...\" ou sépare plusieurs variables par « , ».");
        }

        StepReturnNode root = items.Count == 1
            ? items[0]
            : new ArrayReturnNode(items);

        var dependencies = CollectDependencies(root);
        return new StepReturnParseResult(true, root, null, dependencies);
    }

    private static StepReturnParseResult FailParse(string? message) => new(
        false,
        null,
        message ?? "Syntaxe d'expression de retour invalide.",
        new StepReturnDependencies([], [], []));

    private static bool TryParseExpression(
        List<Token> tokens,
        ref int cursor,
        out StepReturnNode? node,
        out string? error)
    {
        if (!TryParsePrimary(tokens, ref cursor, out var left, out error))
        {
            node = null;
            return false;
        }

        if (tokens[cursor].Kind != TokenKind.Plus)
        {
            node = left;
            error = null;
            return true;
        }

        var parts = new List<StepReturnNode> { left! };
        while (tokens[cursor].Kind == TokenKind.Plus)
        {
            cursor++;
            if (!TryParsePrimary(tokens, ref cursor, out var right, out error))
            {
                node = null;
                return false;
            }

            parts.Add(right!);
        }

        node = new ConcatReturnNode(parts);
        error = null;
        return true;
    }

    private static bool TryParsePrimary(
        List<Token> tokens,
        ref int cursor,
        out StepReturnNode? node,
        out string? error)
    {
        var token = tokens[cursor];
        switch (token.Kind)
        {
            case TokenKind.TemplateVar:
                cursor++;
                node = new IdentifierReturnNode(token.Text, ForceScalar: true, ForceList: false);
                error = null;
                return true;

            case TokenKind.TemplateList:
                cursor++;
                node = new IdentifierReturnNode(token.Text, ForceScalar: false, ForceList: true);
                error = null;
                return true;

            case TokenKind.String:
                cursor++;
                var hasMarkers = TemplateMarkerInsideStringRegex.IsMatch(token.Text);
                node = new StringLiteralReturnNode(token.Text, hasMarkers);
                error = null;
                return true;

            case TokenKind.Number:
                cursor++;
                node = new NumberReturnNode(token.Text);
                error = null;
                return true;

            case TokenKind.Identifier:
                cursor++;
                if (token.Text == "true")
                {
                    node = new BooleanReturnNode(true);
                }
                else if (token.Text == "false")
                {
                    node = new BooleanReturnNode(false);
                }
                else if (token.Text == "null")
                {
                    node = new NullReturnNode();
                }
                else
                {
                    node = new IdentifierReturnNode(token.Text, ForceScalar: false, ForceList: false);
                }
                error = null;
                return true;

            case TokenKind.LeftParen:
                cursor++;
                if (!TryParseExpression(tokens, ref cursor, out node, out error))
                {
                    return false;
                }
                if (tokens[cursor].Kind != TokenKind.RightParen)
                {
                    node = null;
                    error = $"Parenthèse fermante « ) » attendue en position {tokens[cursor].Position + 1}.";
                    return false;
                }
                cursor++;
                error = null;
                return true;

            case TokenKind.LeftBracket:
                return TryParseArray(tokens, ref cursor, out node, out error);

            case TokenKind.LeftBrace:
                return TryParseObject(tokens, ref cursor, out node, out error);

            default:
                node = null;
                error = token.Kind == TokenKind.EndOfInput
                    ? "Expression incomplète en fin de saisie."
                    : $"Jeton inattendu « {token.Text} » en position {token.Position + 1}.";
                return false;
        }
    }

    private static bool TryParseArray(
        List<Token> tokens,
        ref int cursor,
        out StepReturnNode? node,
        out string? error)
    {
        cursor++; // '['
        var items = new List<StepReturnNode>();

        if (tokens[cursor].Kind == TokenKind.RightBracket)
        {
            cursor++;
            node = new ArrayReturnNode(items);
            error = null;
            return true;
        }

        while (true)
        {
            if (!TryParseExpression(tokens, ref cursor, out var item, out error))
            {
                node = null;
                return false;
            }

            items.Add(item!);

            if (tokens[cursor].Kind == TokenKind.Comma)
            {
                cursor++;
                if (tokens[cursor].Kind == TokenKind.RightBracket)
                {
                    cursor++;
                    break;
                }
                continue;
            }

            if (tokens[cursor].Kind == TokenKind.RightBracket)
            {
                cursor++;
                break;
            }

            node = null;
            error = $"Attendu « , » ou « ] » dans le tableau en position {tokens[cursor].Position + 1}.";
            return false;
        }

        node = new ArrayReturnNode(items);
        error = null;
        return true;
    }

    private static bool TryParseObject(
        List<Token> tokens,
        ref int cursor,
        out StepReturnNode? node,
        out string? error)
    {
        cursor++; // '{'
        var entries = new List<ObjectReturnEntry>();

        if (tokens[cursor].Kind == TokenKind.RightBrace)
        {
            cursor++;
            node = new ObjectReturnNode(entries);
            error = null;
            return true;
        }

        while (true)
        {
            var keyToken = tokens[cursor];
            if (keyToken.Kind is not (TokenKind.String or TokenKind.Identifier))
            {
                node = null;
                error = $"Clé d'objet attendue dans « {{ ... }} » en position {keyToken.Position + 1}.";
                return false;
            }

            var keyName = keyToken.Text;
            cursor++;

            if (tokens[cursor].Kind == TokenKind.Colon)
            {
                cursor++;
                if (!TryParseExpression(tokens, ref cursor, out var valNode, out error))
                {
                    node = null;
                    return false;
                }

                entries.Add(new ObjectReturnEntry(keyName, valNode!));
            }
            else if (keyToken.Kind == TokenKind.Identifier &&
                     tokens[cursor].Kind is TokenKind.Comma or TokenKind.RightBrace)
            {
                // Raccourci type JavaScript : { token, id }
                entries.Add(new ObjectReturnEntry(
                    keyName,
                    new IdentifierReturnNode(keyName, ForceScalar: false, ForceList: false)));
            }
            else
            {
                node = null;
                error = $"Attendu « : » après la clé « {keyName} » en position {tokens[cursor].Position + 1}.";
                return false;
            }

            if (tokens[cursor].Kind == TokenKind.Comma)
            {
                cursor++;
                if (tokens[cursor].Kind == TokenKind.RightBrace)
                {
                    cursor++;
                    break;
                }
                continue;
            }

            if (tokens[cursor].Kind == TokenKind.RightBrace)
            {
                cursor++;
                break;
            }

            node = null;
            error = $"Attendu « , » ou « }} » dans l'objet en position {tokens[cursor].Position + 1}.";
            return false;
        }

        node = new ObjectReturnNode(entries);
        error = null;
        return true;
    }

    private static bool TryTokenize(string input, out List<Token> tokens, out string? error)
    {
        tokens = [];
        var i = 0;
        var len = input.Length;

        while (i < len)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Commentaires // ou #
            if ((c == '/' && i + 1 < len && input[i + 1] == '/') || c == '#')
            {
                while (i < len && input[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            // Commentaires /* ... */
            if (c == '/' && i + 1 < len && input[i + 1] == '*')
            {
                var closeIdx = input.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (closeIdx < 0)
                {
                    error = "Commentaire « /* ... */ » non fermé.";
                    return false;
                }
                i = closeIdx + 2;
                continue;
            }

            // Marqueur {{var}} ou {{var.jsonpath}}
            if (c == '{' && i + 1 < len && input[i + 1] == '{')
            {
                var closeIdx = input.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (closeIdx > i + 2)
                {
                    var inner = input[(i + 2)..closeIdx].Trim();
                    if (ScalarInnerRegex.IsMatch(inner))
                    {
                        tokens.Add(new Token(TokenKind.TemplateVar, inner, i));
                        i = closeIdx + 2;
                        continue;
                    }
                }
            }

            // Marqueur [[liste]] ou [[var.jsonpath]]
            if (c == '[' && i + 1 < len && input[i + 1] == '[')
            {
                var closeIdx = input.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (closeIdx > i + 2)
                {
                    var inner = input[(i + 2)..closeIdx].Trim();
                    if (ListInnerRegex.IsMatch(inner))
                    {
                        tokens.Add(new Token(TokenKind.TemplateList, inner, i));
                        i = closeIdx + 2;
                        continue;
                    }
                }
            }

            switch (c)
            {
                case '{':
                    tokens.Add(new Token(TokenKind.LeftBrace, "{", i++));
                    continue;
                case '}':
                    tokens.Add(new Token(TokenKind.RightBrace, "}", i++));
                    continue;
                case '[':
                    tokens.Add(new Token(TokenKind.LeftBracket, "[", i++));
                    continue;
                case ']':
                    tokens.Add(new Token(TokenKind.RightBracket, "]", i++));
                    continue;
                case '(':
                    tokens.Add(new Token(TokenKind.LeftParen, "(", i++));
                    continue;
                case ')':
                    tokens.Add(new Token(TokenKind.RightParen, ")", i++));
                    continue;
                case ':':
                    tokens.Add(new Token(TokenKind.Colon, ":", i++));
                    continue;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", i++));
                    continue;
                case '+':
                    tokens.Add(new Token(TokenKind.Plus, "+", i++));
                    continue;
                case ';':
                    tokens.Add(new Token(TokenKind.Semicolon, ";", i++));
                    continue;
                case '"':
                case '\'':
                    if (!TryReadString(input, ref i, out var strVal, out error))
                    {
                        return false;
                    }
                    tokens.Add(new Token(TokenKind.String, strVal, i));
                    continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < len && char.IsDigit(input[i + 1])))
            {
                var start = i;
                if (c == '-')
                {
                    i++;
                }
                while (i < len && char.IsDigit(input[i]))
                {
                    i++;
                }
                if (i < len && input[i] == '.' && i + 1 < len && char.IsDigit(input[i + 1]))
                {
                    i++;
                    while (i < len && char.IsDigit(input[i]))
                    {
                        i++;
                    }
                }
                tokens.Add(new Token(TokenKind.Number, input[start..i], start));
                continue;
            }

            if (IsIdentifierStart(c))
            {
                var start = i++;
                while (i < len && IsIdentifierPart(input[i]))
                {
                    i++;
                }

                if (c != '$')
                {
                    ConsumeMemberPath(input, ref i);
                }

                tokens.Add(new Token(TokenKind.Identifier, input[start..i], start));
                continue;
            }

            error = $"Caractère inattendu « {c} » en position {i + 1}.";
            return false;
        }

        tokens.Add(new Token(TokenKind.EndOfInput, string.Empty, len));
        error = null;
        return true;
    }

    private static bool TryReadString(string input, ref int index, out string result, out string? error)
    {
        var quote = input[index];
        var startPos = index;
        index++;
        var sb = new StringBuilder();

        while (index < input.Length)
        {
            var ch = input[index++];
            if (ch == quote)
            {
                result = sb.ToString();
                error = null;
                return true;
            }

            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }

            if (index >= input.Length)
            {
                break;
            }

            var esc = input[index++];
            switch (esc)
            {
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when index + 4 <= input.Length &&
                              int.TryParse(input.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    index += 4;
                    break;
                default:
                    sb.Append(esc);
                    break;
            }
        }

        result = string.Empty;
        error = $"Chaîne de caractères non fermée ouverte en position {startPos + 1}.";
        return false;
    }

    private static void ConsumeMemberPath(string input, ref int index)
    {
        var len = input.Length;
        while (index < len)
        {
            if (input[index] == '.')
            {
                var next = index + 1;
                if (next >= len || char.IsWhiteSpace(input[next]) || IsMemberDelimiter(input[next]))
                {
                    break;
                }

                index = next;
                while (index < len && !char.IsWhiteSpace(input[index]) && !IsMemberDelimiter(input[index]))
                {
                    index++;
                }
            }
            else if (input[index] == '[')
            {
                var scan = index + 1;
                var inQuote = '\0';
                var escaped = false;
                var closeIdx = -1;
                var invalidUnquoted = false;

                while (scan < len)
                {
                    var ch = input[scan];
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
                    }
                    else
                    {
                        if (ch is '"' or '\'')
                        {
                            inQuote = ch;
                        }
                        else if (ch == ']')
                        {
                            closeIdx = scan;
                            break;
                        }
                        else if (ch is '[' or '\r' or '\n' or ',' or '{' or '}')
                        {
                            invalidUnquoted = true;
                            break;
                        }
                    }

                    scan++;
                }

                if (closeIdx < 0 || invalidUnquoted || inQuote != '\0')
                {
                    break;
                }

                index = closeIdx + 1;
            }
            else
            {
                break;
            }
        }
    }

    private static bool IsMemberDelimiter(char ch) =>
        ch is '.' or '[' or ']' or ',' or '}' or '{' or ')' or '(' or ';' or '+' or ':' or '"' or '\'' or '/' or '#';

    private static bool IsIdentifierStart(char ch) =>
        char.IsLetter(ch) || ch == '_' || ch == '$';

    private static bool IsIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    private static StepReturnDependencies CollectDependencies(StepReturnNode root)
    {
        var scalars = new List<string>();
        var lists = new List<string>();
        var ambiguous = new List<string>();
        var seenScalars = new HashSet<string>(StringComparer.Ordinal);
        var seenLists = new HashSet<string>(StringComparer.Ordinal);
        var seenAmbiguous = new HashSet<string>(StringComparer.Ordinal);

        void Visit(StepReturnNode node)
        {
            switch (node)
            {
                case IdentifierReturnNode id:
                    if (id.Name is "$index" or "$guid" or "$timestamp" or "$ip")
                    {
                        return;
                    }
                    if (VariablePathHelper.TrySplit(id.Name, out var rootName, out var jsonPath) &&
                        jsonPath is not null)
                    {
                        if (seenAmbiguous.Add(rootName)) ambiguous.Add(rootName);
                        return;
                    }
                    if (id.ForceList)
                    {
                        if (seenLists.Add(id.Name)) lists.Add(id.Name);
                    }
                    else if (id.ForceScalar)
                    {
                        if (seenScalars.Add(id.Name)) scalars.Add(id.Name);
                    }
                    else
                    {
                        if (seenAmbiguous.Add(id.Name)) ambiguous.Add(id.Name);
                    }
                    break;

                case StringLiteralReturnNode str when str.HasTemplateMarkers:
                    foreach (Match m in TemplateMarkerInsideStringRegex.Matches(str.Value))
                    {
                        if (m.Groups[2].Success)
                        {
                            var rawListName = m.Groups[2].Value;
                            if (VariablePathHelper.TrySplit(rawListName, out var listRoot, out var listPath) &&
                                listPath is not null)
                            {
                                if (seenAmbiguous.Add(listRoot)) ambiguous.Add(listRoot);
                            }
                            else if (seenLists.Add(rawListName))
                            {
                                lists.Add(rawListName);
                            }
                        }
                        else if (m.Groups[1].Success)
                        {
                            if (VariablePathHelper.TrySplit(m.Groups[1].Value, out var scalarRoot, out var scalarPath) &&
                                scalarPath is not null)
                            {
                                if (scalarRoot is not ("$index" or "$guid" or "$timestamp" or "$ip") &&
                                    seenAmbiguous.Add(scalarRoot))
                                {
                                    ambiguous.Add(scalarRoot);
                                }
                            }
                            else
                            {
                                var rawScalarName = VariablePathHelper.ExtractRootName(m.Groups[1].Value);
                                if (rawScalarName is not ("$index" or "$guid" or "$timestamp" or "$ip") &&
                                    seenScalars.Add(rawScalarName))
                                {
                                    scalars.Add(rawScalarName);
                                }
                            }
                        }
                    }
                    break;

                case ArrayReturnNode arr:
                    foreach (var item in arr.Items) Visit(item);
                    break;

                case ObjectReturnNode obj:
                    foreach (var entry in obj.Entries) Visit(entry.Value);
                    break;

                case ConcatReturnNode concat:
                    foreach (var part in concat.Parts) Visit(part);
                    break;
            }
        }

        Visit(root);
        return new StepReturnDependencies(scalars, lists, ambiguous);
    }
}
