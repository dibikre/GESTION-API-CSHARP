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

internal sealed class ExecutionIterationStep
{
    public required List<KeyValueItem> QueryItems { get; init; }
    public required List<KeyValueItem> VaryingItems { get; init; }
    public required List<KeyValueItem> VariableItems { get; init; }
    public required List<KeyValueItem> OrderedAxisItems { get; init; }
    public required Dictionary<string, string> VariableDictionary { get; init; }
    public required string ParamsText { get; init; }
}

internal sealed class RateLimitExecutionResult
{
    public required ResponseModel Response { get; init; }
    public required bool WasRateLimited { get; init; }
    public required int RetryAttempts { get; init; }
    public required long LastWaitMs { get; init; }
    public required bool RateLimitExhausted { get; init; }
    public required TimeSpan PreemptiveWait { get; init; }
    public required TimeSpan NextRequestAdaptationWait { get; init; }
    public required bool UsedSpecificWaitAfterExhaustion { get; init; }
    public required string? ItemErrorMessage { get; init; }
}

internal static class BatchExecutionHelper
{
    private const int ParamsPreviewMaxLength = 40;

    public static bool IsSensitiveName(string? name)
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

    public static string MaskIfSensitive(string? name, string? value) =>
        IsSensitiveName(name) ? "***" : (value ?? string.Empty);

    public static BatchPlanResult BuildPlan(
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchVariable> variables,
        BatchMode mode,
        int hardMaxRequests = BatchRunner.HardMaxRequests,
        IReadOnlySet<string>? externalVariableNames = null)
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
            if (!TryGetSequenceCount(param, hardMaxRequests, out var count, out var error))
            {
                plan.ValidationError = error;
                plan.TotalRequests = 0;
                return plan;
            }

