using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
    private const int ParamsPreviewMaxLength = 40;

    private readonly IHttpService _httpService;
    private readonly ITemplateEngine _templateEngine;

    public BatchRunner(IHttpService httpService, ITemplateEngine templateEngine)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
    }

    public BatchPlanResult BuildPlan(
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchVariable> variables,
        BatchMode mode)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(variables);

        var plan = new BatchPlanResult();
        var enabledParams = parameters
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        var varyingParams = enabledParams.Where(p => p.IsVarying).ToList();
        var activeVariables = variables
            .Where(v => v.IsUsed && !string.IsNullOrWhiteSpace(v.Name))
            .ToList();

        var axisCounts = new List<long>(varyingParams.Count + activeVariables.Count);

        foreach (var param in varyingParams)
        {
            if (!TryGetSequenceCount(param, out var count, out var error))
            {
                plan.ValidationError = error;
                plan.TotalRequests = 0;
                return plan;
            }

            axisCounts.Add(count);
        }

        foreach (var variable in activeVariables)
        {
            if (!variable.IsConfigured || variable.HasError || variable.ValueCount <= 0)
            {
                plan.ValidationError = variable.ErrorText ?? $"La variable « {variable.Name} » n'est pas valide.";
                plan.TotalRequests = 0;
                return plan;
            }

            axisCounts.Add(variable.ValueCount);
        }

        if (axisCounts.Count == 0)
        {
            plan.TotalRequests = 1;
            return plan;
        }

        if (mode == BatchMode.Parallel)
        {
            var minCount = axisCounts.Min();
            if (axisCounts.Distinct().Count() > 1)
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

        // Mode Combinaisons (produit cartésien avec arithmétique saturante)
        long total = 1;
        foreach (var count in axisCounts)
        {
            total = SaturatingMultiply(total, count, HardMaxRequests + 1);
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

    public IReadOnlyList<BatchPreviewItem> GeneratePreview(
        RequestModel templateRequest,
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchVariable> variables,
        BatchMode mode,
        int maxCount = 3)
    {
        ArgumentNullException.ThrowIfNull(templateRequest);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(variables);

        var plan = BuildPlan(parameters, variables, mode);
        if (!string.IsNullOrEmpty(plan.ValidationError))
        {
            return [];
        }

        var take = Math.Clamp(Math.Min(maxCount, plan.TotalRequests), 0, 10);
        var previewList = new List<BatchPreviewItem>(take);
        var index = 0;

        foreach (var step in EnumerateExecutionSteps(parameters, variables, mode, take))
        {
            index++;
            var requestWithQuery = CloneRequestWithQuery(templateRequest, step.QueryItems);
            var applyResult = _templateEngine.ApplyDetailed(requestWithQuery, step.VariableDictionary, index);

            if (!applyResult.IsSuccess || applyResult.Request is null)
            {
                previewList.Add(new BatchPreviewItem
                {
                    Index = index,
                    VariablesSummary = step.ParamsText,
                    Method = templateRequest.Method,
                    FinalUrl = requestWithQuery.Url,
                    HeadersText = FormatHeadersForPreview(requestWithQuery),
                    HasHeaders = HasAnyPreviewHeader(requestWithQuery),
                    BodyText = FormatBodyForPreview(requestWithQuery),
                    HasBody = HasAnyPreviewBody(requestWithQuery),
                    IsInvalidJson = false,
                    ErrorMessage = applyResult.ErrorMessage
                });
                continue;
            }

            var finalReq = applyResult.Request;
            var finalUrl = BuildFinalUrl(finalReq.Url, finalReq.QueryParams, finalReq.Auth, maskAllSensitive: false);
            var headersText = FormatHeadersForPreview(finalReq);
            var bodyText = FormatBodyForPreview(finalReq);

            previewList.Add(new BatchPreviewItem
            {
                Index = index,
                VariablesSummary = step.ParamsText,
                Method = finalReq.Method,
                FinalUrl = finalUrl,
                HeadersText = headersText,
                HasHeaders = !string.IsNullOrEmpty(headersText),
                BodyText = bodyText,
                HasBody = !string.IsNullOrEmpty(bodyText),
                IsInvalidJson = finalReq.BodyType == BodyType.Json && !applyResult.IsValidJson
            });
        }

        return previewList;
    }

    public async Task RunAsync(
        RequestModel templateRequest,
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchVariable> variables,
        BatchRunOptions options,
        IProgress<BatchProgressReport> progress,
        Func<BatchResultItem, CancellationToken, Task>? onResultAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templateRequest);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        var plan = BuildPlan(parameters, variables, options.Mode);
        if (!string.IsNullOrEmpty(plan.ValidationError))
        {
            throw new InvalidOperationException(plan.ValidationError);
        }

        var total = plan.TotalRequests;
        var delayMs = Math.Max(0, options.DelayMs);

        Log.Information("Démarrage du lot : {TotalRequests} requêtes (mode: {Mode}, pause: {DelayMs}ms).",
            total, options.Mode, delayMs);

        var index = 0;
        foreach (var step in EnumerateExecutionSteps(parameters, variables, options.Mode, total))
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var requestWithQuery = CloneRequestWithQuery(templateRequest, step.QueryItems);
            var applyResult = _templateEngine.ApplyDetailed(requestWithQuery, step.VariableDictionary, index);

            BatchResultItem item;
            bool isFailure;

            if (!applyResult.IsSuccess || applyResult.Request is null)
            {
                var maskedUrlOnError = BuildMaskedUrlWithTemplate(templateRequest, step.QueryItems, step.VariableDictionary, index);
                item = new BatchResultItem
                {
                    Index = index,
                    ParamsText = step.ParamsText,
                    StatusCode = 0,
                    StatusCategory = StatusCategory.NetworkError,
                    DurationMs = 0,
                    SizeText = "0 B",
                    SizeBytes = 0,
                    ContentType = string.Empty,
                    RequestUrl = maskedUrlOnError,
                    VaryingParams = step.VaryingItems,
                    VariableValues = step.VariableItems,
                    QueryParams = step.QueryItems,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = applyResult.ErrorMessage ?? "Erreur de substitution des variables.",
                    Response = null
                };
                isFailure = true;
            }
            else
            {
                var finalRequest = applyResult.Request;
                var maskedUrl = BuildMaskedUrlWithTemplate(templateRequest, finalRequest.QueryParams, step.VariableDictionary, index);

                var response = await _httpService.SendAsync(finalRequest, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                }

                item = new BatchResultItem
                {
                    Index = index,
                    ParamsText = step.ParamsText,
                    StatusCode = response.StatusCode,
                    StatusCategory = DetermineStatusCategory(response),
                    DurationMs = response.DurationMs,
                    SizeText = FormatSize(response.SizeBytes),
                    SizeBytes = response.SizeBytes,
                    ContentType = response.ContentType,
                    RequestUrl = maskedUrl,
                    VaryingParams = step.VaryingItems,
                    VariableValues = step.VariableItems,
                    QueryParams = finalRequest.QueryParams,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = response.ErrorMessage,
                    Response = response
                };
                isFailure = response.StatusCode >= 400 || !string.IsNullOrEmpty(response.ErrorMessage);
            }

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
                Index = index,
                Total = total,
                Item = item
            });

            if (options.StopOnFailure && isFailure)
            {
                Log.Information("Lot arrêté prématurément à la requête {Index}/{Total} (option Arrêter au premier échec).",
                    index, total);
                break;
            }

            if (index < total && delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        Log.Information("Exécution du lot terminée.");
    }

    private sealed class BatchExecutionStep
    {
        public required List<KeyValueItem> QueryItems { get; init; }
        public required List<KeyValueItem> VaryingItems { get; init; }
        public required List<KeyValueItem> VariableItems { get; init; }
        public required Dictionary<string, string> VariableDictionary { get; init; }
        public required string ParamsText { get; init; }
    }

    private sealed class VariationAxis
    {
        public required string Name { get; init; }
        public required bool IsVariable { get; init; }
        public required int ParamIndex { get; init; }
        public required Func<IEnumerable<string>> SequenceFactory { get; init; }
    }

    private static IEnumerable<BatchExecutionStep> EnumerateExecutionSteps(
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchVariable> variables,
        BatchMode mode,
        int maxCount)
    {
        if (maxCount <= 0)
        {
            yield break;
        }

        var enabledParams = parameters
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        var activeVariables = variables
            .Where(v => v.IsUsed && v.IsConfigured && !v.HasError && !string.IsNullOrWhiteSpace(v.Name))
            .ToList();

        var axes = new List<VariationAxis>();

        for (var i = 0; i < enabledParams.Count; i++)
        {
            if (enabledParams[i].IsVarying)
            {
                var capturedParam = enabledParams[i];
                axes.Add(new VariationAxis
                {
                    Name = capturedParam.Name.Trim(),
                    IsVariable = false,
                    ParamIndex = i,
                    SequenceFactory = () => EnumerateVaryingParamValues(capturedParam)
                });
            }
        }

        foreach (var variable in activeVariables)
        {
            var capturedVar = variable;
            axes.Add(new VariationAxis
            {
                Name = capturedVar.Name,
                IsVariable = true,
                ParamIndex = -1,
                SequenceFactory = () => capturedVar.EnumerateValues()
            });
        }

        if (axes.Count == 0)
        {
            var staticQuery = enabledParams
                .Select(p => new KeyValueItem(p.Name.Trim(), p.Value, true))
                .ToList();

            yield return new BatchExecutionStep
            {
                QueryItems = staticQuery,
                VaryingItems = [],
                VariableItems = [],
                VariableDictionary = new Dictionary<string, string>(StringComparer.Ordinal),
                ParamsText = FormatStaticParamsText(staticQuery)
            };
            yield break;
        }

        var produced = 0;

        if (mode == BatchMode.Parallel)
        {
            var enumerators = new IEnumerator<string>[axes.Count];
            try
            {
                for (var i = 0; i < axes.Count; i++)
                {
                    enumerators[i] = axes[i].SequenceFactory().GetEnumerator();
                }

                while (produced < maxCount)
                {
                    var allMoved = true;
                    for (var i = 0; i < enumerators.Length; i++)
                    {
                        if (!enumerators[i].MoveNext())
                        {
                            allMoved = false;
                            break;
                        }
                    }

                    if (!allMoved)
                    {
                        yield break;
                    }

                    var selected = new string[axes.Count];
                    for (var i = 0; i < axes.Count; i++)
                    {
                        selected[i] = enumerators[i].Current;
                    }

                    produced++;
                    yield return BuildStepFromAxisValues(enabledParams, axes, selected);
                }
            }
            finally
            {
                for (var i = 0; i < enumerators.Length; i++)
                {
                    enumerators[i]?.Dispose();
                }
            }

            yield break;
        }

        // Mode Combinaisons : produit cartésien paresseux par odomètre
        var cartEnumerators = new IEnumerator<string>[axes.Count];
        try
        {
            for (var i = 0; i < axes.Count; i++)
            {
                cartEnumerators[i] = axes[i].SequenceFactory().GetEnumerator();
                if (!cartEnumerators[i].MoveNext())
                {
                    yield break;
                }
            }

            while (produced < maxCount)
            {
                var selected = new string[axes.Count];
                for (var i = 0; i < axes.Count; i++)
                {
                    selected[i] = cartEnumerators[i].Current;
                }

                produced++;
                yield return BuildStepFromAxisValues(enabledParams, axes, selected);

                if (produced >= maxCount)
                {
                    yield break;
                }

                var pos = axes.Count - 1;
                while (pos >= 0)
                {
                    if (cartEnumerators[pos].MoveNext())
                    {
                        break;
                    }

                    cartEnumerators[pos].Dispose();
                    cartEnumerators[pos] = axes[pos].SequenceFactory().GetEnumerator();
                    if (!cartEnumerators[pos].MoveNext())
                    {
                        yield break;
                    }

                    pos--;
                }

                if (pos < 0)
                {
                    yield break;
                }
            }
        }
        finally
        {
            for (var i = 0; i < cartEnumerators.Length; i++)
            {
                cartEnumerators[i]?.Dispose();
            }
        }
    }

    private static BatchExecutionStep BuildStepFromAxisValues(
        IReadOnlyList<VaryingParam> enabledParams,
        IReadOnlyList<VariationAxis> axes,
        IReadOnlyList<string> selectedValues)
    {
        var varyingValueByParamIndex = new Dictionary<int, string>();
        var varyingItems = new List<KeyValueItem>();
        var variableItems = new List<KeyValueItem>();
        var variableDictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        var displayLabels = new List<string>(axes.Count);

        for (var i = 0; i < axes.Count; i++)
        {
            var axis = axes[i];
            var value = selectedValues[i];

            if (axis.IsVariable)
            {
                variableDictionary[axis.Name] = value;
                variableItems.Add(new KeyValueItem(axis.Name, value, true));
            }
            else
            {
                varyingValueByParamIndex[axis.ParamIndex] = value;
                varyingItems.Add(new KeyValueItem(axis.Name, value, true));
            }

            displayLabels.Add($"{axis.Name}={TruncateForDisplay(value)}");
        }

        var queryItems = new List<KeyValueItem>(enabledParams.Count);
        for (var pIdx = 0; pIdx < enabledParams.Count; pIdx++)
        {
            var p = enabledParams[pIdx];
            var trimmedName = p.Name.Trim();
            var val = varyingValueByParamIndex.TryGetValue(pIdx, out var varyingVal)
                ? varyingVal
                : p.Value;

            queryItems.Add(new KeyValueItem(trimmedName, val, true));
        }

        return new BatchExecutionStep
        {
            QueryItems = queryItems,
            VaryingItems = varyingItems,
            VariableItems = variableItems,
            VariableDictionary = variableDictionary,
            ParamsText = string.Join(", ", displayLabels)
        };
    }

    private static IEnumerable<string> EnumerateVaryingParamValues(VaryingParam param)
    {
        var current = param.Start;
        var end = param.End;
        var step = param.Step;

        if (step == 0)
        {
            yield break;
        }

        while (true)
        {
            if (step > 0 && current > end)
            {
                yield break;
            }

            if (step < 0 && current < end)
            {
                yield break;
            }

            yield return current.ToString(CultureInfo.InvariantCulture);

            if (step > 0 && current > long.MaxValue - step)
            {
                yield break;
            }

            if (step < 0 && current < long.MinValue - step)
            {
                yield break;
            }

            current += step;
        }
    }

    private static long SaturatingMultiply(long left, long right, long cap)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        var product = (Int128)left * (Int128)right;
        return product > cap ? cap : (long)product;
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

        var diff = (Int128)param.End - (Int128)param.Start;
        var count128 = (diff / (Int128)param.Step) + 1;
        count = count128 > HardMaxRequests + 1 ? HardMaxRequests + 1 : (long)count128;
        return true;
    }

    private static RequestModel CloneRequestWithQuery(RequestModel source, List<KeyValueItem> queryItems)
    {
        return new RequestModel
        {
            Method = source.Method,
            Url = source.Url,
            QueryParams = queryItems.Select(q => new KeyValueItem(q.Key, q.Value, q.IsEnabled)).ToList(),
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

    private string BuildMaskedUrlWithTemplate(
        RequestModel templateRequest,
        IReadOnlyList<KeyValueItem> substitutedQueryItems,
        IReadOnlyDictionary<string, string> variableValues,
        int index)
    {
        var maskedVars = new Dictionary<string, string>(variableValues.Count, StringComparer.Ordinal);
        foreach (var kvp in variableValues)
        {
            maskedVars[kvp.Key] = IsSensitiveParamName(kvp.Key) ? "***" : kvp.Value;
        }

        var urlOnlyRequest = new RequestModel
        {
            Method = templateRequest.Method,
            Url = templateRequest.Url,
            BodyType = BodyType.None
        };

        var maskedBaseResult = _templateEngine.ApplyDetailed(urlOnlyRequest, maskedVars, index);
        var baseUrl = maskedBaseResult.IsSuccess && maskedBaseResult.Request is not null
            ? maskedBaseResult.Request.Url
            : templateRequest.Url;

        var maskedQuery = substitutedQueryItems
            .Select(q => new KeyValueItem(
                q.Key,
                IsSensitiveParamName(q.Key) ? "***" : q.Value,
                q.IsEnabled))
            .ToList();

        return BuildFinalUrl(baseUrl, maskedQuery, templateRequest.Auth, maskAllSensitive: true);
    }

    private static string BuildFinalUrl(
        string rawUrl,
        IReadOnlyList<KeyValueItem> queryItems,
        AuthConfig auth,
        bool maskAllSensitive)
    {
        var trimmedUrl = rawUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var baseUri))
        {
            var fallbackParts = BuildQuerySegments(queryItems, auth, maskAllSensitive);
            if (fallbackParts.Count == 0)
            {
                return trimmedUrl;
            }

            var separator = trimmedUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return $"{trimmedUrl}{separator}{string.Join("&", fallbackParts)}";
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
                    queryParts.Add(maskAllSensitive && IsSensitiveParamName(decodedKey) ? $"{encodedKey}=***" : segment);
                }
            }

            queryParts.AddRange(BuildQuerySegments(queryItems, auth, maskAllSensitive));

            uriBuilder.Query = queryParts.Count > 0 ? string.Join("&", queryParts) : string.Empty;
            return uriBuilder.Uri.ToString();
        }
        catch (UriFormatException)
        {
            return trimmedUrl;
        }
    }

    private static List<string> BuildQuerySegments(
        IReadOnlyList<KeyValueItem> queryItems,
        AuthConfig auth,
        bool maskAllSensitive)
    {
        var queryParts = new List<string>();

        foreach (var param in queryItems)
        {
            if (!param.IsEnabled || string.IsNullOrWhiteSpace(param.Key))
            {
                continue;
            }

            var key = param.Key.Trim();
            var encodedKey = Uri.EscapeDataString(key);
            var encodedValue = maskAllSensitive && IsSensitiveParamName(key)
                ? "***"
                : Uri.EscapeDataString(param.Value ?? string.Empty);

            queryParts.Add($"{encodedKey}={encodedValue}");
        }

        if (auth.Type == AuthType.ApiKey && !auth.ApiKeyInHeader && !string.IsNullOrWhiteSpace(auth.ApiKeyName))
        {
            var key = auth.ApiKeyName.Trim();
            var encodedKey = Uri.EscapeDataString(key);
            var encodedValue = maskAllSensitive || IsSensitiveParamName(key)
                ? "***"
                : Uri.EscapeDataString(auth.ApiKeyValue ?? string.Empty);
            queryParts.Add($"{encodedKey}={encodedValue}");
        }

        return queryParts;
    }

    private static bool HasAnyPreviewHeader(RequestModel request) =>
        request.Headers.Any(h => h.IsEnabled && !string.IsNullOrWhiteSpace(h.Key))
        || (request.Auth.Type == AuthType.Bearer && !string.IsNullOrWhiteSpace(request.Auth.Token))
        || request.Auth.Type == AuthType.Basic
        || (request.Auth.Type == AuthType.ApiKey && request.Auth.ApiKeyInHeader && !string.IsNullOrWhiteSpace(request.Auth.ApiKeyName));

    private static string FormatHeadersForPreview(RequestModel request)
    {
        var lines = new List<string>();

        foreach (var header in request.Headers)
        {
            if (!header.IsEnabled || string.IsNullOrWhiteSpace(header.Key))
            {
                continue;
            }

            var key = header.Key.Trim();
            var value = IsSensitivePreviewHeaderName(key) ? "***" : (header.Value ?? string.Empty);
            lines.Add($"{key}: {value}");
        }

        switch (request.Auth.Type)
        {
            case AuthType.Bearer when !string.IsNullOrWhiteSpace(request.Auth.Token):
                lines.Add("Authorization: ***");
                break;
            case AuthType.Basic:
                lines.Add("Authorization: ***");
                break;
            case AuthType.ApiKey when request.Auth.ApiKeyInHeader && !string.IsNullOrWhiteSpace(request.Auth.ApiKeyName):
                lines.Add($"{request.Auth.ApiKeyName.Trim()}: ***");
                break;
        }

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
    }

    private static bool HasAnyPreviewBody(RequestModel request) =>
        !string.IsNullOrEmpty(FormatBodyForPreview(request));

    private static string FormatBodyForPreview(RequestModel request)
    {
        if (request.BodyType == BodyType.None)
        {
            return string.Empty;
        }

        if (request.BodyType == BodyType.FormUrlEncoded)
        {
            var pairs = request.FormFields
                .Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.Key))
                .Select(f => $"{Uri.EscapeDataString(f.Key.Trim())}={Uri.EscapeDataString(f.Value ?? string.Empty)}");
            return string.Join("&", pairs);
        }

        return request.Body ?? string.Empty;
    }

    private static bool IsSensitivePreviewHeaderName(string name)
    {
        return name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatStaticParamsText(List<KeyValueItem> items)
    {
        return items.Count == 0
            ? "(aucun paramètre)"
            : string.Join(", ", items.Select(i => $"{i.Key}={TruncateForDisplay(i.Value)}"));
    }

    private static string TruncateForDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > ParamsPreviewMaxLength
            ? $"{value[..ParamsPreviewMaxLength]}…"
            : value;
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
