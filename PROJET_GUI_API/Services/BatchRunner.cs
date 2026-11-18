using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.ViewModels;
using Serilog;

namespace ApiTester.Services;

public class BatchRunner : IBatchRunner
{
    public const int ConfirmationThreshold = 500;
    public const int HardMaxRequests = 10_000;

    private readonly IHttpService _httpService;

    public BatchRunner(IHttpService httpService)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
    }

    public BatchPlanResult BuildPlan(IReadOnlyList<VaryingParam> parameters, BatchMode mode)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var plan = new BatchPlanResult();
        var enabledParams = parameters
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        var varyingParams = enabledParams.Where(p => p.IsVarying).ToList();

        if (varyingParams.Count == 0)
        {
            plan.TotalRequests = 1;
            return plan;
        }

        var counts = new List<long>(varyingParams.Count);
        foreach (var param in varyingParams)
        {
            if (!TryGetSequenceCount(param, out var count, out var error))
            {
                plan.ValidationError = error;
                plan.TotalRequests = 0;
                return plan;
            }

            counts.Add(count);
        }

        if (mode == BatchMode.Parallel)
        {
            var minCount = counts.Min();
            if (counts.Distinct().Count() > 1)
            {
                plan.Warnings.Add(
                    $"Mode « En parallèle » : les séquences ont des longueurs différentes ; exécution limitée à la plus courte ({minCount} requêtes).");
            }

            if (minCount > HardMaxRequests)
            {
                plan.TotalRequests = HardMaxRequests + 1;
                plan.ExceedsHardLimit = true;
                plan.ValidationError = $"Plafond maximal de {HardMaxRequests:N0} requêtes dépassé ({minCount:N0} demandées).";
            }
            else
            {
                plan.TotalRequests = (int)minCount;
            }

            return plan;
        }

        // Mode Combinaisons (produit cartésien)
        long total = 1;
        foreach (var count in counts)
        {
            if (total > HardMaxRequests / Math.Max(1, count) + 1)
            {
                total = HardMaxRequests + 1;
                break;
            }

            total *= count;
            if (total > HardMaxRequests)
            {
                break;
            }
        }

        if (total > HardMaxRequests)
        {
            plan.TotalRequests = HardMaxRequests + 1;
            plan.ExceedsHardLimit = true;
            plan.ValidationError = $"Plafond maximal de {HardMaxRequests:N0} requêtes dépassé.";
        }
        else
        {
            plan.TotalRequests = (int)total;
        }

        return plan;
    }

    public async Task RunAsync(
        RequestModel templateRequest,
        IReadOnlyList<VaryingParam> parameters,
        BatchRunOptions options,
        IProgress<BatchProgressReport> progress,
        Func<BatchResultItem, CancellationToken, Task>? onResultAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templateRequest);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        var plan = BuildPlan(parameters, options.Mode);
        if (!string.IsNullOrEmpty(plan.ValidationError))
        {
            throw new InvalidOperationException(plan.ValidationError);
        }

        var combinations = GenerateQueryCombinations(parameters, options.Mode, plan.TotalRequests);
        var total = combinations.Count;
        var delayMs = Math.Max(0, options.DelayMs);

        Log.Information("Démarrage du lot : {TotalRequests} requêtes (mode: {Mode}, pause: {DelayMs}ms).",
            total, options.Mode, delayMs);

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (queryItems, varyingItems, paramsText) = combinations[i];
            var requestCopy = CloneRequestWithQuery(templateRequest, queryItems);
            var maskedUrl = BuildMaskedFinalUrl(templateRequest.Url, queryItems);

            var response = await _httpService.SendAsync(requestCopy, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }

            var item = new BatchResultItem
            {
                Index = i + 1,
                ParamsText = paramsText,
                StatusCode = response.StatusCode,
                StatusCategory = DetermineStatusCategory(response),
                DurationMs = response.DurationMs,
                SizeText = FormatSize(response.SizeBytes),
                SizeBytes = response.SizeBytes,
                ContentType = response.ContentType,
                RequestUrl = maskedUrl,
                VaryingParams = varyingItems,
                QueryParams = queryItems,
                TimestampUtc = DateTimeOffset.UtcNow,
                ErrorMessage = response.ErrorMessage,
                Response = response
            };

            if (onResultAsync is not null)
            {
                await onResultAsync(item, ct).ConfigureAwait(false);
            }

            if (!options.KeepBodies)
            {
                item.Response = null;
            }

            progress.Report(new BatchProgressReport
            {
                Index = i + 1,
                Total = total,
                Item = item
            });

            var isFailure = response.StatusCode >= 400 || !string.IsNullOrEmpty(response.ErrorMessage);
            if (options.StopOnFailure && isFailure)
            {
                Log.Information("Lot arrêté prématurément à la requête {Index}/{Total} (option Arrêter au premier échec).",
                    i + 1, total);
                break;
            }

            if (i + 1 < total && delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        Log.Information("Exécution du lot terminée.");
    }

    private static bool TryGetSequenceCount(VaryingParam param, out long count, out string? error)
    {
        count = 0;
        error = null;

        if (param.Step == 0)
        {
            error = $"Le pas du paramètre « {param.Name} » ne peut pas être 0.";
            return false;
        }

        if (param.Start <= param.End && param.Step < 0)
        {
            error = $"Le pas du paramètre « {param.Name} » doit être positif lorsque Début <= Fin.";
            return false;
        }

        if (param.Start > param.End && param.Step > 0)
        {
            error = $"Le pas du paramètre « {param.Name} » doit être négatif lorsque Début > Fin.";
            return false;
        }

        try
        {
            checked
            {
                count = ((param.End - param.Start) / param.Step) + 1;
            }
            return true;
        }
        catch (OverflowException)
        {
            count = HardMaxRequests + 1;
            return true;
        }
    }

    private static List<(List<KeyValueItem> QueryItems, List<KeyValueItem> VaryingItems, string ParamsText)> GenerateQueryCombinations(
        IReadOnlyList<VaryingParam> parameters,
        BatchMode mode,
        int maxCount)
    {
        var enabledParams = parameters
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        var varyingIndices = new List<int>();
        var sequences = new List<List<string>>();

        for (var i = 0; i < enabledParams.Count; i++)
        {
            if (enabledParams[i].IsVarying)
            {
                varyingIndices.Add(i);
                sequences.Add(GenerateSequenceValues(enabledParams[i], maxCount));
            }
        }

        var combinations = new List<(List<KeyValueItem>, List<KeyValueItem>, string)>(maxCount);

        if (varyingIndices.Count == 0)
        {
            var items = enabledParams.Select(p => new KeyValueItem(p.Name.Trim(), p.Value, true)).ToList();
            combinations.Add((items, [], FormatParamsText(items)));
            return combinations;
        }

        if (mode == BatchMode.Parallel)
        {
            for (var row = 0; row < maxCount; row++)
            {
                var items = new List<KeyValueItem>(enabledParams.Count);
                var varyingItems = new List<KeyValueItem>(varyingIndices.Count);
                var varyingLabels = new List<string>(varyingIndices.Count);
                var seqCursor = 0;

                for (var pIdx = 0; pIdx < enabledParams.Count; pIdx++)
                {
                    var p = enabledParams[pIdx];
                    var trimmedName = p.Name.Trim();
                    if (p.IsVarying)
                    {
                        var val = sequences[seqCursor][row];
                        var kv = new KeyValueItem(trimmedName, val, true);
                        items.Add(kv);
                        varyingItems.Add(kv);
                        varyingLabels.Add($"{trimmedName}={val}");
                        seqCursor++;
                    }
                    else
                    {
                        items.Add(new KeyValueItem(trimmedName, p.Value, true));
                    }
                }

                combinations.Add((items, varyingItems, string.Join(", ", varyingLabels)));
            }

            return combinations;
        }

        // Mode Combinaisons (produit cartésien)
        var selectedValues = new string[varyingIndices.Count];
        BuildCartesianRecursive(0);
        return combinations;

        void BuildCartesianRecursive(int depth)
        {
            if (combinations.Count >= maxCount)
            {
                return;
            }

            if (depth == varyingIndices.Count)
            {
                var items = new List<KeyValueItem>(enabledParams.Count);
                var varyingItems = new List<KeyValueItem>(varyingIndices.Count);
                var varyingLabels = new List<string>(varyingIndices.Count);
                var seqCursor = 0;

                for (var pIdx = 0; pIdx < enabledParams.Count; pIdx++)
                {
                    var p = enabledParams[pIdx];
                    var trimmedName = p.Name.Trim();
                    if (p.IsVarying)
                    {
                        var val = selectedValues[seqCursor];
                        var kv = new KeyValueItem(trimmedName, val, true);
                        items.Add(kv);
                        varyingItems.Add(kv);
                        varyingLabels.Add($"{trimmedName}={val}");
                        seqCursor++;
                    }
                    else
                    {
                        items.Add(new KeyValueItem(trimmedName, p.Value, true));
                    }
                }

                combinations.Add((items, varyingItems, string.Join(", ", varyingLabels)));
                return;
            }

            foreach (var val in sequences[depth])
            {
                selectedValues[depth] = val;
                BuildCartesianRecursive(depth + 1);
            }
        }
    }

    private static List<string> GenerateSequenceValues(VaryingParam param, int maxTake)
    {
        var values = new List<string>();
        var current = param.Start;

        while (values.Count < maxTake)
        {
            if (param.Step > 0 && current > param.End)
            {
                break;
            }

            if (param.Step < 0 && current < param.End)
            {
                break;
            }

            values.Add(current.ToString(CultureInfo.InvariantCulture));

            if (param.Step > 0 && current > long.MaxValue - param.Step)
            {
                break;
            }

            if (param.Step < 0 && current < long.MinValue - param.Step)
            {
                break;
            }

            current += param.Step;
        }

        return values;
    }

    private static RequestModel CloneRequestWithQuery(RequestModel source, List<KeyValueItem> queryItems)
    {
        return new RequestModel
        {
            Method = source.Method,
            Url = source.Url,
            QueryParams = queryItems,
            Headers = source.Headers.Select(h => new KeyValueItem(h.Key, h.Value, h.IsEnabled)).ToList(),
            BodyType = source.BodyType,
            Body = source.Body,
            FormFields = source.FormFields.Select(f => new KeyValueItem(f.Key, f.Value, f.IsEnabled)).ToList(),
            Auth = new AuthConfig
            {
                Type = source.Auth.Type,
                Token = source.Auth.Token,
                Username = source.Auth.Username,
                Password = source.Auth.Password,
                ApiKeyName = source.Auth.ApiKeyName,
                ApiKeyValue = source.Auth.ApiKeyValue,
                ApiKeyInHeader = source.Auth.ApiKeyInHeader
            },
            TimeoutSeconds = source.TimeoutSeconds
        };
    }

    private static string FormatParamsText(List<KeyValueItem> items)
    {
        return items.Count == 0
            ? "(aucun paramètre)"
            : string.Join(", ", items.Select(i => $"{i.Key}={i.Value}"));
    }

    private static StatusCategory DetermineStatusCategory(ResponseModel response)
    {
        if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
        {
            return StatusCategory.NetworkError;
        }

        return response.StatusCode switch
        {
            >= 200 and < 300 => StatusCategory.Success,
            >= 300 and < 400 => StatusCategory.Redirect,
            >= 400 and < 500 => StatusCategory.ClientError,
            >= 500 and < 600 => StatusCategory.ServerError,
            _ => StatusCategory.None
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        if (bytes < kb)
        {
            return $"{bytes} B";
        }

        if (bytes < mb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / kb:0.##} KB");
        }

        if (bytes < gb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / mb:0.##} MB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes / gb:0.##} GB");
    }

    private static string BuildMaskedFinalUrl(string rawUrl, IReadOnlyList<KeyValueItem> queryItems)
    {
        var trimmedUrl = rawUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var baseUri))
        {
            return trimmedUrl;
        }

        try
        {
            var uriBuilder = new UriBuilder(baseUri);
            var queryParts = new List<string>();

            var existingQuery = uriBuilder.Query.TrimStart('?');
            if (!string.IsNullOrEmpty(existingQuery))
            {
                foreach (var segment in existingQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIndex = segment.IndexOf('=');
                    if (eqIndex < 0)
                    {
                        queryParts.Add(segment);
                        continue;
                    }

                    var encodedKey = segment[..eqIndex];
                    var decodedKey = Uri.UnescapeDataString(encodedKey.Replace('+', ' '));
                    queryParts.Add(IsSensitiveParamName(decodedKey) ? $"{encodedKey}=***" : segment);
                }
            }

            foreach (var param in queryItems)
            {
                if (!param.IsEnabled || string.IsNullOrWhiteSpace(param.Key))
                {
                    continue;
                }

                var key = param.Key.Trim();
                var encodedKey = Uri.EscapeDataString(key);
                var encodedValue = IsSensitiveParamName(key)
                    ? "***"
                    : Uri.EscapeDataString(param.Value ?? string.Empty);

                queryParts.Add($"{encodedKey}={encodedValue}");
            }

            uriBuilder.Query = queryParts.Count > 0 ? string.Join("&", queryParts) : string.Empty;
            return uriBuilder.Uri.ToString();
        }
        catch (UriFormatException)
        {
            return trimmedUrl;
        }
    }

    private static bool IsSensitiveParamName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("auth", StringComparison.OrdinalIgnoreCase);
    }
}
