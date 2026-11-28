using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using Serilog;

namespace ApiTester.Services;

public sealed class BatchResultWriter : IBatchResultWriter
{
    private const int MaxFileNameLength = 120;
    private const int JsonParseMaxDepth = 1024;
    private const int JsonWriteMaxDepth = 2048;

    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        MaxDepth = JsonParseMaxDepth
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly byte[] NewLineBytes = "\n"u8.ToArray();

    private static readonly HashSet<char> WindowsInvalidChars =
    [
        '\\', '/', ':', '*', '?', '"', '<', '>', '|'
    ];

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private BatchSaveOptions _options = new();
    private int _indexDigits = 4;
    private bool _isActive;

    private StreamWriter? _csvWriter;
    private string _csvFileName = string.Empty;

    private FileStream? _groupedStream;
    private Utf8JsonWriter? _groupedJsonWriter;
    private bool _groupedJsonArrayStarted;
    private string _groupedFileName = string.Empty;

    public int SavedCount { get; private set; }
    public int ErrorCount { get; private set; }
    public string OutputPath { get; private set; } = string.Empty;

    public async Task BeginAsync(BatchSaveOptions options, int totalRequests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_isActive)
        {
            await CompleteAsync().ConfigureAwait(false);
        }

        SavedCount = 0;
        ErrorCount = 0;
        OutputPath = string.Empty;
        _options = new BatchSaveOptions
        {
            SaveMode = options.SaveMode,
            OutputFolder = options.OutputFolder,
            GroupedFormat = options.GroupedFormat,
            CreateTimestampSubfolder = options.CreateTimestampSubfolder,
            IncludeSummaryCsv = options.IncludeSummaryCsv
        };

