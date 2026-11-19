using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.ViewModels;
using Serilog;

namespace ApiTester.Services;

public sealed class ChainRunner : IChainRunner
{
    private readonly IHttpService _httpService;
    private readonly ITemplateEngine _templateEngine;
    private readonly IResponseExtractor _responseExtractor;
    private readonly IRateLimitDetector _rateLimitDetector;

    public ChainRunner(
        IHttpService httpService,
        ITemplateEngine templateEngine,
        IResponseExtractor responseExtractor,
        IRateLimitDetector? rateLimitDetector = null)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        _responseExtractor = responseExtractor ?? throw new ArgumentNullException(nameof(responseExtractor));
        _rateLimitDetector = rateLimitDetector ?? new RateLimitDetector();
    }

    public ChainPlanResult BuildPlan(IReadOnlyList<ChainStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var result = new ChainPlanResult();
        if (steps.Count == 0)
        {
            result.ValidationError = "Ajoute au moins une étape à la chaîne.";
            return result;
        }

        var counts = new List<int>(steps.Count);
        var availableFromPreviousSteps = new HashSet<string>(StringComparer.Ordinal);
        long totalRequests = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var stepNumber = i + 1;
            var stepLabel = string.IsNullOrWhiteSpace(step.Name) ? $"Étape {stepNumber}" : step.Name.Trim();

            if (!step.HasParsed && string.IsNullOrWhiteSpace(step.Url))
            {
                result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : analyse une commande curl ou saisis une URL.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(step.Url))
            {
                result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : l'URL est vide.";
                return result;
            }

            foreach (var ext in step.Extractions)
            {
                if (!_responseExtractor.TryValidateVariableName(ext.VariableName, out var varError))
                {
                    result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : {varError}";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(ext.Expression))
                {
                    result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : expression vide pour l'extraction « {ext.VariableName} ».";
                    return result;
                }

                if (ext.Source == ChainExtractionSource.Header && ext.Kind == ExtractionKind.JsonPath)
                {
                    result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : JSONPath ne s'applique pas aux en-têtes pour « {ext.VariableName} ».";
                    return result;
                }

                if (ext.Source == ChainExtractionSource.Header && string.IsNullOrWhiteSpace(ext.HeaderName))
                {
                    result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : saisis le nom d'en-tête pour « {ext.VariableName} ».";
                    return result;
                }
            }

            var template = step.BuildTemplateRequest();
            foreach (var unknownDollar in _templateEngine.GetUnknownPredefinedMarkers(template))
            {
                result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : variable prédéfinie inconnue {{{{{unknownDollar}}}}}.";
                return result;
            }

            var stepPlan = BatchExecutionHelper.BuildPlan(
                step.VaryingParams,
                step.Variables,
                step.SelectedMode,
                BatchRunner.HardMaxRequests,
                availableFromPreviousSteps);

            if (!string.IsNullOrEmpty(stepPlan.ValidationError))
            {
                result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : {stepPlan.ValidationError}";
                result.ExceedsHardLimit = stepPlan.ExceedsHardLimit;
                return result;
            }

            foreach (var warning in stepPlan.Warnings)
            {
                result.Warnings.Add($"Étape {stepNumber} ({stepLabel}) : {warning}");
            }

            foreach (var variable in step.Variables)
            {
                if (variable.IsUsed && variable.IsConfigured && !variable.HasError &&
                    availableFromPreviousSteps.Contains(variable.Name))
                {
                    result.Warnings.Add(
                        $"Étape {stepNumber} ({stepLabel}) : la variable locale « {variable.Name} » écrasera celle extraite d'une étape précédente.");
                }
            }

            counts.Add(stepPlan.TotalRequests);
            totalRequests += stepPlan.TotalRequests;

            if (totalRequests > BatchRunner.HardMaxRequests)
            {
                result.TotalRequests = BatchRunner.HardMaxRequests + 1;
                result.ExceedsHardLimit = true;
                result.ValidationError = $"Plafond maximal de {BatchRunner.HardMaxRequests:N0} requêtes dépassé.";
                return result;
            }

            foreach (var ext in step.Extractions)
            {
                if (!string.IsNullOrWhiteSpace(ext.VariableName))
                {
                    availableFromPreviousSteps.Add(ext.VariableName.Trim());
                }
            }
        }

        result.TotalRequests = (int)totalRequests;
        result.StepIterationCounts = counts;
        return result;
    }

    public async Task<ChainRunResult> RunAsync(
        IReadOnlyList<ChainStep> steps,
        ChainRunOptions options,
        IProgress<ChainProgressReport> progress,
        Func<ChainStepResult, CancellationToken, Task>? onResultAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        var plan = BuildPlan(steps);
        if (!string.IsNullOrEmpty(plan.ValidationError))
        {
            throw new InvalidOperationException(plan.ValidationError);
        }

        var stopwatch = Stopwatch.StartNew();
        var totalSteps = steps.Count;
        var globalTotal = plan.TotalRequests;
        var globalIndex = 0;
        var totalRetryCount = 0;
        var consecutiveRateLimitFailures = 0;

        var globalPolicy = (options.GlobalRateLimitPolicy ?? new RateLimitPolicy()).Clone();
        var disabledPolicy = new RateLimitPolicy { IsEnabled = false };

        var accumulatedVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        var allResults = new List<ChainStepResult>(globalTotal);
        var finalStatus = ChainRunStatus.Success;
        var stopChain = false;

        Log.Information("Démarrage de la chaîne : {TotalSteps} étapes, {GlobalTotal} requêtes prévues.",
            totalSteps, globalTotal);

        try
        {
            for (var sIdx = 0; sIdx < totalSteps; sIdx++)
            {
                ct.ThrowIfCancellationRequested();

                var step = steps[sIdx];
                var stepNumber = sIdx + 1;
                var stepName = string.IsNullOrWhiteSpace(step.Name) ? $"Étape {stepNumber}" : step.Name.Trim();

                if (stopChain)
                {
                    var skippedResult = CreateSkippedResult(globalIndex, stepNumber, stepName);
                    allResults.Add(skippedResult);
                    progress.Report(new ChainProgressReport
                    {
                        StepIndex = stepNumber,
                        TotalSteps = totalSteps,
                        Iteration = 1,
                        StepTotalIterations = 1,
                        GlobalIndex = globalIndex,
                        GlobalTotal = globalTotal,
                        RetryCount = totalRetryCount,
                        StepName = stepName,
                        Item = skippedResult,
                        AccumulatedVariables = new Dictionary<string, string>(accumulatedVariables, StringComparer.Ordinal)
                    });
                    continue;
                }

                var stepIterationsCount = plan.StepIterationCounts[sIdx];
                var templateRequest = step.BuildTemplateRequest();
                var fixedStepVars = BatchExecutionHelper.BuildFixedVariableDictionary(step.Variables);
                var axes = step.Axes.Count > 0
                    ? (IReadOnlyList<BatchAxis>)step.Axes
                    : BatchExecutionHelper.BuildDefaultStepAxes(step.VaryingParams, step.Variables);

                var stepDelayMs = Math.Max(0, step.DelayAfterMs);
                var activePolicy = step.StepRateLimitUsesGlobalPolicy ? globalPolicy : disabledPolicy;
                if (step.StepRateLimitUsesGlobalPolicy)
                {
                    activePolicy = globalPolicy.Clone();
                    activePolicy.PauseFloorMs = stepDelayMs;
                }

                var iterIndex = 0;
                foreach (var iterStep in BatchExecutionHelper.EnumerateExecutionSteps(
                    step.VaryingParams,
                    axes,
                    fixedStepVars,
                    step.SelectedMode,
                    stepIterationsCount))
                {
                    ct.ThrowIfCancellationRequested();
                    iterIndex++;
                    globalIndex++;

                    // Priorité : variables accumulées puis variables propres de l'étape
                    var mergedVars = new Dictionary<string, string>(accumulatedVariables, StringComparer.Ordinal);
                    foreach (var kvp in iterStep.VariableDictionary)
                    {
                        if (mergedVars.ContainsKey(kvp.Key))
                        {
                            Log.Warning(
                                "Étape {StepNumber} ({StepName}) : la variable « {VariableName} » écrase une variable accumulée.",
                                stepNumber, stepName, kvp.Key);
                        }

                        mergedVars[kvp.Key] = kvp.Value;
                    }

                    var requestWithQuery = BatchExecutionHelper.CloneRequestWithQuery(templateRequest, iterStep.QueryItems);
                    var applyResult = _templateEngine.ApplyDetailed(requestWithQuery, mergedVars, globalIndex);

                    ChainStepResult stepResult;
                    var preemptiveWait = TimeSpan.Zero;
                    var nextRequestAdaptationWait = TimeSpan.Zero;
                    var usedSpecificWaitAfterExhaustion = false;

                    if (!applyResult.IsSuccess || applyResult.Request is null)
                    {
                        var maskedUrl = BatchExecutionHelper.BuildMaskedUrlWithTemplate(
                            _templateEngine, templateRequest, iterStep.QueryItems, mergedVars, globalIndex);

                        var applyError = applyResult.ErrorMessage ?? "Erreur de substitution des variables.";
                        var extractedOnFail = new Dictionary<string, string>(StringComparer.Ordinal);
                        ApplyDefaultsOnMissingExtractions(step.Extractions, extractedOnFail, accumulatedVariables);

                        stepResult = new ChainStepResult
                        {
                            Index = globalIndex,
                            StepIndex = stepNumber,
                            StepName = stepName,
                            Iteration = iterIndex,
                            TotalIterationsForStep = stepIterationsCount,
                            RequestSummary = BuildRequestSummary(templateRequest.Method, maskedUrl, iterStep.ParamsText),
                            RequestUrl = maskedUrl,
                            StatusCode = 0,
                            StatusCategory = StatusCategory.NetworkError,
                            DurationMs = 0,
                            SizeBytes = 0,
                            SizeText = "0 B",
                            ContentType = string.Empty,
                            Response = null,
                            ExtractedValues = extractedOnFail,
                            ExtractionErrors = [applyError],
                            Status = ChainStepStatus.Failed,
                            ErrorMessage = applyError,
                            VaryingParams = iterStep.VaryingItems,
                            VariableValues = iterStep.VariableItems,
                            OrderedAxisValues = iterStep.OrderedAxisItems,
                            QueryParams = iterStep.QueryItems,
                            TimestampUtc = DateTimeOffset.UtcNow
                        };
                    }
                    else
                    {
                        var finalRequest = applyResult.Request;
                        var maskedUrl = BatchExecutionHelper.BuildMaskedUrlWithTemplate(
                            _templateEngine, templateRequest, finalRequest.QueryParams, mergedVars, globalIndex);

                        var execResult = await BatchExecutionHelper.SendWithRateLimitAsync(
                            _httpService,
                            _rateLimitDetector,
                            finalRequest,
                            activePolicy,
                            stepDelayMs,
                            globalIndex,
                            globalTotal,
                            hasMoreRequests: globalIndex < globalTotal,
                            stopOnFailure: !step.ContinueOnError,
                            consecutiveRateLimitFailures,
                            totalRetryCount,
                            onRetryScheduled: (_, updatedTotalRetries) =>
                            {
                                progress.Report(new ChainProgressReport
                                {
                                    StepIndex = stepNumber,
                                    TotalSteps = totalSteps,
                                    Iteration = iterIndex,
                                    StepTotalIterations = stepIterationsCount,
                                    GlobalIndex = globalIndex - 1,
                                    GlobalTotal = globalTotal,
                                    RetryCount = updatedTotalRetries,
                                    IsRetryUpdate = true,
                                    StepName = stepName
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
                        var httpFailed = !string.IsNullOrEmpty(execResult.ItemErrorMessage) || response.StatusCode >= 400;

                        var extractedThisIteration = new Dictionary<string, string>(StringComparer.Ordinal);
                        var extractionErrors = new List<string>();
                        var requiredExtractionFailed = false;

                        foreach (var ext in step.Extractions)
                        {
                            var varName = ext.VariableName.Trim();
                            var outcome = _responseExtractor.Extract(response, ext);

                            if (outcome.IsSuccess)
                            {
                                extractedThisIteration[varName] = outcome.Value;
                                accumulatedVariables[varName] = outcome.Value;
                                Log.Information(
                                    "Étape {StepNumber} ({StepName}) : variable « {VariableName} » extraite.",
                                    stepNumber, stepName, varName);
                            }
                            else
                            {
                                var detail = $"Extraction « {varName} » : {outcome.ErrorMessage}";
                                Log.Warning(
                                    "Étape {StepNumber} ({StepName}) : échec d'extraction pour « {VariableName} » ({Reason}).",
                                    stepNumber, stepName, varName, outcome.ErrorMessage);

                                if (ext.IsRequired)
                                {
                                    requiredExtractionFailed = true;
                                    extractionErrors.Add(detail);
                                }

                                var fallback = ext.DefaultValueIfMissing ?? string.Empty;
                                extractedThisIteration[varName] = fallback;
                                accumulatedVariables[varName] = fallback;
                            }
                        }

                        var iterationFailed = httpFailed || requiredExtractionFailed;
                        var combinedError = CombineErrorMessages(execResult.ItemErrorMessage, extractionErrors);

                        stepResult = new ChainStepResult
                        {
                            Index = globalIndex,
                            StepIndex = stepNumber,
                            StepName = stepName,
                            Iteration = iterIndex,
                            TotalIterationsForStep = stepIterationsCount,
                            RequestSummary = BuildRequestSummary(finalRequest.Method, maskedUrl, iterStep.ParamsText),
                            RequestUrl = maskedUrl,
                            StatusCode = response.StatusCode,
                            StatusCategory = BatchExecutionHelper.DetermineStatusCategory(response),
                            DurationMs = response.DurationMs,
                            SizeBytes = response.SizeBytes,
                            SizeText = BatchExecutionHelper.FormatSize(response.SizeBytes),
                            ContentType = response.ContentType,
                            Response = response,
                            ExtractedValues = extractedThisIteration,
                            ExtractionErrors = extractionErrors,
                            Status = iterationFailed ? ChainStepStatus.Failed : ChainStepStatus.Success,
                            ErrorMessage = combinedError,
                            VaryingParams = iterStep.VaryingItems,
                            VariableValues = iterStep.VariableItems,
                            OrderedAxisValues = iterStep.OrderedAxisItems,
                            QueryParams = finalRequest.QueryParams,
                            TimestampUtc = DateTimeOffset.UtcNow,
                            WasRateLimited = execResult.WasRateLimited,
                            RetryAttempts = execResult.RetryAttempts,
                            FinalWaitMs = execResult.LastWaitMs
                        };

                        Log.Information(
                            "Étape {StepNumber} ({StepName}) [{Iteration}/{TotalIterations}] terminée : {Status} (HTTP {StatusCode}, {DurationMs} ms).",
                            stepNumber, stepName, iterIndex, stepIterationsCount, stepResult.Status, stepResult.StatusCode, stepResult.DurationMs);
                    }

                    if (onResultAsync is not null)
                    {
                        await onResultAsync(stepResult, ct).ConfigureAwait(false);
                    }

                    if (!options.KeepBodies)
                    {
                        stepResult.Response = null;
                    }

                    allResults.Add(stepResult);

                    progress.Report(new ChainProgressReport
                    {
                        StepIndex = stepNumber,
                        TotalSteps = totalSteps,
                        Iteration = iterIndex,
                        StepTotalIterations = stepIterationsCount,
                        GlobalIndex = globalIndex,
                        GlobalTotal = globalTotal,
                        RetryCount = totalRetryCount,
                        StepName = stepName,
                        Item = stepResult,
                        AccumulatedVariables = new Dictionary<string, string>(accumulatedVariables, StringComparer.Ordinal)
                    });

                    if (stepResult.Status == ChainStepStatus.Failed && !step.ContinueOnError)
                    {
                        Log.Warning(
                            "Chaîne interrompue à l'étape {StepNumber} ({StepName}) (ContinueOnError désactivé).",
                            stepNumber, stepName);
                        finalStatus = ChainRunStatus.Failed;
                        stopChain = true;
                        break;
                    }

                    var hasMoreInChain = iterIndex < stepIterationsCount || sIdx < totalSteps - 1;
                    if (hasMoreInChain)
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

                            if (stepDelayMs > 0)
                            {
                                await Task.Delay(stepDelayMs, ct).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            finalStatus = ChainRunStatus.Cancelled;
            Log.Information("Exécution de la chaîne annulée par l'utilisateur.");
            throw;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (finalStatus == ChainRunStatus.Success &&
            allResults.Any(r => r.Status == ChainStepStatus.Failed))
        {
            finalStatus = ChainRunStatus.Failed;
        }

        Log.Information("Chaîne terminée ({FinalStatus}) en {DurationMs} ms.",
            finalStatus, stopwatch.ElapsedMilliseconds);

        return new ChainRunResult
        {
            StepResults = allResults,
            FinalStatus = finalStatus,
            AllVariables = accumulatedVariables,
            TotalDurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static void ApplyDefaultsOnMissingExtractions(
        IReadOnlyList<ChainExtraction> extractions,
        Dictionary<string, string> extractedThisIteration,
        Dictionary<string, string> accumulatedVariables)
    {
        foreach (var ext in extractions)
        {
            if (string.IsNullOrWhiteSpace(ext.VariableName))
            {
                continue;
            }

            var name = ext.VariableName.Trim();
            var fallback = ext.DefaultValueIfMissing ?? string.Empty;
            extractedThisIteration[name] = fallback;
            accumulatedVariables[name] = fallback;
        }
    }

    private static ChainStepResult CreateSkippedResult(int globalIndex, int stepNumber, string stepName) => new()
    {
        Index = globalIndex,
        StepIndex = stepNumber,
        StepName = stepName,
        Iteration = 1,
        TotalIterationsForStep = 1,
        RequestSummary = "(ignorée suite à un échec précédent)",
        Status = ChainStepStatus.Skipped,
        StatusCategory = StatusCategory.None,
        ErrorMessage = "Étape ignorée suite à l'échec d'une étape précédente.",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    private static string BuildRequestSummary(HttpMethodType method, string maskedUrl, string paramsText)
    {
        var prefix = $"{method.ToString().ToUpperInvariant()} {maskedUrl}";
        return string.IsNullOrWhiteSpace(paramsText) || paramsText == "(aucun paramètre)"
            ? prefix
            : $"{prefix} ({paramsText})";
    }

    private static string? CombineErrorMessages(string? httpError, List<string> extractionErrors)
    {
        if (string.IsNullOrWhiteSpace(httpError) && extractionErrors.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(httpError))
        {
            return string.Join(" | ", extractionErrors);
        }

        if (extractionErrors.Count == 0)
        {
            return httpError;
        }

        return $"{httpError} | {string.Join(" | ", extractionErrors)}";
    }
}
