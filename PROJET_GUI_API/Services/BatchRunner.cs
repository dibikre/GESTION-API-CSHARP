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
    private const int ParamsPreviewMaxLength = 40;

    private readonly IHttpService _httpService;
    private readonly ITemplateEngine _templateEngine;
    private readonly IRateLimitDetector _rateLimitDetector;

    public BatchRunner(
        IHttpService httpService,
        ITemplateEngine templateEngine,
        IRateLimitDetector? rateLimitDetector = null)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        _rateLimitDetector = rateLimitDetector ?? new RateLimitDetector();
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
            if (!variable.IsConfigured || variable.HasError)
            {
                plan.ValidationError = variable.ErrorText ?? $"La variable « {variable.Name} » n'est pas configurée.";
                plan.TotalRequests = 0;
                return plan;
            }

            if (!variable.IsVarying)
            {
                continue;
            }

            if (variable.ValueCount <= 0)
            {
                plan.ValidationError = $"La variable « {variable.Name} » ne contient aucune valeur.";
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

    public IReadOnlyList<string> GetCombination(IReadOnlyList<BatchAxis> axes, long n)
    {
        ArgumentNullException.ThrowIfNull(axes);
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "L'index de combinaison doit être positif ou nul.");
        }

        if (axes.Count == 0)
        {
            if (n == 0)
            {
                return [];
            }

            throw new ArgumentOutOfRangeException(nameof(n), n, "Une seule requête est disponible sans axe de variation.");
        }

        var totalSaturated = ComputeSaturatingProduct(axes, long.MaxValue);
        if (n >= totalSaturated)
        {
            throw new ArgumentOutOfRangeException(
                nameof(n),
                n,
                $"L'index {n} dépasse le nombre total de combinaisons ({totalSaturated}).");
        }

        var values = new string[axes.Count];
        var remainder = n;

        for (var i = axes.Count - 1; i >= 0; i--)
        {
            var count = axes[i].ValueCount;
            if (count <= 0)
            {
                throw new InvalidOperationException($"L'axe « {axes[i].Name} » ne contient aucune valeur.");
            }

            var axisIndex = remainder % count;
            remainder /= count;
            values[i] = axes[i].GetValue(axisIndex);
        }

        return values;
    }

    public IReadOnlyList<BatchPreviewItem> GeneratePreview(
        RequestModel templateRequest,
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchVariable> variables,
        IReadOnlyList<BatchAxis> axes,
        BatchMode mode,
        int maxCount = 3)
    {
        ArgumentNullException.ThrowIfNull(templateRequest);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(axes);

        var plan = BuildPlan(parameters, variables, mode);
        if (!string.IsNullOrEmpty(plan.ValidationError))
        {
            return [];
        }

        var enabledParams = parameters
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();
        var fixedVariableValues = BuildFixedVariableDictionary(variables);

        var take = Math.Clamp(Math.Min(maxCount, plan.TotalRequests), 0, 10);
        var previewList = new List<BatchPreviewItem>(take);

        for (var i = 0; i < take; i++)
        {
            var index = i + 1;
            var step = BuildPreviewStep(enabledParams, axes, fixedVariableValues, mode, i);
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
        IReadOnlyList<BatchAxis> axes,
        BatchRunOptions options,
        IProgress<BatchProgressReport> progress,
        Func<BatchResultItem, CancellationToken, Task>? onResultAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templateRequest);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        var plan = BuildPlan(parameters, variables, options.Mode);
        if (!string.IsNullOrEmpty(plan.ValidationError))
        {
            throw new InvalidOperationException(plan.ValidationError);
        }

        var total = plan.TotalRequests;
        var delayMs = Math.Max(0, options.DelayMs);
        var policy = (options.RateLimitPolicy ?? new RateLimitPolicy()).Clone();
        var policyEnabled = policy.IsEnabled;
        var maxRetries = policyEnabled
            ? Math.Clamp(policy.MaxRetriesPerRequest, RateLimitPolicy.MinRetries, RateLimitPolicy.MaxRetriesLimit)
            : 0;
        var consecutiveRateLimitFailures = 0;
        var totalRetryCount = 0;
        var axisNames = axes.Select(a => a.DisplayName).ToArray();
        var fixedVariableValues = BuildFixedVariableDictionary(variables);

        Log.Information("Démarrage du lot : {TotalRequests} requêtes (mode: {Mode}, pause: {DelayMs}ms, axes: {@Axes}).",
            total, options.Mode, delayMs, axisNames);

        var index = 0;
        foreach (var step in EnumerateExecutionSteps(parameters, axes, fixedVariableValues, options.Mode, total))
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var requestWithQuery = CloneRequestWithQuery(templateRequest, step.QueryItems);
            var applyResult = _templateEngine.ApplyDetailed(requestWithQuery, step.VariableDictionary, index);

            BatchResultItem item;
            bool isFailure;
            var preemptiveWait = TimeSpan.Zero;
            var nextRequestAdaptationWait = TimeSpan.Zero;
            var usedSpecificWaitAfterExhaustion = false;

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
                    OrderedAxisValues = step.OrderedAxisItems,
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

                var wasRateLimited = false;
                var retryAttempts = 0;
                long lastWaitMs = 0;
                var finalRateLimitExhausted = false;
                ResponseModel response;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    response = await _httpService.SendAsync(finalRequest, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    if (policyEnabled && _rateLimitDetector.IsRateLimited(response, policy))
                    {
                        wasRateLimited = true;

                        if (retryAttempts < maxRetries)
                        {
                            var waitDelay = _rateLimitDetector.ComputeRateLimitDelay(
                                response,
                                policy,
                                delayMs,
                                ref consecutiveRateLimitFailures,
                                out _);

                            lastWaitMs = (long)Math.Round(waitDelay.TotalMilliseconds);
                            retryAttempts++;
                            totalRetryCount++;

                            Log.Warning(
                                "Limitation de débit HTTP {StatusCode} sur la requête {Index}/{Total}. Tentative {Attempt}/{MaxRetries} après {WaitMs} ms.",
                                response.StatusCode, index, total, retryAttempts, maxRetries, lastWaitMs);

                            progress.Report(new BatchProgressReport
                            {
                                Index = index - 1,
                                Total = total,
                                RetryCount = totalRetryCount,
                                IsRetryUpdate = true
                            });

                            if (waitDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(waitDelay, ct).ConfigureAwait(false);
                            }

                            continue;
                        }

                        finalRateLimitExhausted = true;
                        if (retryAttempts == 0 && maxRetries == 0 && index < total && !options.StopOnFailure)
                        {
                            var waitDelay = _rateLimitDetector.ComputeRateLimitDelay(
                                response,
                                policy,
                                normalDelayMs: 0,
                                ref consecutiveRateLimitFailures,
                                out var usedSpecific);

                            if (usedSpecific)
                            {
                                usedSpecificWaitAfterExhaustion = true;
                                nextRequestAdaptationWait = waitDelay;
                                lastWaitMs = (long)Math.Round(waitDelay.TotalMilliseconds);
                            }
                        }

                        break;
                    }

                    if (string.IsNullOrEmpty(response.ErrorMessage) && response.StatusCode is >= 200 and < 400)
                    {
                        consecutiveRateLimitFailures = 0;
                    }

                    if (policyEnabled &&
                        response.IsSuccess &&
                        string.IsNullOrEmpty(response.ErrorMessage) &&
                        _rateLimitDetector.ShouldSlowDownPreemptively(response, policy, out var suggestedWait))
                    {
                        preemptiveWait = suggestedWait ??
                                         TimeSpan.FromMilliseconds(Math.Max(policy.PauseFloorMs, policy.BackoffInitialMs));

                        Log.Information(
                            "Ralentissement préventif sur la requête {Index}/{Total} : attente de {WaitMs} ms avant la pause normale.",
                            index, total, (long)Math.Round(preemptiveWait.TotalMilliseconds));
                    }

                    break;
                }

                var itemErrorMessage = finalRateLimitExhausted
                    ? (maxRetries == 0
                        ? $"Limitation de débit HTTP {response.StatusCode} (aucune nouvelle tentative configurée)."
                        : $"Limitation de débit HTTP {response.StatusCode} : toutes les tentatives ({retryAttempts}) ont échoué.")
                    : response.ErrorMessage;

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
                    OrderedAxisValues = step.OrderedAxisItems,
                    QueryParams = finalRequest.QueryParams,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    ErrorMessage = itemErrorMessage,
                    Response = response,
                    WasRateLimited = wasRateLimited,
                    RetryAttempts = retryAttempts,
                    FinalWaitMs = lastWaitMs
                };
                isFailure = response.StatusCode >= 400 || !string.IsNullOrEmpty(itemErrorMessage);
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
                RetryCount = totalRetryCount,
                IsRetryUpdate = false,
                Item = item
            });

            if (options.StopOnFailure && isFailure)
            {
                Log.Information("Lot arrêté prématurément à la requête {Index}/{Total} (option Arrêter au premier échec).",
                    index, total);
                break;
            }

            if (index < total)
            {
                if (usedSpecificWaitAfterExhaustion)
                {
                    if (nextRequestAdaptationWait > TimeSpan.Zero)
                    {
                        await Task.Delay(nextRequestAdaptationWait, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (preemptiveWait > TimeSpan.Zero)
                    {
                        await Task.Delay(preemptiveWait, ct).ConfigureAwait(false);
                    }

                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        Log.Information("Exécution du lot terminée.");
    }

    private sealed class BatchExecutionStep
    {
        public required List<KeyValueItem> QueryItems { get; init; }
        public required List<KeyValueItem> VaryingItems { get; init; }
        public required List<KeyValueItem> VariableItems { get; init; }
        public required List<KeyValueItem> OrderedAxisItems { get; init; }
        public required Dictionary<string, string> VariableDictionary { get; init; }
        public required string ParamsText { get; init; }
    }

    private BatchExecutionStep BuildPreviewStep(
        IReadOnlyList<VaryingParam> enabledParams,
        IReadOnlyList<BatchAxis> axes,
        IReadOnlyDictionary<string, string> fixedVariableValues,
        BatchMode mode,
        long stepIndex)
    {
        if (axes.Count == 0)
        {
            var staticQuery = enabledParams
                .Select(p => new KeyValueItem(p.Name.Trim(), p.Value, true))
                .ToList();

            return new BatchExecutionStep
            {
                QueryItems = staticQuery,
                VaryingItems = [],
                VariableItems = [],
                OrderedAxisItems = [],
                VariableDictionary = new Dictionary<string, string>(fixedVariableValues, StringComparer.Ordinal),
                ParamsText = FormatStaticParamsText(staticQuery)
            };
        }

        IReadOnlyList<string> selected;
        if (mode == BatchMode.Parallel)
        {
            var arr = new string[axes.Count];
            for (var i = 0; i < axes.Count; i++)
            {
                arr[i] = axes[i].GetValue(stepIndex);
            }
            selected = arr;
        }
        else
        {
            selected = GetCombination(axes, stepIndex);
        }

        return BuildStepFromAxisValues(enabledParams, axes, fixedVariableValues, selected);
    }

    private static IEnumerable<BatchExecutionStep> EnumerateExecutionSteps(
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchAxis> axes,
        IReadOnlyDictionary<string, string> fixedVariableValues,
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
                OrderedAxisItems = [],
                VariableDictionary = new Dictionary<string, string>(fixedVariableValues, StringComparer.Ordinal),
                ParamsText = FormatStaticParamsText(staticQuery)
            };
            yield break;
        }

        if (mode == BatchMode.Parallel)
        {
            for (long stepIndex = 0; stepIndex < maxCount; stepIndex++)
            {
                var selected = new string[axes.Count];
                for (var i = 0; i < axes.Count; i++)
                {
                    if (stepIndex >= axes[i].ValueCount)
                    {
                        yield break;
                    }

                    selected[i] = axes[i].GetValue(stepIndex);
                }

                yield return BuildStepFromAxisValues(enabledParams, axes, fixedVariableValues, selected);
            }

            yield break;
        }

        // Mode Combinaisons : compteur paresseux (axe 0 externe, dernier axe interne)
        for (var i = 0; i < axes.Count; i++)
        {
            if (axes[i].ValueCount <= 0)
            {
                yield break;
            }
        }

        var indices = new long[axes.Count];
        var produced = 0;

        while (produced < maxCount)
        {
            var selected = new string[axes.Count];
            for (var i = 0; i < axes.Count; i++)
            {
                selected[i] = axes[i].GetValue(indices[i]);
            }

            produced++;
            yield return BuildStepFromAxisValues(enabledParams, axes, fixedVariableValues, selected);

            if (produced >= maxCount)
            {
                yield break;
            }

            var pos = axes.Count - 1;
            while (pos >= 0)
            {
                indices[pos]++;
                if (indices[pos] < axes[pos].ValueCount)
                {
                    break;
                }

                indices[pos] = 0;
                pos--;
            }

            if (pos < 0)
            {
                yield break;
            }
        }
    }

    private static BatchExecutionStep BuildStepFromAxisValues(
        IReadOnlyList<VaryingParam> enabledParams,
        IReadOnlyList<BatchAxis> axes,
        IReadOnlyDictionary<string, string> fixedVariableValues,
        IReadOnlyList<string> selectedValues)
    {
        var varyingValueByParamIndex = new Dictionary<int, string>();
        var matchedParamIndices = new HashSet<int>();
        var varyingItems = new List<KeyValueItem>();
        var variableItems = new List<KeyValueItem>();
        var orderedAxisItems = new List<KeyValueItem>(axes.Count);
        var variableDictionary = new Dictionary<string, string>(fixedVariableValues, StringComparer.Ordinal);
        var displayLabels = new List<string>(axes.Count);

        for (var i = 0; i < axes.Count; i++)
        {
            var axis = axes[i];
            var value = selectedValues[i];

            orderedAxisItems.Add(new KeyValueItem(axis.DisplayName, value, true));
            displayLabels.Add($"{axis.DisplayName}={TruncateForDisplay(value)}");

            if (axis.Kind == BatchAxisKind.Variable)
            {
                variableDictionary[axis.Name] = value;
                variableItems.Add(new KeyValueItem(axis.Name, value, true));
            }
            else
            {
                var paramIndex = ResolveParamIndex(enabledParams, axis, matchedParamIndices);
                if (paramIndex >= 0)
                {
                    matchedParamIndices.Add(paramIndex);
                    varyingValueByParamIndex[paramIndex] = value;
                }

                varyingItems.Add(new KeyValueItem(axis.Name, value, true));
            }
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
            OrderedAxisItems = orderedAxisItems,
            VariableDictionary = variableDictionary,
            ParamsText = string.Join(", ", displayLabels)
        };
    }

    private static Dictionary<string, string> BuildFixedVariableDictionary(IReadOnlyList<BatchVariable> variables)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            if (variable.IsUsed &&
                !variable.IsVarying &&
                variable.IsConfigured &&
                !variable.HasError &&
                !string.IsNullOrWhiteSpace(variable.Name))
            {
                dict[variable.Name] = variable.FixedValue;
            }
        }

        return dict;
    }

    private static int ResolveParamIndex(
        IReadOnlyList<VaryingParam> enabledParams,
        BatchAxis axis,
        HashSet<int> alreadyMatched)
    {
        if (axis.SourceParam is not null)
        {
            for (var i = 0; i < enabledParams.Count; i++)
            {
                if (!alreadyMatched.Contains(i) && ReferenceEquals(enabledParams[i], axis.SourceParam))
                {
                    return i;
                }
            }
        }

        for (var i = 0; i < enabledParams.Count; i++)
        {
            if (!alreadyMatched.Contains(i) &&
                enabledParams[i].IsVarying &&
                enabledParams[i].Name.Trim().Equals(axis.Name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static long ComputeSaturatingProduct(IReadOnlyList<BatchAxis> axes, long cap)
    {
        long total = 1;
        foreach (var axis in axes)
        {
            if (axis.ValueCount <= 0)
            {
                return 0;
            }

            total = SaturatingMultiply(total, axis.ValueCount, cap);
            if (total >= cap)
            {
                return cap;
            }
        }

        return total;
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
        if (!BatchAxis.TryComputeRangeCount(param.Start, param.End, param.Step, 0, out var rawCount, out var rangeError))
        {
            count = 0;
            if (param.Step == 0)
            {
                error = $"Le pas du paramètre « {param.Name} » ne peut pas être 0.";
            }
            else if (param.Start <= param.End && param.Step < 0)
            {
                error = $"Le pas du paramètre « {param.Name} » doit être positif lorsque Début <= Fin.";
            }
            else if (param.Start > param.End && param.Step > 0)
            {
                error = $"Le pas du paramètre « {param.Name} » doit être négatif lorsque Début > Fin.";
            }
            else
            {
                error = rangeError;
            }

            return false;
        }

        count = rawCount > HardMaxRequests + 1 ? HardMaxRequests + 1 : rawCount;
        error = null;
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