        if (options.SaveMode == SaveMode.None)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.OutputFolder))
        {
            throw new InvalidOperationException("Le dossier d'enregistrement ne peut pas être vide.");
        }

        var trimmedFolder = options.OutputFolder.Trim();
        var baseFolder = Path.GetFullPath(trimmedFolder);
        var targetFolder = options.CreateTimestampSubfolder
            ? Path.Combine(baseFolder, $"lot_{DateTime.Now:yyyyMMdd_HHmmss}")
            : baseFolder;

        Directory.CreateDirectory(targetFolder);
        await VerifyDirectoryWritableAsync(targetFolder, ct).ConfigureAwait(false);

        OutputPath = targetFolder;
        var effectiveTotal = Math.Max(1, totalRequests);
        _indexDigits = Math.Max(4, effectiveTotal.ToString(CultureInfo.InvariantCulture).Length);

        try
        {
            if (options.SaveMode is SaveMode.PerRequest or SaveMode.Both && options.IncludeSummaryCsv)
            {
                var csvPath = GetUniqueFilePath(OutputPath, "_resume", ".csv");
                _csvFileName = Path.GetFileName(csvPath);

                var csvStream = new FileStream(csvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                _csvWriter = new StreamWriter(csvStream, Utf8WithBom, bufferSize: 4096, leaveOpen: false);
                await _csvWriter.WriteLineAsync("index;parametres;statut;duree_ms;taille_octets;fichier;erreur;rate_limited;tentatives").ConfigureAwait(false);
                await _csvWriter.FlushAsync(ct).ConfigureAwait(false);
            }

            if (options.SaveMode is SaveMode.Grouped or SaveMode.Both)
            {
                var groupedExt = options.GroupedFormat == GroupedFormat.Json ? ".json" : ".jsonl";
                var groupedPath = GetUniqueFilePath(OutputPath, "lot_responses", groupedExt);
                _groupedFileName = Path.GetFileName(groupedPath);

                _groupedStream = new FileStream(groupedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, useAsync: true);

                if (options.GroupedFormat == GroupedFormat.Json)
                {
                    var writerOptions = new JsonWriterOptions
                    {
                        Indented = true,
                        MaxDepth = JsonWriteMaxDepth,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    _groupedJsonWriter = new Utf8JsonWriter(_groupedStream, writerOptions);
                    _groupedJsonWriter.WriteStartArray();
                    await _groupedJsonWriter.FlushAsync(ct).ConfigureAwait(false);
                    _groupedJsonArrayStarted = true;
                }
            }

            _isActive = true;
            Log.Information("Enregistrement des réponses du lot initialisé dans {OutputPath} (mode: {SaveMode}).",
                OutputPath, options.SaveMode);
        }
        catch
        {
            await DisposeWritersSafeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task WriteAsync(BatchResultItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_isActive || _options.SaveMode == SaveMode.None)
        {
            return;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_isActive)
            {
                return;
            }

            var itemHadWriteError = false;

            if (_options.SaveMode is SaveMode.PerRequest or SaveMode.Both)
            {
                var perRequestOk = await WritePerRequestAsync(item).ConfigureAwait(false);
                if (!perRequestOk)
                {
                    itemHadWriteError = true;
                }
            }

            if (_options.SaveMode is SaveMode.Grouped or SaveMode.Both)
            {
                var groupedOk = await WriteGroupedAsync(item).ConfigureAwait(false);
                if (!groupedOk)
                {
                    itemHadWriteError = true;
                }
            }

            if (itemHadWriteError)
            {
                ErrorCount++;
            }
            else
            {
                SavedCount++;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task CompleteAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isActive && _groupedJsonWriter is null && _groupedStream is null && _csvWriter is null)
            {
                return;
            }

            await DisposeWritersSafeAsync().ConfigureAwait(false);
            _isActive = false;

            Log.Information("Enregistrement du lot terminé dans {OutputPath} ({SavedCount} réussies, {ErrorCount} en échec).",
                OutputPath, SavedCount, ErrorCount);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> WritePerRequestAsync(BatchResultItem item)
    {
        var allOk = true;
        var hasHttpResponse = item.Response is not null &&
                              item.StatusCode > 0 &&
                              string.IsNullOrEmpty(item.ErrorMessage);

        var savedFileName = string.Empty;

        if (hasHttpResponse)
        {
            var candidateName = BuildPerRequestFileName(item);
            try
            {
                var fullPath = GetUniqueFilePathFromFileName(OutputPath, candidateName);
                savedFileName = Path.GetFileName(fullPath);

                await using var fileStream = new FileStream(
                    fullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    8192,
                    useAsync: true);

                await using var writer = new StreamWriter(fileStream, Utf8WithoutBom, bufferSize: 8192, leaveOpen: false);
                await writer.WriteAsync(item.Response!.Body).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileSystemWriteException(ex) || ex is InvalidOperationException)
            {
                allOk = false;
                savedFileName = string.Empty;
                Log.Error(ex, "Échec de l'enregistrement du fichier {FileName} ({ErrorType}).",
                    candidateName, ex.GetType().Name);
            }
        }

        if (_csvWriter is not null)
        {
            try
            {
                var paramsCell = FormatParamsForSummary(item);
                var statusCell = item.StatusCode > 0
                    ? item.StatusCode.ToString(CultureInfo.InvariantCulture)
                    : "Erreur";

                var line = string.Join(";",
                    item.Index.ToString(CultureInfo.InvariantCulture),
                    EscapeCsvField(paramsCell),
                    EscapeCsvField(statusCell),
                    item.DurationMs.ToString(CultureInfo.InvariantCulture),
                    item.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    EscapeCsvField(savedFileName),
                    EscapeCsvField(item.ErrorMessage ?? string.Empty),
                    item.WasRateLimited ? "true" : "false",
                    item.RetryAttempts.ToString(CultureInfo.InvariantCulture));

                await _csvWriter.WriteLineAsync(line).ConfigureAwait(false);
                await _csvWriter.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileSystemWriteException(ex) || ex is InvalidOperationException)
            {
                allOk = false;
                Log.Error(ex, "Échec de l'écriture dans le fichier {FileName} ({ErrorType}).",
                    _csvFileName, ex.GetType().Name);
            }
        }

        return allOk;
    }

    private async Task<bool> WriteGroupedAsync(BatchResultItem item)
    {
        if (_groupedStream is null)
        {
            return false;
        }

        try
        {
            if (_options.GroupedFormat == GroupedFormat.Json)
            {
                if (_groupedJsonWriter is null)
                {
                    return false;
                }

                WriteJsonItemObject(_groupedJsonWriter, item);
                await _groupedJsonWriter.FlushAsync().ConfigureAwait(false);
                return true;
            }

            var buffer = new ArrayBufferWriter<byte>(1024);
            var lineWriterOptions = new JsonWriterOptions
            {
                Indented = false,
                MaxDepth = JsonWriteMaxDepth,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            using (var lineWriter = new Utf8JsonWriter(buffer, lineWriterOptions))
            {
                WriteJsonItemObject(lineWriter, item);
                lineWriter.Flush();
            }

            await _groupedStream.WriteAsync(buffer.WrittenMemory).ConfigureAwait(false);
            await _groupedStream.WriteAsync(NewLineBytes).ConfigureAwait(false);
            await _groupedStream.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsFileSystemWriteException(ex) || ex is JsonException or InvalidOperationException)
        {
            Log.Error(ex, "Échec de l'écriture dans le fichier {FileName} ({ErrorType}).",
                _groupedFileName, ex.GetType().Name);
            return false;
        }
    }

    private static void WriteJsonItemObject(Utf8JsonWriter writer, BatchResultItem item)
    {
        writer.WriteStartObject();

        writer.WriteNumber("index", item.Index);

        writer.WritePropertyName("params");
        writer.WriteStartObject();
        var paramsSource = GetCombinedParamsForOutput(item);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var param in paramsSource)
        {
            if (string.IsNullOrWhiteSpace(param.Key) || !seenKeys.Add(param.Key))
            {
                continue;
            }

            var value = BatchExecutionHelper.MaskIfSensitive(param.Key, param.Value);
            writer.WriteString(param.Key, value);
        }
        writer.WriteEndObject();

        writer.WriteString("url", item.RequestUrl ?? string.Empty);
        writer.WriteString("timestamp", item.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteNumber("statusCode", item.StatusCode);
        writer.WriteNumber("durationMs", item.DurationMs);
        writer.WriteNumber("sizeBytes", item.SizeBytes);
        writer.WriteString("contentType", item.ContentType ?? string.Empty);

        if (string.IsNullOrEmpty(item.ErrorMessage))
        {
            writer.WriteNull("error");
        }
        else
        {
            writer.WriteString("error", item.ErrorMessage);
        }

        var hasHttpResponse = item.Response is not null &&
                              item.StatusCode > 0 &&
                              string.IsNullOrEmpty(item.ErrorMessage);

        if (!hasHttpResponse)
        {
            writer.WriteNull("body");
        }
        else
        {
            var body = item.Response!.Body ?? string.Empty;
            if (IsJsonContentType(item.ContentType) && TryParseJsonDocument(body, out var jsonDoc))
            {
                using (jsonDoc)
                {
                    writer.WritePropertyName("body");
                    jsonDoc.RootElement.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteString("body", body);
            }
        }

        writer.WriteBoolean("rate_limited", item.WasRateLimited);
        writer.WriteNumber("tentatives", item.RetryAttempts);

        if (item.StepIndex > 0)
        {
            writer.WriteNumber("step_index", item.StepIndex);
            writer.WriteString("step_name", item.StepName ?? string.Empty);
            writer.WriteNumber("iteration", item.Iteration);

            writer.WritePropertyName("extracted");
            writer.WriteStartObject();
            if (item.ExtractedValues is not null)
            {
                foreach (var kvp in item.ExtractedValues)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        continue;
                    }

                    writer.WriteString(kvp.Key, BatchExecutionHelper.MaskIfSensitive(kvp.Key, kvp.Value));
                }
            }
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private string BuildPerRequestFileName(BatchResultItem item)
    {
        var indexPart = item.Index.ToString(CultureInfo.InvariantCulture).PadLeft(_indexDigits, '0');
        var statusPart = item.StatusCode > 0
            ? item.StatusCode.ToString(CultureInfo.InvariantCulture)
            : "erreur";
        var extension = GetExtensionFromContentType(item.ContentType);

        var stepPrefixSegment = item.StepIndex > 0 && !string.IsNullOrWhiteSpace(item.StepName)
            ? SanitizeFileNameSegment(item.StepName.Trim())
            : null;

        var combinedAxes = GetCombinedAxisValues(item);
        var varyingSegments = new List<string>(combinedAxes.Count + (stepPrefixSegment is not null ? 1 : 0));
        if (stepPrefixSegment is not null)
        {
            varyingSegments.Add(stepPrefixSegment);
        }

        foreach (var param in combinedAxes)
        {
            if (string.IsNullOrWhiteSpace(param.Key))
            {
                continue;
            }

            var safeKey = SanitizeFileNameSegment(param.Key.Trim());
            var rawVal = BatchExecutionHelper.MaskIfSensitive(param.Key, param.Value);
            var safeVal = SanitizeFileNameSegment(rawVal);
            varyingSegments.Add($"{safeKey}={safeVal}");
        }

        var suffix = $"_{statusPart}";
        const int collisionReserve = 6;
        var maxStemLength = Math.Max(16, MaxFileNameLength - extension.Length - collisionReserve);

        string stem;
        if (varyingSegments.Count > 0)
        {
            var varyingPart = string.Join("_", varyingSegments);
            var fixedLength = indexPart.Length + 1 + suffix.Length;
            var availableForVarying = Math.Max(1, maxStemLength - fixedLength);

            if (varyingPart.Length > availableForVarying)
            {
                varyingPart = varyingPart[..availableForVarying].TrimEnd('_', '.', ' ');
            }

            stem = string.IsNullOrEmpty(varyingPart)
                ? $"{indexPart}{suffix}"
                : $"{indexPart}_{varyingPart}{suffix}";
        }
        else
        {
            stem = $"{indexPart}{suffix}";
        }

        stem = NormalizeWindowsStem(stem);
        return $"{stem}{extension}";
    }

    private static string SanitizeFileNameSegment(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "_";
        }

        var osInvalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);

        foreach (var ch in input)
        {
            if (ch < 32 || WindowsInvalidChars.Contains(ch) || Array.IndexOf(osInvalidChars, ch) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var sanitized = sb.ToString().TrimEnd(' ', '.');
        return string.IsNullOrEmpty(sanitized) ? "_" : sanitized;
    }

    private static string NormalizeWindowsStem(string stem)
    {
        var cleaned = SanitizeFileNameSegment(stem).TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(cleaned))
        {
            cleaned = "reponse";
        }

        var dotIndex = cleaned.IndexOf('.');
        var baseName = dotIndex >= 0 ? cleaned[..dotIndex] : cleaned;
        if (WindowsReservedNames.Contains(baseName))
        {
            cleaned = $"_{cleaned}";
        }

        return cleaned;
    }

    private static string GetExtensionFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return ".txt";
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return ".json";
        }

        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return ".html";
        }

        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return ".xml";
        }

        if (contentType.Contains("csv", StringComparison.OrdinalIgnoreCase))
        {
            return ".csv";
        }

        return ".txt";
    }

    private static bool IsJsonContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJsonDocument(string body, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(body.TrimStart('\uFEFF'), JsonParseOptions);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static List<KeyValueItem> GetCombinedAxisValues(BatchResultItem item)
    {
        if (item.OrderedAxisValues.Count > 0)
        {
            return [.. item.OrderedAxisValues];
        }

        var combined = new List<KeyValueItem>(item.VaryingParams.Count + item.VariableValues.Count);
        combined.AddRange(item.VaryingParams);
        combined.AddRange(item.VariableValues);
        return combined;
    }

    private static IReadOnlyList<KeyValueItem> GetCombinedParamsForOutput(BatchResultItem item)
    {
        var combined = GetCombinedAxisValues(item);
        return combined.Count > 0 ? combined : item.QueryParams;
    }

    private static string FormatParamsForSummary(BatchResultItem item)
    {
        var source = GetCombinedParamsForOutput(item);
        if (source.Count == 0)
        {
            return "(aucun paramètre)";
        }

        return string.Join(", ", source.Select(p =>
        {
            var val = BatchExecutionHelper.MaskIfSensitive(p.Key, p.Value);
            return $"{p.Key}={val}";
        }));
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuotes = value.IndexOfAny([';', '"', '\r', '\n']) >= 0;
        if (!needsQuotes)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string GetUniqueFilePath(string folder, string stem, string extension)
    {
        var candidate = Path.Combine(folder, $"{stem}{extension}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var counter = 2;
        while (true)
        {
            candidate = Path.Combine(folder, $"{stem}_{counter}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter++;
        }
    }

    private static string GetUniqueFilePathFromFileName(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return GetUniqueFilePath(folder, stem, extension);
    }

    private static async Task VerifyDirectoryWritableAsync(string folder, CancellationToken ct)
    {
        var probePath = Path.Combine(folder, $".apitester_write_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64, useAsync: true))
            {
                await stream.WriteAsync(new byte[] { 0x20 }, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (Exception ex) when (IsFileSystemWriteException(ex))
            {
                Log.Warning(ex, "Impossible de supprimer le fichier de test temporaire {FileName}.",
                    Path.GetFileName(probePath));
            }
        }
    }

    private async Task DisposeWritersSafeAsync()
    {
        if (_groupedJsonWriter is not null)
        {
            try
            {
                if (_groupedJsonArrayStarted)
                {
                    _groupedJsonWriter.WriteEndArray();
                }

                await _groupedJsonWriter.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileSystemWriteException(ex) || ex is InvalidOperationException)
            {
                Log.Error(ex, "Échec lors de la fermeture du fichier {FileName} ({ErrorType}).",
                    _groupedFileName, ex.GetType().Name);
            }
            finally
            {
                try
                {
                    await _groupedJsonWriter.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsFileSystemWriteException(ex) || ex is InvalidOperationException)
                {
                    Log.Error(ex, "Échec de libération du rédacteur JSON {FileName} ({ErrorType}).",
                        _groupedFileName, ex.GetType().Name);
                }

                _groupedJsonWriter = null;
                _groupedJsonArrayStarted = false;
            }
        }

        if (_groupedStream is not null)
        {
            try
            {
                await _groupedStream.FlushAsync().ConfigureAwait(false);
                await _groupedStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileSystemWriteException(ex) || ex is InvalidOperationException)
            {
                Log.Error(ex, "Échec lors de la fermeture du flux {FileName} ({ErrorType}).",
                    _groupedFileName, ex.GetType().Name);
            }
            finally
            {
                _groupedStream = null;
            }
        }

        if (_csvWriter is not null)
        {
            try
            {
                await _csvWriter.FlushAsync().ConfigureAwait(false);
                await _csvWriter.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFileSystemWriteException(ex) || ex is InvalidOperationException)
            {
                Log.Error(ex, "Échec lors de la fermeture du fichier {FileName} ({ErrorType}).",
                    _csvFileName, ex.GetType().Name);
            }
            finally
            {
                _csvWriter = null;
            }
        }
    }

    private static bool IsFileSystemWriteException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or PathTooLongException
            or NotSupportedException
            or ArgumentException;
}