            axisCounts.Add(count);
        }

        foreach (var variable in activeVariables)
        {
            if (!variable.IsConfigured)
            {
                if (externalVariableNames is not null && externalVariableNames.Contains(variable.Name))
                {
                    continue;
                }

                plan.ValidationError = variable.ErrorText ?? $"La variable « {variable.Name} » n'est pas configurée.";
                plan.TotalRequests = 0;
                return plan;
            }

            if (variable.HasError)
            {
                plan.ValidationError = variable.ErrorText ?? $"La variable « {variable.Name} » contient une erreur.";
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

            if (minCount > hardMaxRequests)
            {
                plan.TotalRequests = hardMaxRequests + 1;
                plan.ExceedsHardLimit = true;
                plan.ValidationError = $"Plafond maximal de {hardMaxRequests:N0} requêtes dépassé ({minCount:N0} demandées).";
            }
            else
            {
                plan.TotalRequests = (int)minCount;
            }

            return plan;
        }

        long total = 1;
        foreach (var count in axisCounts)
        {
            total = SaturatingMultiply(total, count, hardMaxRequests + 1);
            if (total > hardMaxRequests)
            {
                break;
            }
        }

        if (total > hardMaxRequests)
        {
            plan.TotalRequests = hardMaxRequests + 1;
            plan.ExceedsHardLimit = true;
            plan.ValidationError = $"Plafond maximal de {hardMaxRequests:N0} requêtes dépassé.";
        }
        else
        {
            plan.TotalRequests = (int)total;
        }

        return plan;
    }

    public static IReadOnlyList<string> GetCombination(IReadOnlyList<BatchAxis> axes, long n)
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

    public static IEnumerable<ExecutionIterationStep> EnumerateExecutionSteps(
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

            yield return new ExecutionIterationStep
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

    public static ExecutionIterationStep BuildPreviewStep(
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

            return new ExecutionIterationStep
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

    public static Dictionary<string, string> BuildFixedVariableDictionary(IReadOnlyList<BatchVariable> variables)
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

    public static async Task<RateLimitExecutionResult> SendWithRateLimitAsync(
        IHttpService httpService,
        IRateLimitDetector rateLimitDetector,
        RequestModel finalRequest,
        RateLimitPolicy policy,
        int normalDelayMs,
        int index,
        int total,
        bool hasMoreRequests,
        bool stopOnFailure,
        int consecutiveRateLimitFailures,
        int totalRetryCount,
        Action<int, int> onRetryScheduled,
        Action<int, int> onStateUpdated,
        CancellationToken ct)
    {
        var policyEnabled = policy.IsEnabled;
        var maxRetries = policyEnabled
            ? Math.Clamp(policy.MaxRetriesPerRequest, RateLimitPolicy.MinRetries, RateLimitPolicy.MaxRetriesLimit)
            : 0;

        var wasRateLimited = false;
        var retryAttempts = 0;
        long lastWaitMs = 0;
        var finalRateLimitExhausted = false;
        var preemptiveWait = TimeSpan.Zero;
        var nextRequestAdaptationWait = TimeSpan.Zero;
        var usedSpecificWaitAfterExhaustion = false;
        ResponseModel response;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            response = await httpService.SendAsync(finalRequest, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (policyEnabled && rateLimitDetector.IsRateLimited(response, policy))
            {
                wasRateLimited = true;

                if (retryAttempts < maxRetries)
                {
                    var waitDelay = rateLimitDetector.ComputeRateLimitDelay(
                        response,
                        policy,
                        normalDelayMs,
                        ref consecutiveRateLimitFailures,
                        out _);

                    lastWaitMs = (long)Math.Round(waitDelay.TotalMilliseconds);
                    retryAttempts++;
                    totalRetryCount++;

                    Log.Warning(
                        "Limitation de débit HTTP {StatusCode} sur la requête {Index}/{Total}. Tentative {Attempt}/{MaxRetries} après {WaitMs} ms.",
                        response.StatusCode, index, total, retryAttempts, maxRetries, lastWaitMs);

                    onRetryScheduled(consecutiveRateLimitFailures, totalRetryCount);

                    if (waitDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(waitDelay, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                finalRateLimitExhausted = true;
                if (retryAttempts == 0 && maxRetries == 0 && hasMoreRequests && !stopOnFailure)
                {
                    var waitDelay = rateLimitDetector.ComputeRateLimitDelay(
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
                rateLimitDetector.ShouldSlowDownPreemptively(response, policy, out var suggestedWait))
            {
                preemptiveWait = suggestedWait ??
                                 TimeSpan.FromMilliseconds(Math.Max(policy.PauseFloorMs, policy.BackoffInitialMs));

                Log.Information(
                    "Ralentissement préventif sur la requête {Index}/{Total} : attente de {WaitMs} ms avant la pause normale.",
                    index, total, (long)Math.Round(preemptiveWait.TotalMilliseconds));
            }

            break;
        }

        onStateUpdated(consecutiveRateLimitFailures, totalRetryCount);

        var itemErrorMessage = finalRateLimitExhausted
            ? (maxRetries == 0
                ? $"Limitation de débit HTTP {response.StatusCode} (aucune nouvelle tentative configurée)."
                : $"Limitation de débit HTTP {response.StatusCode} : toutes les tentatives ({retryAttempts}) ont échoué.")
            : response.ErrorMessage;

        return new RateLimitExecutionResult
        {
            Response = response,
            WasRateLimited = wasRateLimited,
            RetryAttempts = retryAttempts,
            LastWaitMs = lastWaitMs,
            RateLimitExhausted = finalRateLimitExhausted,
            PreemptiveWait = preemptiveWait,
            NextRequestAdaptationWait = nextRequestAdaptationWait,
            UsedSpecificWaitAfterExhaustion = usedSpecificWaitAfterExhaustion,
            ItemErrorMessage = itemErrorMessage
        };
    }

    public static RequestModel CloneRequestWithQuery(RequestModel source, List<KeyValueItem> queryItems)
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

    public static string BuildMaskedUrlWithTemplate(
        ITemplateEngine templateEngine,
        RequestModel templateRequest,
        IReadOnlyList<KeyValueItem> substitutedQueryItems,
        IReadOnlyDictionary<string, string> variableValues,
        int index)
    {
        var maskedVars = new Dictionary<string, string>(variableValues.Count, StringComparer.Ordinal);
        foreach (var kvp in variableValues)
        {
            maskedVars[kvp.Key] = IsSensitiveName(kvp.Key) ? "***" : kvp.Value;
        }

        var urlOnlyRequest = new RequestModel
        {
            Method = templateRequest.Method,
            Url = templateRequest.Url,
            BodyType = BodyType.None
        };

        var maskedBaseResult = templateEngine.ApplyDetailed(urlOnlyRequest, maskedVars, index);
        var baseUrl = maskedBaseResult.IsSuccess && maskedBaseResult.Request is not null
            ? maskedBaseResult.Request.Url
            : templateRequest.Url;

        var maskedQuery = substitutedQueryItems
            .Select(q => new KeyValueItem(
                q.Key,
                IsSensitiveName(q.Key) ? "***" : q.Value,
                q.IsEnabled))
            .ToList();

        return BuildFinalUrl(baseUrl, maskedQuery, templateRequest.Auth, maskAllSensitive: true);
    }

    public static string BuildFinalUrl(
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
                    queryParts.Add(maskAllSensitive && IsSensitiveName(decodedKey) ? $"{encodedKey}=***" : segment);
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

    public static StatusCategory DetermineStatusCategory(ResponseModel response)
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

    public static string FormatSize(long bytes)
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

    public static string TruncateForDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > ParamsPreviewMaxLength
            ? $"{value[..ParamsPreviewMaxLength]}…"
            : value;
    }

    public static List<BatchAxis> BuildDefaultStepAxes(
        IReadOnlyList<VaryingParam> parameters,
        IReadOnlyList<BatchVariable> variables)
    {
        var axes = new List<BatchAxis>();

        foreach (var param in parameters)
        {
            if (!param.IsEnabled || !param.IsVarying || string.IsNullOrWhiteSpace(param.Name))
            {
                continue;
            }

            if (!BatchAxis.TryComputeRangeCount(param.Start, param.End, param.Step, 0, out var count, out _) || count <= 0)
            {
                continue;
            }

            var axis = BatchAxis.CreateRange(
                BatchAxisKind.UrlParam,
                param.Name.Trim(),
                param.Start,
                param.End,
                param.Step);
            axis.SourceParam = param;
            axes.Add(axis);
        }

        foreach (var variable in variables)
        {
            if (!variable.IsUsed || !variable.IsVarying || !variable.IsConfigured || variable.HasError ||
                variable.ValueCount <= 0 || string.IsNullOrWhiteSpace(variable.Name))
            {
                continue;
            }

            BatchAxis? axis = null;
            if (variable.Mode == VariableMode.Range)
            {
                axis = BatchAxis.CreateRange(
                    BatchAxisKind.Variable,
                    variable.Name,
                    variable.ParsedStart,
                    variable.ParsedEnd,
                    variable.ParsedStep,
                    variable.Base,
                    variable.Width,
                    variable.UppercaseHex,
                    variable.UsePrefix);
            }
            else if (variable.Mode == VariableMode.List)
            {
                var values = BatchVariable.ParseListValues(variable.ListText);
                if (values.Count > 0)
                {
                    axis = BatchAxis.CreateList(BatchAxisKind.Variable, variable.Name, values);
                }
            }
            else if (variable.Mode == VariableMode.Random)
            {
                var values = variable.GetRandomValuesSnapshot();
                if (values.Count > 0)
                {
                    axis = BatchAxis.CreateList(BatchAxisKind.Variable, variable.Name, values);
                }
            }

            if (axis is not null)
            {
                axis.SourceVariable = variable;
                axes.Add(axis);
            }
        }

        return axes;
    }

    private static ExecutionIterationStep BuildStepFromAxisValues(
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
            displayLabels.Add($"{axis.DisplayName}={TruncateForDisplay(MaskIfSensitive(axis.Name, value))}");

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

        return new ExecutionIterationStep
        {
            QueryItems = queryItems,
            VaryingItems = varyingItems,
            VariableItems = variableItems,
            OrderedAxisItems = orderedAxisItems,
            VariableDictionary = variableDictionary,
            ParamsText = string.Join(", ", displayLabels)
        };
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

    private static bool TryGetSequenceCount(VaryingParam param, int hardMaxRequests, out long count, out string? error)
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

        count = rawCount > hardMaxRequests + 1 ? hardMaxRequests + 1 : rawCount;
        error = null;
        return true;
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
            var encodedValue = maskAllSensitive && IsSensitiveName(key)
                ? "***"
                : Uri.EscapeDataString(param.Value ?? string.Empty);

            queryParts.Add($"{encodedKey}={encodedValue}");
        }

        if (auth.Type == AuthType.ApiKey && !auth.ApiKeyInHeader && !string.IsNullOrWhiteSpace(auth.ApiKeyName))
        {
            var key = auth.ApiKeyName.Trim();
            var encodedKey = Uri.EscapeDataString(key);
            var encodedValue = maskAllSensitive || IsSensitiveName(key)
                ? "***"
                : Uri.EscapeDataString(auth.ApiKeyValue ?? string.Empty);
            queryParts.Add($"{encodedKey}={encodedValue}");
        }

        return queryParts;
    }

    private static string FormatStaticParamsText(List<KeyValueItem> items)
    {
        return items.Count == 0
            ? "(aucun paramètre)"
            : string.Join(", ", items.Select(i => $"{i.Key}={TruncateForDisplay(MaskIfSensitive(i.Key, i.Value))}"));
    }
}
