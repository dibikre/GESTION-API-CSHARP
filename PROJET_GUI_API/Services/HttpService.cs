using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using Serilog;

namespace ApiTester.Services;

public class HttpService : IHttpService
{
    private readonly HttpClient _httpClient;

    public HttpService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryBuildUri(request, out var finalUri, out var urlError))
        {
            Log.Error("URL invalide fournie pour la requête {Method} : {Error}", request.Method, urlError);
            return new ResponseModel
            {
                IsSuccess = false,
                ErrorMessage = urlError
            };
        }

        var safeLogUrl = SanitizeUrlForLog(finalUri, request.Auth);
        var timeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 30;
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var httpRequest = new HttpRequestMessage(ToHttpMethod(request.Method), finalUri);
            httpRequest.Content = BuildContent(request);

            ApplyHeaders(httpRequest, request.Headers);
            ApplyAuth(httpRequest, request.Auth);

            Log.Information("Envoi de la requête {Method} vers {Url} (timeout: {TimeoutSeconds}s).",
                request.Method, safeLogUrl, timeoutSeconds);

            using var httpResponse = await _httpClient.SendAsync(httpRequest, linkedCts.Token).ConfigureAwait(false);
            var responseBody = await httpResponse.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var responseHeaders = ExtractResponseHeaders(httpResponse);
            var contentType = httpResponse.Content.Headers.ContentType?.ToString() ?? string.Empty;
            var sizeBytes = httpResponse.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(responseBody);

            Log.Information("Réponse reçue pour {Method} {Url} : {StatusCode} ({DurationMs} ms, {SizeBytes} octets).",
                request.Method, safeLogUrl, (int)httpResponse.StatusCode, stopwatch.ElapsedMilliseconds, sizeBytes);

            return new ResponseModel
            {
                StatusCode = (int)httpResponse.StatusCode,
                ReasonPhrase = httpResponse.ReasonPhrase ?? string.Empty,
                IsSuccess = httpResponse.IsSuccessStatusCode,
                DurationMs = stopwatch.ElapsedMilliseconds,
                SizeBytes = sizeBytes,
                ContentType = contentType,
                Headers = responseHeaders,
                Body = responseBody,
                ErrorMessage = null
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            Log.Information("Requête {Method} {Url} annulée par l'utilisateur après {DurationMs} ms.",
                request.Method, safeLogUrl, stopwatch.ElapsedMilliseconds);
            return new ResponseModel
            {
                IsSuccess = false,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = "The request was cancelled."
            };
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            Log.Error(ex, "Délai d'attente dépassé ({TimeoutSeconds}s) pour {Method} {Url}.",
                timeoutSeconds, request.Method, safeLogUrl);
            return new ResponseModel
            {
                IsSuccess = false,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = $"Request timed out after {timeoutSeconds} seconds."
            };
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "Requête {Method} {Url} interrompue après {DurationMs} ms.",
                request.Method, safeLogUrl, stopwatch.ElapsedMilliseconds);
            return new ResponseModel
            {
                IsSuccess = false,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = "The request was cancelled or timed out."
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var details = ex.InnerException is not null
                ? $"{ex.Message} ({ex.InnerException.Message})"
                : ex.Message;

            Log.Error(ex, "Erreur réseau lors de la requête {Method} {Url} : {Details}",
                request.Method, safeLogUrl, details);
            return new ResponseModel
            {
                IsSuccess = false,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = $"Network error: {details}"
            };
        }
        catch (InvalidOperationException ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "Configuration invalide pour la requête {Method} {Url}.",
                request.Method, safeLogUrl);
            return new ResponseModel
            {
                IsSuccess = false,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = $"Invalid request configuration: {ex.Message}"
            };
        }
    }

    private static bool TryBuildUri(RequestModel request, out Uri finalUri, out string errorMessage)
    {
        finalUri = null!;
        errorMessage = string.Empty;

        var rawUrl = request.Url?.Trim();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            errorMessage = "URL cannot be empty.";
            return false;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            errorMessage = "Invalid URL. Please provide an absolute HTTP or HTTPS URL.";
            return false;
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

            foreach (var param in request.QueryParams)
            {
                if (!param.IsEnabled || string.IsNullOrWhiteSpace(param.Key))
                {
                    continue;
                }

                var encodedKey = Uri.EscapeDataString(param.Key.Trim());
                var encodedValue = Uri.EscapeDataString(param.Value ?? string.Empty);
                queryParts.Add($"{encodedKey}={encodedValue}");
            }

            if (request.Auth.Type == AuthType.ApiKey &&
                !request.Auth.ApiKeyInHeader &&
                !string.IsNullOrWhiteSpace(request.Auth.ApiKeyName))
            {
                var encodedKey = Uri.EscapeDataString(request.Auth.ApiKeyName.Trim());
                var encodedValue = Uri.EscapeDataString(request.Auth.ApiKeyValue ?? string.Empty);
                queryParts.Add($"{encodedKey}={encodedValue}");
            }

            uriBuilder.Query = queryParts.Count > 0 ? string.Join("&", queryParts) : string.Empty;
            finalUri = uriBuilder.Uri;
            return true;
        }
        catch (UriFormatException ex)
        {
            errorMessage = $"Invalid URL format: {ex.Message}";
            return false;
        }
    }

    private static HttpMethod ToHttpMethod(HttpMethodType method) => method switch
    {
        HttpMethodType.Get => HttpMethod.Get,
        HttpMethodType.Post => HttpMethod.Post,
        HttpMethodType.Put => HttpMethod.Put,
        HttpMethodType.Patch => HttpMethod.Patch,
        HttpMethodType.Delete => HttpMethod.Delete,
        _ => HttpMethod.Get
    };

    private static HttpContent? BuildContent(RequestModel request)
    {
        switch (request.BodyType)
        {
            case BodyType.None:
                return null;

            case BodyType.Json:
                return new StringContent(request.Body ?? string.Empty, Encoding.UTF8, "application/json");

            case BodyType.Text:
            {
                var body = request.Body ?? string.Empty;
                var contentType = request.Headers
                    .LastOrDefault(h => h.IsEnabled && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    ?.Value;

                if (contentType is not null &&
                    contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    body = body
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal)
                        .Replace("\n", "\r\n", StringComparison.Ordinal);
                }

                return new StringContent(body, Encoding.UTF8, "text/plain");
            }

            case BodyType.FormUrlEncoded:
                return new FormUrlEncodedContent(
                    request.FormFields
                        .Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.Key))
                        .Select(f => new KeyValuePair<string, string>(f.Key.Trim(), f.Value ?? string.Empty)));

            default:
                return null;
        }
    }

    private static void ApplyHeaders(HttpRequestMessage httpRequest, IEnumerable<KeyValueItem> headers)
    {
        foreach (var header in headers)
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

            var value = header.Value ?? string.Empty;

            try
            {
                if (httpRequest.Headers.TryAddWithoutValidation(key, value))
                {
                    continue;
                }

                if (httpRequest.Content is not null)
                {
                    httpRequest.Content.Headers.Remove(key);
                    if (httpRequest.Content.Headers.TryAddWithoutValidation(key, value))
                    {
                        continue;
                    }
                }

                Log.Warning("En-tête HTTP ignoré ou refusé par .NET : {HeaderName}.", key);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                Log.Warning("En-tête HTTP refusé par .NET : {HeaderName} ({Reason}).", key, ex.Message);
            }
        }
    }

    private static void ApplyAuth(HttpRequestMessage httpRequest, AuthConfig auth)
    {
        switch (auth.Type)
        {
            case AuthType.None:
                break;

            case AuthType.Bearer:
                if (!string.IsNullOrWhiteSpace(auth.Token))
                {
                    httpRequest.Headers.Remove("Authorization");
                    httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.Token.Trim()}");
                }
                break;

            case AuthType.Basic:
                var rawCredentials = $"{auth.Username ?? string.Empty}:{auth.Password ?? string.Empty}";
                var base64Credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials));
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Credentials);
                break;

            case AuthType.ApiKey:
                if (auth.ApiKeyInHeader && !string.IsNullOrWhiteSpace(auth.ApiKeyName))
                {
                    var keyName = auth.ApiKeyName.Trim();
                    httpRequest.Headers.Remove(keyName);
                    httpRequest.Headers.TryAddWithoutValidation(keyName, auth.ApiKeyValue ?? string.Empty);
                }
                break;
        }
    }

    private static List<KeyValueItem> ExtractResponseHeaders(HttpResponseMessage httpResponse)
    {
        var result = new List<KeyValueItem>();

        foreach (var header in httpResponse.Headers)
        {
            result.Add(new KeyValueItem(header.Key, string.Join(", ", header.Value)));
        }

        foreach (var header in httpResponse.Content.Headers)
        {
            result.Add(new KeyValueItem(header.Key, string.Join(", ", header.Value)));
        }

        return result;
    }

    private static string SanitizeUrlForLog(Uri uri, AuthConfig auth)
    {
        if (auth.Type != AuthType.ApiKey ||
            auth.ApiKeyInHeader ||
            string.IsNullOrWhiteSpace(auth.ApiKeyName) ||
            string.IsNullOrEmpty(uri.Query))
        {
            return uri.ToString();
        }

        var encodedSecretKey = Uri.EscapeDataString(auth.ApiKeyName.Trim());
        var segments = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var sanitizedSegments = segments.Select(segment =>
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex < 0)
            {
                return segment;
            }

            var keyPart = segment[..equalsIndex];
            return string.Equals(keyPart, encodedSecretKey, StringComparison.OrdinalIgnoreCase)
                ? $"{keyPart}=***"
                : segment;
        });

        var builder = new UriBuilder(uri)
        {
            Query = string.Join("&", sanitizedSegments)
        };

        return builder.Uri.ToString();
    }
}
