using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using Serilog;

namespace ApiTester.Services;

public class BatchRunner : IBatchRunner
{
    public const int ConfirmationThreshold = 500;
    public const int HardMaxRequests = 10_000;

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
        return BatchExecutionHelper.BuildPlan(parameters, variables, mode, HardMaxRequests);
    }

    public IReadOnlyList<string> GetCombination(IReadOnlyList<BatchAxis> axes, long n)
    {
        return BatchExecutionHelper.GetCombination(axes, n);
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
        var fixedVariableValues = BatchExecutionHelper.BuildFixedVariableDictionary(variables);

        var take = Math.Clamp(Math.Min(maxCount, plan.TotalRequests), 0, 10);
        var previewList = new List<BatchPreviewItem>(take);

        for (var i = 0; i < take; i++)
        {
            var index = i + 1;
            var step = BatchExecutionHelper.BuildPreviewStep(enabledParams, axes, fixedVariableValues, mode, i);
            var requestWithQuery = BatchExecutionHelper.CloneRequestWithQuery(templateRequest, step.QueryItems);
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
            var finalUrl = BatchExecutionHelper.BuildFinalUrl(finalReq.Url, finalReq.QueryParams, finalReq.Auth, maskAllSensitive: false);
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
        var consecutiveRateLimitFailures = 0;
        var totalRetryCount = 0;
        var axisNames = axes.Select(a => a.DisplayName).ToArray();
        var fixedVariableValues = BatchExecutionHelper.BuildFixedVariableDictionary(variables);

        Log.Information("Démarrage du lot : {TotalRequests} requêtes (mode: {Mode}, pause: {DelayMs}ms, axes: {@Axes}).",
            total, options.Mode, delayMs, axisNames);

        var index = 0;
        foreach (var step in BatchExecutionHelper.EnumerateExecutionSteps(parameters, axes, fixedVariableValues, options.Mode, total))
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var requestWithQuery = BatchExecutionHelper.CloneRequestWithQuery(templateRequest, step.QueryItems);
            var applyResult = _templateEngine.ApplyDetailed(requestWithQuery, step.VariableDictionary, index);

            BatchResultItem item;
            bool isFailure;
            var preemptiveWait = TimeSpan.Zero;
            var nextRequestAdaptationWait = TimeSpan.Zero;
            var usedSpecificWaitAfterExhaustion = false;

            if (!applyResult.IsSuccess || applyResult.Request is null)
            {
                var maskedUrlOnError = BatchExecutionHelper.BuildMaskedUrlWithTemplate(
                    _templateEngine, templateRequest, step.QueryItems, step.VariableDictionary, index);
                item = new BatchResultItem
                {
                    Index = index,
                    ParamsText = step.ParamsText,
                    StatusCode = 0,
                    StatusCategory = ViewModels.StatusCategory.NetworkError,
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
                var maskedUrl = BatchExecutionHelper.BuildMaskedUrlWithTemplate(
                    _templateEngine, templateRequest, finalRequest.QueryParams, step.VariableDictionary, index);

                var execResult = await BatchExecutionHelper.SendWithRateLimitAsync(
                    _httpService,
                    _rateLimitDetector,
                    finalRequest,
                    policy,
                    delayMs,
                    index,
                    total,
                    hasMoreRequests: index < total,
                    stopOnFailure: options.StopOnFailure,
                    consecutiveRateLimitFailures,
                    totalRetryCount,
                    onRetryScheduled: (_, updatedTotalRetries) =>
                    {
                        progress.Report(new BatchProgressReport
                        {
                            Index = index - 1,
                            Total = total,
                            RetryCount = updatedTotalRetries,
                            IsRetryUpdate = true
                        });
                    },
                    onStateUpdated: (updatedConsecutive, updatedTotalRetries) =>
                    {
                        consecutiveRateLimitFailures = updatedConsecutive;
                        totalRetryCount = updatedTotalRetries;
                    },
                    ct).ConfigureAwait(false);

                preemptiveWait = execResult.PreemptiveWait;
                nextRequestAdaptationWait = execResult.NextRequestAdaptationWait;
                usedSpecificWaitAfterExhaustion = execResult.UsedSpecificWaitAfterExhaustion;

                var response = execResult.Response;
                var itemErrorMessage = execResult.ItemErrorMessage;

                item = new BatchResultItem
                {
                    Index = index,
                    ParamsText = step.ParamsText,
                    StatusCode = response.StatusCode,
                    StatusCategory = BatchExecutionHelper.DetermineStatusCategory(response),
                    DurationMs = response.DurationMs,
                    SizeText = BatchExecutionHelper.FormatSize(response.SizeBytes),
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
                    WasRateLimited = execResult.WasRateLimited,
                    RetryAttempts = execResult.RetryAttempts,
                    FinalWaitMs = execResult.LastWaitMs
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
}
