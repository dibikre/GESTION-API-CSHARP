using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed class TemplateEngine : ITemplateEngine
{
    private static readonly Regex MarkerRegex = VariablePathHelper.ScalarMarkerRegex;

    private static readonly Regex ListMarkerRegex = VariablePathHelper.ListMarkerRegex;

    private static readonly Regex AnyMarkerRegex = VariablePathHelper.AnyMarkerRegex;

    public bool IsPredefinedVariable(string name) =>
        name is "$index" or "$guid" or "$timestamp";

    public bool ContainsAnyMarker(RequestModel template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (HasMarker(template.Url))
        {
            return true;
        }

        if (template.QueryParams.Any(p => p.IsEnabled && HasMarker(p.Value)))
        {
            return true;
        }

        if (template.Headers.Any(h => h.IsEnabled && HasMarker(h.Value)))
        {
            return true;
        }

        if (template.BodyType != BodyType.FormUrlEncoded && HasMarker(template.Body))
        {
            return true;
        }

        if (template.BodyType == BodyType.FormUrlEncoded &&
            template.FormFields.Any(f => f.IsEnabled && HasMarker(f.Value)))
        {
            return true;
        }

        return false;
    }

    public IReadOnlyList<string> GetUnknownPredefinedMarkers(RequestModel template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match match in MarkerRegex.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (name.StartsWith('$') && !IsPredefinedVariable(name) && seen.Add(name))
                {
                    unknown.Add(name);
                }
            }
        }

        Scan(template.Url);

        foreach (var param in template.QueryParams)
        {
            if (param.IsEnabled)
            {
                Scan(param.Value);
            }
        }

        foreach (var header in template.Headers)
        {
            if (header.IsEnabled)
            {
                Scan(header.Value);
            }
        }

        if (template.BodyType != BodyType.FormUrlEncoded)
        {
            Scan(template.Body);
        }
        else
        {
            foreach (var field in template.FormFields)
            {
                if (field.IsEnabled)
                {
                    Scan(field.Value);
                }
            }
        }

        return unknown;
    }

    public IReadOnlyList<ExtractedVariable> ExtractVariables(RequestModel template) =>
        ExtractMarkers(template, MarkerRegex, isList: false);

    public IReadOnlyList<ExtractedVariable> ExtractListVariables(RequestModel template) =>
        ExtractMarkers(template, ListMarkerRegex, isList: true);

    private static IReadOnlyList<ExtractedVariable> ExtractMarkers(
        RequestModel template,
        Regex regex,
        bool isList)
    {
        ArgumentNullException.ThrowIfNull(template);

        var orderedNames = new List<string>();
        var locationsByName = new Dictionary<string, List<VariableLocation>>(StringComparer.Ordinal);

        void ScanText(string? text, VariableLocation location)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match match in regex.Matches(text))
            {
                var rawName = match.Groups[1].Value;
                if (rawName.StartsWith('$'))
                {
                    continue;
                }

                var name = isList ? rawName : VariablePathHelper.ExtractRootName(rawName);
                if (!locationsByName.TryGetValue(name, out var locations))
                {
                    locations = [];
                    locationsByName[name] = locations;
                    orderedNames.Add(name);
                }

                if (!locations.Contains(location))
                {
                    locations.Add(location);
                }
            }
        }

        ScanText(template.Url, VariableLocation.Url);

        foreach (var param in template.QueryParams)
        {
            if (param.IsEnabled)
            {
                ScanText(param.Value, VariableLocation.QueryParams);
            }
        }

        foreach (var header in template.Headers)
        {
            if (header.IsEnabled)
            {
                ScanText(header.Value, VariableLocation.Headers);
            }
        }

        if (template.BodyType != BodyType.FormUrlEncoded)
        {
            ScanText(template.Body, VariableLocation.Body);
        }
        else
        {
            foreach (var field in template.FormFields)
            {
                if (field.IsEnabled)
                {
                    ScanText(field.Value, VariableLocation.FormFields);
                }
            }
        }

        var result = new List<ExtractedVariable>(orderedNames.Count);
        foreach (var name in orderedNames)
        {
            var locs = locationsByName[name];
            var display = string.Join(", ", locs.Select(FormatLocationLabel));
            result.Add(new ExtractedVariable(name, locs, display, isList));
        }

        return result;
    }

    public RequestModel Apply(RequestModel template, IReadOnlyDictionary<string, string> values, int index)
    {
        var result = ApplyDetailed(template, values, index);
        if (!result.IsSuccess || result.Request is null)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? "Échec de substitution des variables.");
        }

        return result.Request;
    }

    public TemplateApplyResult ApplyDetailed(RequestModel template, IReadOnlyDictionary<string, string> values, int index) =>
        ApplyDetailed(template, values, listValues: null, index);

    public TemplateApplyResult ApplyDetailed(
        RequestModel template,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? listValues,
        int index)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        if (index < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "L'index de requête doit être supérieur ou égal à 1.");
        }

        var indexText = index.ToString(CultureInfo.InvariantCulture);
        var guidText = Guid.NewGuid().ToString("D");
        var timestampText = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        bool TryResolve(bool isList, string name, out string resolvedValue, out string? error)
        {
            if (isList)
            {
                var source = listValues ?? values;
                if (source.TryGetValue(name, out var listItemVal))
                {
                    resolvedValue = listItemVal ?? string.Empty;
                    error = null;
                    return true;
                }

                if (VariablePathHelper.TrySplit(name, out var listRoot, out var listJsonPath) &&
                    listJsonPath is not null &&
                    values.TryGetValue(listRoot, out var rootJson))
                {
                    var listOutcome = ResponseExtractor.ExtractListJsonPathFromBody(rootJson, listJsonPath);
                    if (listOutcome.IsSuccess && listOutcome.Values.Count > 0)
                    {
                        resolvedValue = listOutcome.Values[0] ?? string.Empty;
                        error = null;
                        return true;
                    }

                    resolvedValue = string.Empty;
                    error = $"[[{name}]] : {listOutcome.ErrorMessage ?? "aucune valeur extraite."}";
                    return false;
                }

                resolvedValue = string.Empty;
                error = $"Liste inconnue ou non définie : [[{name}]].";
                return false;
            }

            if (values.TryGetValue(name, out var userVal))
            {
                resolvedValue = userVal ?? string.Empty;
                error = null;
                return true;
            }

            switch (name)
            {
                case "$index":
                    resolvedValue = indexText;
                    error = null;
                    return true;
                case "$guid":
                    resolvedValue = guidText;
                    error = null;
                    return true;
                case "$timestamp":
                    resolvedValue = timestampText;
                    error = null;
                    return true;
            }

            if (VariablePathHelper.TrySplit(name, out var rootName, out var jsonPath) && jsonPath is not null)
            {
                if (!values.TryGetValue(rootName, out var rootJson))
                {
                    resolvedValue = string.Empty;
                    error = $"Variable inconnue ou non définie : {{{{{rootName}}}}} (pour {{{{{name}}}}}).";
                    return false;
                }

                var jpOutcome = ResponseExtractor.ExtractJsonPathFromBody(rootJson, jsonPath);
                if (!jpOutcome.IsSuccess)
                {
                    resolvedValue = string.Empty;
                    error = $"{{{{{name}}}}} : {jpOutcome.ErrorMessage}";
                    return false;
                }

                resolvedValue = jpOutcome.Value;
                error = null;
                return true;
            }

            resolvedValue = string.Empty;
            error = $"Variable inconnue ou non définie : {{{{{name}}}}}.";
            return false;
        }

        if (!TrySubstituteUrl(template.Url, TryResolve, out var substitutedUrl, out var urlError))
        {
            return Fail(urlError);
        }

        var newQueryParams = new List<KeyValueItem>(template.QueryParams.Count);
        foreach (var param in template.QueryParams)
        {
            if (!param.IsEnabled)
            {
                newQueryParams.Add(new KeyValueItem(param.Key, param.Value, false));
                continue;
            }

            if (!TrySubstituteSimple(param.Value, TryResolve, static v => v, out var paramVal, out var paramError))
            {
                return Fail(paramError);
            }

            newQueryParams.Add(new KeyValueItem(param.Key, paramVal, true));
        }

        var newHeaders = new List<KeyValueItem>(template.Headers.Count);
        foreach (var header in template.Headers)
        {
            if (!header.IsEnabled)
            {
                newHeaders.Add(new KeyValueItem(header.Key, header.Value, false));
                continue;
            }

            if (!TrySubstituteSimple(header.Value, TryResolve, static v => v, out var headerVal, out var headerError))
            {
                return Fail(headerError);
            }

            if (headerVal.IndexOfAny(['\r', '\n']) >= 0)
            {
                return Fail($"L'en-tête « {header.Key} » contient un retour à la ligne interdit après substitution.");
            }

            newHeaders.Add(new KeyValueItem(header.Key, headerVal, true));
        }

        var newBody = template.Body ?? string.Empty;
        if (template.BodyType == BodyType.Json)
        {
            if (!TrySubstituteJsonBody(newBody, TryResolve, out newBody, out var bodyError))
            {
                return Fail(bodyError);
            }
        }
        else if (template.BodyType == BodyType.Text)
        {
            if (!TrySubstituteSimple(newBody, TryResolve, static v => v, out newBody, out var bodyError))
            {
                return Fail(bodyError);
            }
        }

        var newFormFields = new List<KeyValueItem>(template.FormFields.Count);
        foreach (var field in template.FormFields)
        {
            if (!field.IsEnabled || template.BodyType != BodyType.FormUrlEncoded)
            {
                newFormFields.Add(new KeyValueItem(field.Key, field.Value, field.IsEnabled));
                continue;
            }

            if (!TrySubstituteSimple(field.Value, TryResolve, static v => v, out var fieldVal, out var fieldError))
            {
                return Fail(fieldError);
            }

            newFormFields.Add(new KeyValueItem(field.Key, fieldVal, true));
        }

        var request = new RequestModel
        {
            Method = template.Method,
            Url = substitutedUrl,
            QueryParams = newQueryParams,
            Headers = newHeaders,
            BodyType = template.BodyType,
            Body = newBody,
            FormFields = newFormFields,
            Auth = new AuthConfig
            {
                Type = template.Auth.Type,
                Token = template.Auth.Token,
                Username = template.Auth.Username,
                Password = template.Auth.Password,
                ApiKeyName = template.Auth.ApiKeyName,
                ApiKeyValue = template.Auth.ApiKeyValue,
                ApiKeyInHeader = template.Auth.ApiKeyInHeader
            },
            TimeoutSeconds = template.TimeoutSeconds
        };

        var validJson = request.BodyType != BodyType.Json || IsValidJson(request.Body);

        return new TemplateApplyResult
        {
            IsSuccess = true,
            Request = request,
            IsValidJson = validJson
        };
    }

    public bool IsValidJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private delegate bool ResolveDelegate(bool isList, string name, out string value, out string? error);

    private static bool TrySubstituteSimple(
        string? input,
        ResolveDelegate resolve,
        Func<string, string> encoder,
        out string output,
        out string? error)
    {
        if (string.IsNullOrEmpty(input))
        {
            output = string.Empty;
            error = null;
            return true;
        }

        var matches = AnyMarkerRegex.Matches(input);
        if (matches.Count == 0)
        {
            output = input;
            error = null;
            return true;
        }

        var sb = new StringBuilder(input.Length + 16);
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            sb.Append(input, lastIndex, match.Index - lastIndex);

            var isList = match.Groups[2].Success;
            var name = isList ? match.Groups[2].Value : match.Groups[1].Value;
            if (!resolve(isList, name, out var rawValue, out error))
            {
                output = string.Empty;
                return false;
            }

            sb.Append(encoder(rawValue));
            lastIndex = match.Index + match.Length;
        }

        sb.Append(input, lastIndex, input.Length - lastIndex);
        output = sb.ToString();
        error = null;
        return true;
    }

    private static bool TrySubstituteUrl(
        string? input,
        ResolveDelegate resolve,
        out string output,
        out string? error)
    {
        if (string.IsNullOrEmpty(input))
        {
            output = string.Empty;
            error = null;
            return true;
        }

        var matches = AnyMarkerRegex.Matches(input);
        if (matches.Count == 0)
        {
            output = input;
            error = null;
            return true;
        }

        var sb = new StringBuilder(input.Length + 32);
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            sb.Append(input, lastIndex, match.Index - lastIndex);

            var isList = match.Groups[2].Success;
            var name = isList ? match.Groups[2].Value : match.Groups[1].Value;
            if (!resolve(isList, name, out var rawValue, out error))
            {
                output = string.Empty;
                return false;
            }

            sb.Append(EncodeUrlValueForContext(sb, rawValue));
            lastIndex = match.Index + match.Length;
        }

        sb.Append(input, lastIndex, input.Length - lastIndex);
        output = sb.ToString();
        error = null;
        return true;
    }

    private static string EncodeUrlValueForContext(StringBuilder prefixBuilder, string rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return string.Empty;
        }

        var prefix = prefixBuilder.ToString();
        if (IsInQueryValueContext(prefix))
        {
            return Uri.EscapeDataString(rawValue);
        }

        var trimmedPrefix = prefix.Trim();
        var valueToEncode = trimmedPrefix.Length == 0 ? rawValue.Trim() : rawValue;
        return EscapeUrlPreservingStructure(valueToEncode);
    }

    private static bool IsInQueryValueContext(string prefix)
    {
        if (prefix.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        var queryIdx = prefix.IndexOf('?');
        if (queryIdx < 0)
        {
            return false;
        }

        var queryPart = prefix[(queryIdx + 1)..];
        var lastAmp = queryPart.LastIndexOf('&');
        var currentSegment = lastAmp >= 0 ? queryPart[(lastAmp + 1)..] : queryPart;
        return currentSegment.Contains('=', StringComparison.Ordinal);
    }

    private static string EscapeUrlPreservingStructure(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (IsAllowedUriChar(ch))
            {
                sb.Append(ch);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(ch.ToString());
                foreach (var b in bytes)
                {
                    sb.Append('%');
                    sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }

        return sb.ToString();
    }

    private static bool IsAllowedUriChar(char ch) =>
        (ch >= 'A' && ch <= 'Z') ||
        (ch >= 'a' && ch <= 'z') ||
        (ch >= '0' && ch <= '9') ||
        ch is '-' or '.' or '_' or '~' or ':' or '/' or '?' or '#' or '[' or ']' or '@'
            or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or '%';

    private static bool TrySubstituteJsonBody(
        string? input,
        ResolveDelegate resolve,
        out string output,
        out string? error)
    {
        if (string.IsNullOrEmpty(input))
        {
            output = string.Empty;
            error = null;
            return true;
        }

        var matches = AnyMarkerRegex.Matches(input);
        if (matches.Count == 0)
        {
            output = input;
            error = null;
            return true;
        }

        var sb = new StringBuilder(input.Length + 16);
        var lastScannedIndex = 0;
        var inString = false;
        var escaped = false;

        foreach (Match match in matches)
        {
            for (var i = lastScannedIndex; i < match.Index; i++)
            {
                var c = input[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inString = true;
                        escaped = false;
                    }
                }
            }

            sb.Append(input, lastScannedIndex, match.Index - lastScannedIndex);

            var isList = match.Groups[2].Success;
            var name = isList ? match.Groups[2].Value : match.Groups[1].Value;
            if (!resolve(isList, name, out var rawValue, out error))
            {
                output = string.Empty;
                return false;
            }

            sb.Append(inString ? EscapeJsonStringContent(rawValue) : rawValue);
            lastScannedIndex = match.Index + match.Length;
        }

        sb.Append(input, lastScannedIndex, input.Length - lastScannedIndex);
        output = sb.ToString();
        error = null;
        return true;
    }

    private static string EscapeJsonStringContent(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool HasMarker(string? text) =>
        !string.IsNullOrEmpty(text) && AnyMarkerRegex.IsMatch(text);

    private static string FormatLocationLabel(VariableLocation location) => location switch
    {
        VariableLocation.Url => "URL",
        VariableLocation.QueryParams => "Paramètres",
        VariableLocation.Headers => "En-têtes",
        VariableLocation.Body => "Corps",
        VariableLocation.FormFields => "Formulaire",
        _ => location.ToString()
    };

    private static TemplateApplyResult Fail(string? message) => new()
    {
        IsSuccess = false,
        ErrorMessage = message ?? "Échec de substitution des variables."
    };
}
