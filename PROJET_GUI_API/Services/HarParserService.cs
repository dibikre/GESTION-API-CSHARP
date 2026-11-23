using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Serilog;

namespace ApiTester.Services;

public sealed class HarParserService : IHarParserService
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        MaxDepth = 1024,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept-Encoding",
        "Content-Length",
        "Host",
        "Connection",
        "Transfer-Encoding"
    };

    private static readonly HashSet<string> StaticResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "stylesheet",
        "image",
        "font",
        "media",
        "script",
        "manifest",
        "imageset"
    };

    private static readonly string[] StaticExtensions =
    [
        ".css", ".js", ".mjs", ".cjs", ".map",
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".webp", ".svg", ".ico", ".avif", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp4", ".webm", ".mp3", ".ogg", ".wav"
    ];

    public async Task<IStorageFile?> PickHarFileAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            var topLevel = GetMainTopLevel();
            var storageProvider = topLevel?.StorageProvider;
            if (storageProvider is null || !storageProvider.CanOpen)
            {
                Log.Warning("Le fournisseur de stockage Avalonia ne permet pas l'ouverture de fichier HAR.");
                return null;
            }

            var options = new FilePickerOpenOptions
            {
                Title = "Importer un fichier HAR (.har)",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Fichier HTTP Archive (*.har)")
                    {
                        Patterns = ["*.har"]
                    },
                    new FilePickerFileType("Tous les fichiers (*.*)")
                    {
                        Patterns = ["*.*"]
                    }
                ]
            };

            var files = await storageProvider.OpenFilePickerAsync(options);
            return files.Count > 0 ? files[0] : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Échec lors de l'ouverture du sélecteur de fichier HAR ({ErrorType}).", ex.GetType().Name);
            return null;
        }
    }

    public async Task<HarParseResult> ParseFileAsync(IStorageFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var localPath = file.TryGetLocalPath()
            ?? (file.Path is { IsAbsoluteUri: true, IsFile: true } uri ? uri.LocalPath : null);
        var displayName = !string.IsNullOrWhiteSpace(localPath)
            ? Path.GetFileName(localPath)
            : file.Name;

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "archive.har";
        }

        try
        {
            string content;
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                await using var fileStream = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 16384,
                    useAsync: true);
                using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                content = await reader.ReadToEndAsync(ct);
            }
            else
            {
                await using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                content = await reader.ReadToEndAsync(ct);
            }

            return ParseContent(content, displayName);
        }
        catch (OperationCanceledException)
        {
            return HarParseResult.Failure(displayName, "Lecture du fichier HAR annulée.");
        }
        catch (Exception ex) when (IsFileSystemReadException(ex))
        {
            Log.Error(ex, "Impossible de lire le fichier HAR {FileName} ({ErrorType}).", displayName, ex.GetType().Name);
            return HarParseResult.Failure(displayName, $"Impossible de lire le fichier « {displayName} » : {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erreur inattendue lors de la lecture du fichier HAR {FileName} ({ErrorType}).", displayName, ex.GetType().Name);
            return HarParseResult.Failure(displayName, $"Erreur lors de la lecture du fichier « {displayName} » : {ex.Message}");
        }
    }

    public HarParseResult ParseContent(string jsonContent, string fileName)
    {
        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "archive.har" : fileName.Trim();
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            return HarParseResult.Failure(safeFileName, "Le fichier HAR est vide.");
        }

        try
        {
            using var document = JsonDocument.Parse(jsonContent.TrimStart('\uFEFF'), JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return HarParseResult.Failure(safeFileName, "Format HAR invalide : l'objet racine JSON est attendu.");
            }

            if (!TryGetEntriesElement(root, out var entriesElement))
            {
                return HarParseResult.Failure(safeFileName, "Structure HAR invalide : le tableau « log.entries » est introuvable.");
            }

            var parsedEntries = new List<HarEntryItem>();
            var ignoredNonHttpCount = 0;
            var ignoredInvalidCount = 0;
            var index = 0;

            foreach (var entry in entriesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(entry, "request", out var requestElem) ||
                    requestElem.ValueKind != JsonValueKind.Object)
                {
                    ignoredInvalidCount++;
                    continue;
                }

                var rawUrl = GetString(requestElem, "url")?.Trim();
                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    ignoredInvalidCount++;
                    continue;
                }

                var fragmentIndex = rawUrl.IndexOf('#');
                var urlWithoutFragment = fragmentIndex >= 0 ? rawUrl[..fragmentIndex] : rawUrl;

                if (!Uri.TryCreate(urlWithoutFragment, UriKind.Absolute, out var parsedUri) ||
                    (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
                {
                    ignoredNonHttpCount++;
                    continue;
                }

                index++;
                var item = BuildEntryItem(index, entry, requestElem, rawUrl, urlWithoutFragment, parsedUri);
                parsedEntries.Add(item);
            }

            if (parsedEntries.Count == 0)
            {
                var reason = ignoredNonHttpCount > 0
                    ? $"Aucune requête HTTP/HTTPS trouvée dans « {safeFileName} » ({ignoredNonHttpCount} entrée(s) non HTTP ignorée(s))."
                    : $"Aucune requête trouvée dans « {safeFileName} ».";
                return HarParseResult.Failure(safeFileName, reason);
            }

            Log.Information("Fichier HAR {FileName} analysé : {Count} requêtes HTTP/HTTPS extraites ({IgnoredNonHttp} non-HTTP, {IgnoredInvalid} invalides).",
                safeFileName, parsedEntries.Count, ignoredNonHttpCount, ignoredInvalidCount);

            return HarParseResult.Success(safeFileName, parsedEntries, ignoredNonHttpCount, ignoredInvalidCount);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Fichier HAR invalide ({FileName}) : erreur JSON.", safeFileName);
            return HarParseResult.Failure(safeFileName, $"Le fichier « {safeFileName} » n'est pas un JSON HAR valide : {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erreur inattendue lors de l'analyse du fichier HAR {FileName} ({ErrorType}).", safeFileName, ex.GetType().Name);
            return HarParseResult.Failure(safeFileName, $"Erreur lors de l'analyse du fichier HAR : {ex.Message}");
        }
    }

    private static HarEntryItem BuildEntryItem(
        int index,
        JsonElement entry,
        JsonElement requestElem,
        string rawUrl,
        string urlWithoutFragment,
        Uri parsedUri)
    {
        var warnings = new List<string>();

        var originalMethod = (GetString(requestElem, "method") ?? "GET").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(originalMethod))
        {
            originalMethod = "GET";
        }

        var method = ParseHttpMethod(originalMethod, warnings);

        // URL de base et paramètres de requête
        var queryIndex = urlWithoutFragment.IndexOf('?');
        var baseUrl = queryIndex >= 0 ? urlWithoutFragment[..queryIndex] : urlWithoutFragment;
        var queryParams = ExtractQueryParams(requestElem, urlWithoutFragment, queryIndex);

        // En-têtes et cookies
        var headers = ExtractHeadersAndCookies(requestElem);

        // Corps (postData)
        var (bodyType, body, formFields) = ExtractPostData(requestElem, headers, warnings);

        // Métadonnées de réponse et type de ressource
        var responseStatus = ExtractResponseStatus(entry);
        var responseMime = ExtractResponseMimeType(entry);
        var resourceType = (GetString(entry, "_resourceType") ?? string.Empty).Trim();
        var isStaticAsset = DetectStaticAsset(resourceType, responseMime, parsedUri.AbsolutePath);
        var responseFormatTag = DetectResponseFormatTag(responseMime, resourceType, parsedUri.AbsolutePath);

        return new HarEntryItem
        {
            Index = index,
            OriginalMethod = originalMethod,
            Method = method,
            FullUrl = rawUrl,
            BaseUrl = baseUrl,
            StepName = BuildStepName(parsedUri),
            ResponseStatus = responseStatus,
            ResponseMimeType = responseMime,
            ResponseFormatTag = responseFormatTag,
            ResourceType = resourceType,
            IsStaticAsset = isStaticAsset,
            QueryParams = queryParams,
            Headers = headers,
            BodyType = bodyType,
            Body = body,
            FormFields = formFields,
            Warnings = warnings
        };
    }

    private static HttpMethodType ParseHttpMethod(string method, List<string> warnings)
    {
        return method switch
        {
            "GET" => HttpMethodType.Get,
            "POST" => HttpMethodType.Post,
            "PUT" => HttpMethodType.Put,
            "PATCH" => HttpMethodType.Patch,
            "DELETE" => HttpMethodType.Delete,
            _ => AddUnsupportedMethodWarning(method, warnings)
        };
    }

    private static HttpMethodType AddUnsupportedMethodWarning(string method, List<string> warnings)
    {
        warnings.Add($"Méthode HTTP « {method} » non prise en charge ; GET sera utilisée par défaut.");
        return HttpMethodType.Get;
    }

    private static List<KeyValueItem> ExtractQueryParams(
        JsonElement requestElem,
        string urlWithoutFragment,
        int queryIndex)
    {
        var queryParams = new List<KeyValueItem>();

        if (TryGetProperty(requestElem, "queryString", out var qsElem) &&
            qsElem.ValueKind == JsonValueKind.Array &&
            qsElem.GetArrayLength() > 0)
        {
            foreach (var item in qsElem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(item, "name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var value = GetString(item, "value") ?? string.Empty;
                queryParams.Add(new KeyValueItem(name, value, true));
            }

            if (queryParams.Count > 0)
            {
                return queryParams;
            }
        }

        if (queryIndex >= 0 && queryIndex + 1 < urlWithoutFragment.Length)
        {
            ParseEncodedPairsInto(urlWithoutFragment[(queryIndex + 1)..], queryParams);
        }

        return queryParams;
    }

    private static List<KeyValueItem> ExtractHeadersAndCookies(JsonElement requestElem)
    {
        var headers = new List<KeyValueItem>();
        var cookieHeaderValues = new List<string>();

        if (TryGetProperty(requestElem, "headers", out var headersElem) &&
            headersElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var header in headersElem.EnumerateArray())
            {
                if (header.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(header, "name")?.Trim();
                if (string.IsNullOrEmpty(name) || name.StartsWith(':'))
                {
                    continue;
                }

                if (IgnoredHeaders.Contains(name))
                {
                    continue;
                }

                var value = GetString(header, "value") ?? string.Empty;

                if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        cookieHeaderValues.Add(value.Trim().TrimEnd(';'));
                    }
                    continue;
                }

                headers.Add(new KeyValueItem(name, value, true));
            }
        }

        if (cookieHeaderValues.Count > 0)
        {
            headers.Add(new KeyValueItem("Cookie", string.Join("; ", cookieHeaderValues), true));
        }
        else if (TryGetProperty(requestElem, "cookies", out var cookiesElem) &&
                 cookiesElem.ValueKind == JsonValueKind.Array)
        {
            var cookiePairs = new List<string>();
            foreach (var cookie in cookiesElem.EnumerateArray())
            {
                if (cookie.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(cookie, "name")?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var value = GetString(cookie, "value") ?? string.Empty;
                cookiePairs.Add($"{name}={value}");
            }

            if (cookiePairs.Count > 0)
            {
                headers.Add(new KeyValueItem("Cookie", string.Join("; ", cookiePairs), true));
            }
        }

        return headers;
    }

    private static (BodyType BodyType, string Body, List<KeyValueItem> FormFields) ExtractPostData(
        JsonElement requestElem,
        List<KeyValueItem> headers,
        List<string> warnings)
    {
        if (!TryGetProperty(requestElem, "postData", out var postDataElem) ||
            postDataElem.ValueKind != JsonValueKind.Object)
        {
            return (BodyType.None, string.Empty, []);
        }

        var mimeType = (GetString(postDataElem, "mimeType") ?? string.Empty).Trim();
        var headerContentType = headers
            .LastOrDefault(h => h.IsEnabled && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var effectiveMime = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType
            : headerContentType ?? string.Empty;

        var rawText = GetString(postDataElem, "text") ?? string.Empty;
        var encoding = GetString(postDataElem, "encoding");
        if (!string.IsNullOrEmpty(rawText) &&
            string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(rawText);
                rawText = Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                warnings.Add("Le corps encodé en base64 n'a pas pu être décodé en UTF-8 ; texte brut conservé.");
            }
        }

        var rawParams = new List<(string Name, string Value, string? FileName, string? ContentType)>();
        if (TryGetProperty(postDataElem, "params", out var paramsElem) &&
            paramsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in paramsElem.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(p, "name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                rawParams.Add((
                    name,
                    GetString(p, "value") ?? string.Empty,
                    GetString(p, "fileName"),
                    GetString(p, "contentType")));
            }
        }

        if (effectiveMime.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var formFields = new List<KeyValueItem>();
            if (rawParams.Count > 0)
            {
                foreach (var p in rawParams)
                {
                    formFields.Add(new KeyValueItem(p.Name, p.Value, true));
                }
            }
            else if (!string.IsNullOrEmpty(rawText))
            {
                ParseEncodedPairsInto(rawText, formFields);
            }

            if (formFields.Count == 0 && string.IsNullOrEmpty(rawText))
            {
                return (BodyType.None, string.Empty, []);
            }

            EnsureContentTypeHeader(headers, mimeType);
            return (BodyType.FormUrlEncoded, rawText, formFields);
        }

        if (effectiveMime.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(rawText))
            {
                return (BodyType.None, string.Empty, []);
            }

            EnsureContentTypeHeader(headers, mimeType);
            return (BodyType.Json, rawText, []);
        }

        if (effectiveMime.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(rawText))
            {
                EnsureContentTypeHeader(headers, mimeType);
                return (BodyType.Text, rawText, []);
            }

            if (rawParams.Count > 0)
            {
                var (reconstructed, contentTypeWithBoundary) = BuildMultipartBody(rawParams, effectiveMime);
                SetOrAddContentTypeHeader(headers, contentTypeWithBoundary);
                return (BodyType.Text, reconstructed, []);
            }

            return (BodyType.None, string.Empty, []);
        }

        if (!string.IsNullOrEmpty(rawText))
        {
            var trimmed = rawText.TrimStart();
            var detectedType = string.IsNullOrWhiteSpace(effectiveMime) &&
                               (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                ? BodyType.Json
                : BodyType.Text;
            EnsureContentTypeHeader(headers, mimeType);
            return (detectedType, rawText, []);
        }

        if (rawParams.Count > 0)
        {
            var formFields = rawParams
                .Select(p => new KeyValueItem(p.Name, p.Value, true))
                .ToList();
            EnsureContentTypeHeader(headers, mimeType);
            return (BodyType.FormUrlEncoded, string.Empty, formFields);
        }

        return (BodyType.None, string.Empty, []);
    }

    private static void EnsureContentTypeHeader(List<KeyValueItem> headers, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return;
        }

        var exists = headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            headers.Add(new KeyValueItem("Content-Type", mimeType, true));
        }
    }

    private static void SetOrAddContentTypeHeader(List<KeyValueItem> headers, string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return;
        }

        var existing = headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Value = contentType;
        }
        else
        {
            headers.Add(new KeyValueItem("Content-Type", contentType, true));
        }
    }

    private static (string Body, string ContentTypeHeader) BuildMultipartBody(
        IReadOnlyList<(string Name, string Value, string? FileName, string? ContentType)> parameters,
        string effectiveMime)
    {
        var boundary = ExtractBoundary(effectiveMime);
        var contentTypeHeader = effectiveMime;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            boundary = $"----ApiTesterBoundary{Guid.NewGuid():N}"[..34];
            contentTypeHeader = $"multipart/form-data; boundary={boundary}";
        }

        var sb = new StringBuilder();
        foreach (var p in parameters)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            if (!string.IsNullOrEmpty(p.FileName))
            {
                sb.Append($"Content-Disposition: form-data; name=\"{EscapeQuoted(p.Name)}\"; filename=\"{EscapeQuoted(p.FileName)}\"\r\n");
                if (!string.IsNullOrWhiteSpace(p.ContentType))
                {
                    sb.Append($"Content-Type: {p.ContentType}\r\n");
                }
            }
            else
            {
                sb.Append($"Content-Disposition: form-data; name=\"{EscapeQuoted(p.Name)}\"\r\n");
            }

            sb.Append("\r\n").Append(p.Value).Append("\r\n");
        }

        sb.Append("--").Append(boundary).Append("--\r\n");
        return (sb.ToString(), contentTypeHeader);
    }

    private static string? ExtractBoundary(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        foreach (var part in contentType.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["boundary=".Length..].Trim();
                if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                {
                    value = value[1..^1];
                }

                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static string EscapeQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ParseEncodedPairsInto(string encodedPairs, List<KeyValueItem> target)
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

    private static int? ExtractResponseStatus(JsonElement entry)
    {
        if (TryGetProperty(entry, "response", out var respElem) &&
            respElem.ValueKind == JsonValueKind.Object &&
            TryGetProperty(respElem, "status", out var statusElem) &&
            statusElem.ValueKind == JsonValueKind.Number &&
            statusElem.TryGetInt32(out var status) &&
            status > 0)
        {
            return status;
        }

        return null;
    }

    private static string ExtractResponseMimeType(JsonElement entry)
    {
        if (!TryGetProperty(entry, "response", out var respElem) ||
            respElem.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var rawMime = string.Empty;
        if (TryGetProperty(respElem, "content", out var contentElem) &&
            contentElem.ValueKind == JsonValueKind.Object)
        {
            rawMime = GetString(contentElem, "mimeType") ?? string.Empty;
        }

        var normalized = NormalizeMimeType(rawMime);
        if (!string.IsNullOrEmpty(normalized) &&
            !normalized.Equals("x-unknown", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (TryGetProperty(respElem, "headers", out var headersElem) &&
            headersElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var header in headersElem.EnumerateArray())
            {
                if (header.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(header, "name")?.Trim();
                if (name is not null && name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    var headerMime = NormalizeMimeType(GetString(header, "value"));
                    if (!string.IsNullOrEmpty(headerMime))
                    {
                        return headerMime;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeMimeType(string? rawMime)
    {
        if (string.IsNullOrWhiteSpace(rawMime))
        {
            return string.Empty;
        }

        var semicolonIndex = rawMime.IndexOf(';');
        var baseMime = semicolonIndex >= 0 ? rawMime[..semicolonIndex] : rawMime;
        return baseMime.Trim().ToLowerInvariant();
    }

    private static string ExtractUrlExtension(string urlPath)
    {
        if (string.IsNullOrWhiteSpace(urlPath))
        {
            return string.Empty;
        }

        var lastSlash = urlPath.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? urlPath[(lastSlash + 1)..] : urlPath;
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot < 0 || lastDot == fileName.Length - 1)
        {
            return string.Empty;
        }

        return fileName[lastDot..].ToLowerInvariant();
    }

    private static string DetectResponseFormatTag(string responseMime, string resourceType, string urlPath)
    {
        var ext = ExtractUrlExtension(urlPath);
        if (ext == ".map")
        {
            return "MAP";
        }

        if (!string.IsNullOrEmpty(responseMime))
        {
            if (responseMime.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                responseMime.Contains("ecmascript", StringComparison.OrdinalIgnoreCase))
            {
                return "JS";
            }

            if (responseMime == "text/css")
            {
                return "CSS";
            }

            if (responseMime is "image/jpeg" or "image/jpg" or "image/pjpeg")
            {
                return "JPG";
            }

            if (responseMime == "image/png")
            {
                return "PNG";
            }

            if (responseMime == "image/gif")
            {
                return "GIF";
            }

            if (responseMime == "image/svg+xml")
            {
                return "SVG";
            }

            if (responseMime is "image/webp" or "image/avif")
            {
                return "WEBP";
            }

            if (responseMime is "image/x-icon" or "image/vnd.microsoft.icon")
            {
                return "ICO";
            }

            if (responseMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return "IMG";
            }

            if (responseMime.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
                responseMime.Contains("font", StringComparison.OrdinalIgnoreCase))
            {
                return "FONT";
            }

            if (responseMime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                responseMime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                return "MEDIA";
            }

            if (responseMime is "text/html" or "application/xhtml+xml")
            {
                return "HTML";
            }

            if (responseMime is "application/json" or "text/json" ||
                responseMime.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            {
                return "JSON";
            }

            if (responseMime is "application/xml" or "text/xml" ||
                responseMime.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
            {
                return "XML";
            }

            if (responseMime == "application/wasm")
            {
                return "WASM";
            }

            if (responseMime == "application/pdf")
            {
                return "PDF";
            }

            if (responseMime == "text/event-stream")
            {
                return "SSE";
            }
        }

        if (!string.IsNullOrEmpty(ext))
        {
            var tagFromExtension = ext switch
            {
                ".js" or ".mjs" or ".cjs" => "JS",
                ".css" or ".scss" => "CSS",
                ".jpg" or ".jpeg" or ".jfif" => "JPG",
                ".png" => "PNG",
                ".gif" => "GIF",
                ".svg" => "SVG",
                ".webp" or ".avif" => "WEBP",
                ".ico" => "ICO",
                ".bmp" or ".tif" or ".tiff" => "IMG",
                ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot" => "FONT",
                ".mp4" or ".webm" or ".mp3" or ".ogg" or ".wav" or ".m4a" or ".m3u8" => "MEDIA",
                ".html" or ".htm" => "HTML",
                ".json" => "JSON",
                ".xml" => "XML",
                ".wasm" => "WASM",
                ".pdf" => "PDF",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(tagFromExtension))
            {
                return tagFromExtension;
            }
        }

        if (responseMime == "text/plain")
        {
            return "TEXT";
        }

        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            return resourceType.ToLowerInvariant() switch
            {
                "script" => "JS",
                "stylesheet" => "CSS",
                "image" or "imageset" => "IMG",
                "font" => "FONT",
                "media" => "MEDIA",
                "document" => "HTML",
                "manifest" => "JSON",
                _ => string.Empty
            };
        }

        return string.Empty;
    }

    private static bool DetectStaticAsset(string resourceType, string responseMime, string urlPath)
    {
        if (!string.IsNullOrWhiteSpace(resourceType) && StaticResourceTypes.Contains(resourceType))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(urlPath))
        {
            foreach (var ext in StaticExtensions)
            {
                if (urlPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(responseMime))
        {
            if (responseMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                responseMime.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
                responseMime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                responseMime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                responseMime.Contains("text/css", StringComparison.OrdinalIgnoreCase) ||
                responseMime.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                responseMime.Contains("ecmascript", StringComparison.OrdinalIgnoreCase) ||
                responseMime == "application/wasm")
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildStepName(Uri uri)
    {
        var path = uri.AbsolutePath;
        var candidate = string.IsNullOrEmpty(path) || path == "/"
            ? uri.Host
            : path;

        const int maxLength = 56;
        if (candidate.Length > maxLength)
        {
            candidate = "..." + candidate[^(maxLength - 3)..];
        }

        return candidate;
    }

    private static bool TryGetEntriesElement(JsonElement root, out JsonElement entriesElement)
    {
        if (TryGetProperty(root, "log", out var logElem) &&
            logElem.ValueKind == JsonValueKind.Object &&
            TryGetProperty(logElem, "entries", out entriesElement) &&
            entriesElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (TryGetProperty(root, "entries", out entriesElement) &&
            entriesElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        entriesElement = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!TryGetProperty(obj, name, out var elem))
        {
            return null;
        }

        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool IsFileSystemReadException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or PathTooLongException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException;

    private static TopLevel? GetMainTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } mainWindow)
        {
            return TopLevel.GetTopLevel(mainWindow);
        }

        return null;
    }
}
