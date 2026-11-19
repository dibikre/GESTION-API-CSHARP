using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using ApiTester.Models;

namespace ApiTester.Services;

public class CurlParser : ICurlParser
{
    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept-Encoding",
        "Content-Length",
        "Host",
        "Connection",
        "Transfer-Encoding"
    };

    private static readonly HashSet<string> KnownValueOptionsToIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        "-m", "--max-time", "--connect-timeout", "-o", "--output",
        "-x", "--proxy", "--resolve", "--interface", "--ciphers",
        "--cacert", "--cert", "--key", "-E"
    };

    public CurlParseResult Parse(string input)
    {
        var result = new CurlParseResult();

        var rawWarnings = new List<(string Message, bool IsImportant)>();

        if (string.IsNullOrWhiteSpace(input))
        {
            result.Warnings.Add("La commande curl est vide.");
            return result;
        }

        var isCmdFormat = DetectCmdFormat(input);
        var tokens = isCmdFormat ? TokenizeCmd(input) : TokenizePosixOrPowerShell(input);

        if (tokens.Count == 0)
        {
            result.Warnings.Add("Aucun argument détecté dans la commande curl.");
            return result;
        }

        var startIndex = 0;
        if (IsCurlExecutable(tokens[0]))
        {
            startIndex = 1;
        }

        string? rawUrl = null;
        HttpMethodType? explicitMethod = null;
        var dataParts = new List<string>();
        var useGetWithData = false;
        var endOfOptions = false;

        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (!endOfOptions && token == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (endOfOptions || !token.StartsWith('-'))
            {
                if (rawUrl is null)
                {
                    rawUrl = token;
                }
                else
                {
                    rawWarnings.Add(($"Argument supplémentaire ignoré : {token}", false));
                }
                continue;
            }

            // Gestion des options sous forme --option=valeur
            string optionName;
            string? inlineValue = null;
            var equalsIdx = token.IndexOf('=');
            if (token.StartsWith("--", StringComparison.Ordinal) && equalsIdx > 2)
            {
                optionName = token[..equalsIdx];
                inlineValue = token[(equalsIdx + 1)..];
            }
            else
            {
                optionName = token;
            }

            switch (optionName)
            {
                case "--url":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var urlVal))
                    {
                        rawUrl = urlVal;
                    }
                    break;

                case "-X":
                case "--request":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var methodVal))
                    {
                        explicitMethod = ParseHttpMethod(methodVal, rawWarnings);
                    }
                    break;

                case "-H":
                case "--header":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var headerVal))
                    {
                        ParseHeader(headerVal, result.Request.Headers, rawWarnings);
                    }
                    break;

                case "-d":
                case "--data":
                case "--data-raw":
                case "--data-binary":
                case "--data-urlencode":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var dataVal))
                    {
                        dataParts.Add(dataVal);
                    }
                    break;

                case "-b":
                case "--cookie":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var cookieVal))
                    {
                        result.Request.Headers.Add(new KeyValueItem("Cookie", cookieVal, true));
                    }
                    break;

                case "-u":
                case "--user":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var userVal))
                    {
                        ApplyBasicAuth(userVal, result.Request.Auth);
                    }
                    break;

                case "-A":
                case "--user-agent":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var uaVal))
                    {
                        result.Request.Headers.Add(new KeyValueItem("User-Agent", uaVal, true));
                    }
                    break;

                case "-e":
                case "--referer":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, rawWarnings, out var refVal))
                    {
                        result.Request.Headers.Add(new KeyValueItem("Referer", refVal, true));
                    }
                    break;

                case "-G":
                case "--get":
                    useGetWithData = true;
                    break;

                case "--compressed":
                case "-s":
                case "--silent":
                case "-S":
                case "--show-error":
                case "-v":
                case "--verbose":
                case "--http1.1":
                case "--http2":
                    break;

                case "-L":
                case "--location":
                    rawWarnings.Add(($"Option {optionName} ignorée : les redirections suivent le comportement par défaut du client HTTP.", true));
                    break;

                case "-k":
                case "--insecure":
                    rawWarnings.Add(($"Option {optionName} ignorée : la validation des certificats TLS reste active.", true));
                    break;

                case "-i":
                case "--include":
                    rawWarnings.Add(($"Option {optionName} ignorée : les en-têtes de réponse sont affichés séparément.", true));
                    break;

                default:
                    if (optionName.StartsWith("-X", StringComparison.Ordinal) && optionName.Length > 2)
                    {
                        explicitMethod = ParseHttpMethod(optionName[2..], rawWarnings);
                    }
                    else if (KnownValueOptionsToIgnore.Contains(optionName))
                    {
                        if (inlineValue is null && i + 1 < tokens.Count)
                        {
                            i++;
                        }
                        rawWarnings.Add(($"Option curl ignorée : {optionName}", false));
                    }
                    else
                    {
                        rawWarnings.Add(($"Option curl inconnue ignorée : {optionName}", false));
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            rawWarnings.Add(("Aucune URL trouvée dans la commande curl.", true));
        }
        else
        {
            SplitUrlAndQueryParams(rawUrl, result.Request, rawWarnings);
        }

        var combinedData = dataParts.Count > 0 ? string.Join("&", dataParts) : string.Empty;

        if (useGetWithData && !string.IsNullOrEmpty(combinedData))
        {
            ParseFormPairsInto(combinedData, result.Request.QueryParams);
            result.Request.BodyType = BodyType.None;
            result.Request.Body = string.Empty;
        }
        else if (dataParts.Count > 0)
        {
            result.Request.Body = combinedData;
            var contentType = FindHeaderValue(result.Request.Headers, "Content-Type");

            if (contentType is not null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                result.Request.BodyType = BodyType.Json;
            }
            else if (contentType is not null && contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                result.Request.BodyType = BodyType.FormUrlEncoded;
                ParseFormPairsInto(combinedData, result.Request.FormFields);
            }
            else
            {
                result.Request.BodyType = BodyType.Text;
            }
        }
        else
        {
            result.Request.BodyType = BodyType.None;
            result.Request.Body = string.Empty;
        }

        result.Request.Method = explicitMethod
            ?? (result.Request.BodyType != BodyType.None ? HttpMethodType.Post : HttpMethodType.Get);

        result.Warnings = BuildGroupedSortedWarnings(rawWarnings);
        return result;
    }

    private static bool DetectCmdFormat(string input)
    {
        var trimmed = input.TrimStart();
        if (trimmed.StartsWith("curl.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (input.Contains("^\"", StringComparison.Ordinal) ||
            input.Contains("^&", StringComparison.Ordinal) ||
            input.Contains("^^", StringComparison.Ordinal) ||
            input.Contains("^%", StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.Contains("^\n", StringComparison.Ordinal);
    }

    private static List<string> TokenizeCmd(string input)
    {
        // PASSE 1.1 : jointure des lignes (^ suivi immédiatement de CRLF ou LF, sans espace ajouté)
        var joined = input.Replace("^\r\n", string.Empty, StringComparison.Ordinal)
                          .Replace("^\n", string.Empty, StringComparison.Ordinal);

        // PASSE 1.2 : dé-échappement cmd (^X -> X) sur une seule ligne logique
        var unescaped = new StringBuilder(joined.Length);
        for (var i = 0; i < joined.Length; i++)
        {
            if (joined[i] == '^' && i + 1 < joined.Length)
            {
                unescaped.Append(joined[++i]);
            }
            else
            {
                unescaped.Append(joined[i]);
            }
        }

        // PASSE 2 : découpage selon les règles CommandLineToArgvW
        return SplitWindowsCommandLineArgs(unescaped.ToString());
    }

    private static List<string> SplitWindowsCommandLineArgs(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            if (c == '\\')
            {
                var slashCount = 1;
                while (i + 1 < text.Length && text[i + 1] == '\\')
                {
                    slashCount++;
                    i++;
                }

                hasToken = true;
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    else
                    {
                        current.Append('"');
                    }

                    i++;
                }
                else
                {
                    current.Append('\\', slashCount);
                }

                continue;
            }

            if (c == '"')
            {
                hasToken = true;
                inQuotes = !inQuotes;
                continue;
            }

            hasToken = true;
            current.Append(c);
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static List<string> TokenizePosixOrPowerShell(string input)
    {
        var text = input.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n');

        var tokens = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var inAnsiCQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inSingleQuotes)
            {
                if (c == '\'')
                {
                    inSingleQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (inAnsiCQuotes)
            {
                if (c == '\'')
                {
                    inAnsiCQuotes = false;
                }
                else if (c == '\\' && i + 1 < text.Length)
                {
                    var next = text[++i];
                    switch (next)
                    {
                        case 'n': current.Append('\n'); break;
                        case 'r': current.Append('\r'); break;
                        case 't': current.Append('\t'); break;
                        case '\\': current.Append('\\'); break;
                        case '\'': current.Append('\''); break;
                        case '"': current.Append('"'); break;
                        case 'u' when i + 4 < text.Length &&
                                      int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint):
                            current.Append((char)codePoint);
                            i += 4;
                            break;
                        default:
                            current.Append(next);
                            break;
                    }
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (inDoubleQuotes)
            {
                if (c == '"')
                {
                    inDoubleQuotes = false;
                }
                else if (c == '\\' && i + 1 < text.Length)
                {
                    var next = text[i + 1];
                    if (next == '\n')
                    {
                        i++;
                    }
                    else if (next is '"' or '\\' or '$' or '`')
                    {
                        current.Append(next);
                        i++;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '`' && i + 1 < text.Length)
                {
                    var next = text[i + 1];
                    if (next == '\n')
                    {
                        i++;
                    }
                    else if (next is '"' or '`' or '$')
                    {
                        current.Append(next);
                        i++;
                    }
                    else if (next == 'n')
                    {
                        current.Append('\n');
                        i++;
                    }
                    else if (next == 'r')
                    {
                        current.Append('\r');
                        i++;
                    }
                    else if (next == 't')
                    {
                        current.Append('\t');
                        i++;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            // Hors guillemets : continuation de ligne \ ou `
            if ((c == '\\' || c == '`') && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            hasToken = true;

            if (c == '$' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                inAnsiCQuotes = true;
                i++;
            }
            else if (c == '\'')
            {
                inSingleQuotes = true;
            }
            else if (c == '"')
            {
                inDoubleQuotes = true;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
            }
            else if (c == '`' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
            }
            else
            {
                current.Append(c);
            }
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool IsCurlExecutable(string token)
    {
        var name = token.Trim();
        var slashIdx = name.LastIndexOfAny(['/', '\\']);
        if (slashIdx >= 0)
        {
            name = name[(slashIdx + 1)..];
        }

        return name.Equals("curl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConsumeOptionValue(
        IReadOnlyList<string> tokens,
        ref int index,
        string? inlineValue,
        string optionName,
        List<(string Message, bool IsImportant)> warnings,
        out string value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue;
            return true;
        }

        if (index + 1 < tokens.Count)
        {
            index++;
            value = tokens[index];
            return true;
        }

        warnings.Add(($"Valeur manquante pour l'option {optionName}.", true));
        value = string.Empty;
        return false;
    }

    private static HttpMethodType? ParseHttpMethod(
        string rawMethod,
        List<(string Message, bool IsImportant)> warnings)
    {
        return rawMethod.Trim().ToUpperInvariant() switch
        {
            "GET" => HttpMethodType.Get,
            "POST" => HttpMethodType.Post,
            "PUT" => HttpMethodType.Put,
            "PATCH" => HttpMethodType.Patch,
            "DELETE" => HttpMethodType.Delete,
            var other => AddUnsupportedMethodWarning(other, warnings)
        };
    }

    private static HttpMethodType? AddUnsupportedMethodWarning(
        string methodName,
        List<(string Message, bool IsImportant)> warnings)
    {
        warnings.Add(($"Méthode HTTP « {methodName} » non prise en charge ; GET sera utilisée par défaut.", true));
        return HttpMethodType.Get;
    }

    private static void ParseHeader(
        string rawHeader,
        List<KeyValueItem> headers,
        List<(string Message, bool IsImportant)> warnings)
    {
        var colonIndex = rawHeader.IndexOf(':');
        if (colonIndex <= 0)
        {
            warnings.Add(($"En-tête mal formé ignoré : « {rawHeader} ».", true));
            return;
        }

        var name = rawHeader[..colonIndex].Trim();
        var value = rawHeader[(colonIndex + 1)..].Trim();

        if (IgnoredHeaders.Contains(name))
        {
            warnings.Add(($"En-tête « {name} » ignoré : géré automatiquement par l'application.", true));
            return;
        }

        headers.Add(new KeyValueItem(name, value, true));
    }

    private static void ApplyBasicAuth(string rawCredentials, AuthConfig auth)
    {
        auth.Type = AuthType.Basic;
        var colonIndex = rawCredentials.IndexOf(':');
        if (colonIndex < 0)
        {
            auth.Username = rawCredentials;
            auth.Password = string.Empty;
        }
        else
        {
            auth.Username = rawCredentials[..colonIndex];
            auth.Password = rawCredentials[(colonIndex + 1)..];
        }
    }

    private static void SplitUrlAndQueryParams(
        string rawUrl,
        RequestModel request,
        List<(string Message, bool IsImportant)> warnings)
    {
        var trimmed = rawUrl.Trim();
        var fragmentIndex = trimmed.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            trimmed = trimmed[..fragmentIndex];
        }

        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex >= 0)
        {
            var baseUrl = trimmed[..queryIndex];
            var queryString = trimmed[(queryIndex + 1)..];
            request.Url = baseUrl;
            ParseFormPairsInto(queryString, request.QueryParams);
        }
        else
        {
            request.Url = trimmed;
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            warnings.Add(($"L'URL extraite semble invalide ou incomplète : {request.Url}", true));
        }
    }

    private static void ParseFormPairsInto(string encodedPairs, List<KeyValueItem> target)
    {
        if (string.IsNullOrEmpty(encodedPairs))
        {
            return;
        }

        var segments = encodedPairs.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex < 0)
            {
                target.Add(new KeyValueItem(WebUtility.UrlDecode(segment), string.Empty, true));
            }
            else
            {
                var key = WebUtility.UrlDecode(segment[..equalsIndex]);
                var value = WebUtility.UrlDecode(segment[(equalsIndex + 1)..]);
                target.Add(new KeyValueItem(key, value, true));
            }
        }
    }

    private static string? FindHeaderValue(IEnumerable<KeyValueItem> headers, string headerName)
    {
        return headers.LastOrDefault(h => h.IsEnabled && h.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    public static bool IsImportantWarning(string warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return false;
        }

        return !warning.StartsWith("Argument supplémentaire ignoré :", StringComparison.Ordinal)
            && !warning.StartsWith("Option curl ignorée :", StringComparison.Ordinal)
            && !warning.StartsWith("Option curl inconnue ignorée :", StringComparison.Ordinal);
    }

    public static List<string> GroupAndOrderWarnings(IEnumerable<string> warnings)
    {
        var list = new List<(string Message, bool IsImportant)>();
        foreach (var warning in warnings)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                list.Add((warning, IsImportantWarning(warning)));
            }
        }

        return BuildGroupedSortedWarnings(list);
    }

    public static List<string> MergeAndOrderWarnings(
        IEnumerable<string> primaryWarnings,
        IEnumerable<string> secondaryWarnings)
    {
        var ordered = new List<(string Message, bool IsImportant)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var warning in primaryWarnings)
        {
            if (!string.IsNullOrWhiteSpace(warning) && seen.Add(warning))
            {
                ordered.Add((warning, IsImportantWarning(warning)));
            }
        }

        foreach (var warning in secondaryWarnings)
        {
            if (!string.IsNullOrWhiteSpace(warning) && seen.Add(warning))
            {
                ordered.Add((warning, IsImportantWarning(warning)));
            }
        }

        return ordered
            .OrderByDescending(w => w.IsImportant)
            .Select(w => w.Message)
            .ToList();
    }

    private static List<string> BuildGroupedSortedWarnings(List<(string Message, bool IsImportant)> rawWarnings)
    {
        if (rawWarnings.Count == 0)
        {
            return [];
        }

        var grouped = new List<(string Message, bool IsImportant, int Count)>();
        var indexByMessage = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (message, isImportant) in rawWarnings)
        {
            if (indexByMessage.TryGetValue(message, out var idx))
            {
                var existing = grouped[idx];
                grouped[idx] = (existing.Message, existing.IsImportant || isImportant, existing.Count + 1);
            }
            else
            {
                indexByMessage[message] = grouped.Count;
                grouped.Add((message, isImportant, 1));
            }
        }

        return grouped
            .OrderByDescending(g => g.IsImportant)
            .Select(g => g.Count > 1 ? $"{g.Message} (×{g.Count})" : g.Message)
            .ToList();
    }
}
