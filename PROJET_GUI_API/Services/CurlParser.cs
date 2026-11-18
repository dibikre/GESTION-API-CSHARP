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
                    result.Warnings.Add($"Argument supplémentaire ignoré : {token}");
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
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var urlVal))
                    {
                        rawUrl = urlVal;
                    }
                    break;

                case "-X":
                case "--request":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var methodVal))
                    {
                        explicitMethod = ParseHttpMethod(methodVal, result.Warnings);
                    }
                    break;

                case "-H":
                case "--header":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var headerVal))
                    {
                        ParseHeader(headerVal, result.Request.Headers, result.Warnings);
                    }
                    break;

                case "-d":
                case "--data":
                case "--data-raw":
                case "--data-binary":
                case "--data-urlencode":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var dataVal))
                    {
                        dataParts.Add(dataVal);
                    }
                    break;

                case "-b":
                case "--cookie":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var cookieVal))
                    {
                        result.Request.Headers.Add(new KeyValueItem("Cookie", cookieVal, true));
                    }
                    break;

                case "-u":
                case "--user":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var userVal))
                    {
                        ApplyBasicAuth(userVal, result.Request.Auth);
                    }
                    break;

                case "-A":
                case "--user-agent":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var uaVal))
                    {
                        result.Request.Headers.Add(new KeyValueItem("User-Agent", uaVal, true));
                    }
                    break;

                case "-e":
                case "--referer":
                    if (TryConsumeOptionValue(tokens, ref i, inlineValue, optionName, result.Warnings, out var refVal))
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
                    result.Warnings.Add($"Option {optionName} ignorée : les redirections suivent le comportement par défaut du client HTTP.");
                    break;

                case "-k":
                case "--insecure":
                    result.Warnings.Add($"Option {optionName} ignorée : la validation des certificats TLS reste active.");
                    break;

                case "-i":
                case "--include":
                    result.Warnings.Add($"Option {optionName} ignorée : les en-têtes de réponse sont affichés séparément.");
                    break;

                default:
                    if (optionName.StartsWith("-X", StringComparison.Ordinal) && optionName.Length > 2)
                    {
                        explicitMethod = ParseHttpMethod(optionName[2..], result.Warnings);
                    }
                    else if (KnownValueOptionsToIgnore.Contains(optionName))
                    {
                        if (inlineValue is null && i + 1 < tokens.Count)
                        {
                            i++;
                        }
                        result.Warnings.Add($"Option curl ignorée : {optionName}");
                    }
                    else
                    {
                        result.Warnings.Add($"Option curl inconnue ignorée : {optionName}");
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            result.Warnings.Add("Aucune URL trouvée dans la commande curl.");
        }
        else
        {
            SplitUrlAndQueryParams(rawUrl, result.Request, result.Warnings);
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
        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal)
                              .Replace('\r', '\n');

        // Supprimer les continuations de ligne cmd (^ suivi de \n), sans casser ^^ suivi de \n
        var preprocessed = new StringBuilder(normalized.Length);
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '^' && i + 1 < normalized.Length)
            {
                if (normalized[i + 1] == '^')
                {
                    preprocessed.Append("^^");
                    i++;
                    continue;
                }

                if (normalized[i + 1] == '\n')
                {
                    i++;
                    continue;
                }
            }

            preprocessed.Append(normalized[i]);
        }

        var text = preprocessed.ToString();
        var tokens = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;
        var inCmdQuotes = false;
        var inDoubleQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inCmdQuotes)
            {
                if (c == '\\' && i + 2 < text.Length && text[i + 1] == '^' && text[i + 2] == '"')
                {
                    current.Append('"');
                    i += 2;
                }
                else if (c == '^' && i + 1 < text.Length)
                {
                    var next = text[i + 1];
                    if (next == '"')
                    {
                        inCmdQuotes = false;
                        i++;
                    }
                    else
                    {
                        current.Append(next);
                        i++;
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
                if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inDoubleQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
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

            if (c == '^' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == '"')
                {
                    inCmdQuotes = true;
                    i++;
                }
                else
                {
                    current.Append(next);
                    i++;
                }
            }
            else if (c == '"')
            {
                inDoubleQuotes = true;
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
        List<string> warnings,
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

        warnings.Add($"Valeur manquante pour l'option {optionName}.");
        value = string.Empty;
        return false;
    }

    private static HttpMethodType? ParseHttpMethod(string rawMethod, List<string> warnings)
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

    private static HttpMethodType? AddUnsupportedMethodWarning(string methodName, List<string> warnings)
    {
        warnings.Add($"Méthode HTTP « {methodName} » non prise en charge ; GET sera utilisée par défaut.");
        return HttpMethodType.Get;
    }

    private static void ParseHeader(string rawHeader, List<KeyValueItem> headers, List<string> warnings)
    {
        var colonIndex = rawHeader.IndexOf(':');
        if (colonIndex <= 0)
        {
            warnings.Add($"En-tête mal formé ignoré : « {rawHeader} ».");
            return;
        }

        var name = rawHeader[..colonIndex].Trim();
        var value = rawHeader[(colonIndex + 1)..].Trim();

        if (IgnoredHeaders.Contains(name))
        {
            warnings.Add($"En-tête « {name} » ignoré : géré automatiquement par l'application.");
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

    private static void SplitUrlAndQueryParams(string rawUrl, RequestModel request, List<string> warnings)
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
            warnings.Add($"L'URL extraite semble invalide ou incomplète : {request.Url}");
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
}
