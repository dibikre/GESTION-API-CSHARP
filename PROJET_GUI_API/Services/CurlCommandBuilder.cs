using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ApiTester.Models;

namespace ApiTester.Services;

public static class CurlCommandBuilder
{
    public static string Build(RequestModel request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parts = new List<string>
        {
            "curl",
            "-X",
            ToHttpMethodToken(request.Method),
            QuotePosix(BuildUnmaskedUrl(request))
        };

        var hasContentTypeHeader = false;
        string? explicitContentType = null;

        var overridesAuthorization = request.Auth.Type is AuthType.Bearer or AuthType.Basic;
        var apiKeyHeaderName = request.Auth.Type == AuthType.ApiKey &&
                               request.Auth.ApiKeyInHeader &&
                               !string.IsNullOrWhiteSpace(request.Auth.ApiKeyName)
            ? request.Auth.ApiKeyName.Trim()
            : null;

        foreach (var header in request.Headers)
        {
            if (!header.IsEnabled || string.IsNullOrWhiteSpace(header.Key))
            {
                continue;
            }

            var key = header.Key.Trim();
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (overridesAuthorization && key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (apiKeyHeaderName is not null && key.Equals(apiKeyHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Value ?? string.Empty;
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                hasContentTypeHeader = true;
                explicitContentType = value;
            }

            parts.Add("-H");
            parts.Add(QuotePosix($"{key}: {value}"));
        }

        AppendAuthHeader(parts, request.Auth);
        AppendBody(parts, request, hasContentTypeHeader, explicitContentType);

        return string.Join(" ", parts);
    }

    private static string ToHttpMethodToken(HttpMethodType method) => method switch
    {
        HttpMethodType.Get => "GET",
        HttpMethodType.Post => "POST",
        HttpMethodType.Put => "PUT",
        HttpMethodType.Patch => "PATCH",
        HttpMethodType.Delete => "DELETE",
        _ => "GET"
    };

    private static void AppendAuthHeader(List<string> parts, AuthConfig auth)
    {
        switch (auth.Type)
        {
            case AuthType.Bearer when !string.IsNullOrWhiteSpace(auth.Token):
                parts.Add("-H");
                parts.Add(QuotePosix($"Authorization: Bearer {auth.Token.Trim()}"));
                break;

            case AuthType.Basic:
            {
                var rawCredentials = $"{auth.Username ?? string.Empty}:{auth.Password ?? string.Empty}";
                var base64Credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials));
                parts.Add("-H");
                parts.Add(QuotePosix($"Authorization: Basic {base64Credentials}"));
                break;
            }

            case AuthType.ApiKey when auth.ApiKeyInHeader && !string.IsNullOrWhiteSpace(auth.ApiKeyName):
                parts.Add("-H");
                parts.Add(QuotePosix($"{auth.ApiKeyName.Trim()}: {auth.ApiKeyValue ?? string.Empty}"));
                break;
        }
    }

    private static void AppendBody(
        List<string> parts,
        RequestModel request,
        bool hasContentTypeHeader,
        string? explicitContentType)
    {
        switch (request.BodyType)
        {
            case BodyType.None:
                return;

            case BodyType.Json:
                if (!hasContentTypeHeader)
                {
                    parts.Add("-H");
                    parts.Add(QuotePosix("Content-Type: application/json"));
                }

                if (!string.IsNullOrEmpty(request.Body))
                {
                    parts.Add("--data-raw");
                    parts.Add(QuotePosix(request.Body));
                }
                return;

            case BodyType.Text:
            {
                if (!hasContentTypeHeader)
                {
                    parts.Add("-H");
                    parts.Add(QuotePosix("Content-Type: text/plain"));
                }

                var body = request.Body ?? string.Empty;
                if (explicitContentType is not null &&
                    explicitContentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    body = body
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal)
                        .Replace("\n", "\r\n", StringComparison.Ordinal);
                }

                if (!string.IsNullOrEmpty(body))
                {
                    parts.Add("--data-raw");
                    parts.Add(QuotePosix(body));
                }
                return;
            }

            case BodyType.FormUrlEncoded:
            {
                if (!hasContentTypeHeader)
                {
                    parts.Add("-H");
                    parts.Add(QuotePosix("Content-Type: application/x-www-form-urlencoded"));
                }

                var encodedPairs = request.FormFields
                    .Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.Key))
                    .Select(f => $"{Uri.EscapeDataString(f.Key.Trim())}={Uri.EscapeDataString(f.Value ?? string.Empty)}")
                    .ToList();

                if (encodedPairs.Count > 0)
                {
                    parts.Add("--data-raw");
                    parts.Add(QuotePosix(string.Join("&", encodedPairs)));
                }
                return;
            }
        }
    }

    private static string BuildUnmaskedUrl(RequestModel request)
    {
        var rawUrl = request.Url?.Trim() ?? string.Empty;
        var extraQuerySegments = new List<string>();

        foreach (var param in request.QueryParams)
        {
            if (!param.IsEnabled || string.IsNullOrWhiteSpace(param.Key))
            {
                continue;
            }

            var encodedKey = Uri.EscapeDataString(param.Key.Trim());
            var encodedValue = Uri.EscapeDataString(param.Value ?? string.Empty);
            extraQuerySegments.Add($"{encodedKey}={encodedValue}");
        }

        if (request.Auth.Type == AuthType.ApiKey &&
            !request.Auth.ApiKeyInHeader &&
            !string.IsNullOrWhiteSpace(request.Auth.ApiKeyName))
        {
            var encodedKey = Uri.EscapeDataString(request.Auth.ApiKeyName.Trim());
            var encodedValue = Uri.EscapeDataString(request.Auth.ApiKeyValue ?? string.Empty);
            extraQuerySegments.Add($"{encodedKey}={encodedValue}");
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            if (extraQuerySegments.Count == 0)
            {
                return rawUrl;
            }

            var separator = rawUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return $"{rawUrl}{separator}{string.Join("&", extraQuerySegments)}";
        }

        try
        {
            var uriBuilder = new UriBuilder(baseUri);
            var queryParts = new List<string>();

            var existingQuery = uriBuilder.Query.TrimStart('?');
            if (!string.IsNullOrEmpty(existingQuery))
            {
                queryParts.Add(existingQuery);
            }

            queryParts.AddRange(extraQuerySegments);
            uriBuilder.Query = queryParts.Count > 0 ? string.Join("&", queryParts) : string.Empty;
            return uriBuilder.Uri.ToString();
        }
        catch (UriFormatException)
        {
            return rawUrl;
        }
    }

    private static string QuotePosix(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
