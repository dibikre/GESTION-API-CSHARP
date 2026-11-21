using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
    private readonly IStepReturnEvaluator _stepReturnEvaluator;

    public ChainRunner(
        IHttpService httpService,
        ITemplateEngine templateEngine,
        IResponseExtractor responseExtractor,
        IRateLimitDetector? rateLimitDetector = null,
        IStepReturnEvaluator? stepReturnEvaluator = null)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        _responseExtractor = responseExtractor ?? throw new ArgumentNullException(nameof(responseExtractor));
        _rateLimitDetector = rateLimitDetector ?? new RateLimitDetector();
        _stepReturnEvaluator = stepReturnEvaluator ?? new StepReturnEvaluator();
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
        var dynamicFlags = new List<bool>(steps.Count);
        var availableFromPreviousSteps = new HashSet<string>(StringComparer.Ordinal);
        var availableListsFromPreviousSteps = new HashSet<string>(StringComparer.Ordinal);
        long totalRequests = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var stepNumber = i + 1;
            var stepLabel = string.IsNullOrWhiteSpace(step.Name) ? $"Étape {stepNumber}" : step.Name.Trim();

            if (!step.HasParsed && string.IsNullOrWhiteSpace(step.Url) && !step.HasReturnExpression)
            {
                result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : analyse une commande curl, saisis une URL ou définis un retour.";
                return result;
            }

            if (string.IsNullOrWhiteSpace(step.Url) && !step.HasReturnExpression)
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

                if (ext.Conditions.Count > 0 &&
                    !ExtractionConditionEngine.ValidateConditions(ext.Conditions, out var condError))
                {
                    result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : extraction « {ext.VariableName} » — {condError}";
                    return result;
                }
            }

            var template = step.BuildTemplateRequest();
            foreach (var unknownDollar in _templateEngine.GetUnknownPredefinedMarkers(template))
            {
                result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : variable prédéfinie inconnue {{{{{unknownDollar}}}}}.";
                return result;
            }

            var usedLists = _templateEngine.ExtractListVariables(template);
            foreach (var listVar in usedLists)
            {
                if (!availableListsFromPreviousSteps.Contains(listVar.Name))
                {
                    result.ValidationError = availableFromPreviousSteps.Contains(listVar.Name)
                        ? $"Étape {stepNumber} ({stepLabel}) : [[{listVar.Name}]] correspond à une variable simple ; coche « En liste [[...]] » sur son extraction ou utilise {{{{{listVar.Name}}}}}."
                        : $"Étape {stepNumber} ({stepLabel}) : la liste [[{listVar.Name}]] n'est extraite par aucune étape précédente.";
                    return result;
                }
            }

            var configuredVarNames = step.Variables
                .Where(v => v.IsUsed && v.IsConfigured && !v.HasError && !string.IsNullOrWhiteSpace(v.Name))
                .Select(v => v.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var scalarVar in _templateEngine.ExtractVariables(template))
            {
                if (!configuredVarNames.Contains(scalarVar.Name) &&
                    !availableFromPreviousSteps.Contains(scalarVar.Name) &&
                    availableListsFromPreviousSteps.Contains(scalarVar.Name))
                {
                    result.ValidationError =
                        $"Étape {stepNumber} ({stepLabel}) : « {scalarVar.Name} » est extraite en liste ; utilise [[{scalarVar.Name}]] pour itérer sur ses valeurs.";
                    return result;
                }
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

            var stepIsDynamic = usedLists.Count > 0;
            if (stepIsDynamic)
            {
                result.HasDynamicCounts = true;
            }

            dynamicFlags.Add(stepIsDynamic);
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
                if (string.IsNullOrWhiteSpace(ext.VariableName))
                {
                    continue;
                }

                var name = ext.VariableName.Trim();
                if (ext.IsList)
                {
                    availableListsFromPreviousSteps.Add(name);
                }
                else
                {
                    availableFromPreviousSteps.Add(name);
                }
            }

            if (step.HasReturnExpression)
            {
                if (step.IsReturnOnlyStep && step.Extractions.Count > 0)
                {
                    result.Warnings.Add(
                        $"Étape {stepNumber} ({stepLabel}) : aucune URL configurée, les extractions HTTP sont ignorées pour cette étape de retour.");
                }

                var returnScalars = new HashSet<string>(availableFromPreviousSteps, StringComparer.Ordinal);
                returnScalars.UnionWith(configuredVarNames);

                var returnError = ValidateStepReturn(
                    _stepReturnEvaluator,
                    step.ReturnExpression,
                    returnScalars,
                    availableListsFromPreviousSteps);
                if (!string.IsNullOrEmpty(returnError))
                {
                    result.ValidationError = $"Étape {stepNumber} ({stepLabel}) : {returnError}";
                    return result;
                }
            }
        }

        result.TotalRequests = (int)totalRequests;
        result.StepIterationCounts = counts;
        result.StepHasDynamicCounts = dynamicFlags;
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
        var accumulatedLists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var allResults = new List<ChainStepResult>(globalTotal);
        var finalStatus = ChainRunStatus.Success;
        var stopChain = false;
        string? finalReturnValue = null;
        string? cachedRunIp = null;

        Log.Information("Démarrage de la chaîne : {TotalSteps} étapes, {GlobalTotal} requêtes prévues (base).",
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
                        AccumulatedVariables = new Dictionary<string, string>(accumulatedVariables, StringComparer.Ordinal),
                        AccumulatedLists = CloneListsSnapshot(accumulatedLists)
                    });
                    continue;
                }

                var templateRequest = step.BuildTemplateRequest();
                var fixedStepVars = BatchExecutionHelper.BuildFixedVariableDictionary(step.Variables);
                var rawAxes = step.Axes.Count > 0
                    ? (IReadOnlyList<BatchAxis>)step.Axes
                    : BatchExecutionHelper.BuildDefaultStepAxes(step.VaryingParams, step.Variables);

                var usedListNames = _templateEngine.ExtractListVariables(templateRequest)
                    .Select(v => v.Name)
                    .ToHashSet(StringComparer.Ordinal);

                var runtimeAxes = ResolveRuntimeAxes(rawAxes, usedListNames, accumulatedLists, out var emptyListName);
                if (emptyListName is not null)
                {
                    var emptyResult = CreateEmptyListSkippedResult(globalIndex, stepNumber, stepName, emptyListName);
                    allResults.Add(emptyResult);
                    if (onResultAsync is not null)
                    {
                        await onResultAsync(emptyResult, ct).ConfigureAwait(false);
                    }
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
                        Item = emptyResult,
                        AccumulatedVariables = new Dictionary<string, string>(accumulatedVariables, StringComparer.Ordinal),
                        AccumulatedLists = CloneListsSnapshot(accumulatedLists)
                    });
                    continue;
                }

                if (!BatchExecutionHelper.TryComputeTotalIterations(
                    runtimeAxes,
                    step.SelectedMode,
                    BatchRunner.HardMaxRequests - globalIndex,
                    out var stepIterationsCount,
                    out var runtimePlanError))
                {
                    var limitResult = new ChainStepResult
                    {
                        Index = globalIndex + 1,
                        StepIndex = stepNumber,
                        StepName = stepName,
                        Iteration = 1,
                        TotalIterationsForStep = 1,
                        RequestSummary = "(plafond de requêtes dépassé)",
                        Status = ChainStepStatus.Failed,
                        StatusCategory = StatusCategory.NetworkError,
                        ErrorMessage = runtimePlanError ?? $"Plafond maximal de {BatchRunner.HardMaxRequests:N0} requêtes dépassé.",
                        TimestampUtc = DateTimeOffset.UtcNow
                    };
                    allResults.Add(limitResult);
                    if (onResultAsync is not null)
                    {
                        await onResultAsync(limitResult, ct).ConfigureAwait(false);
                    }
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
                        Item = limitResult,
                        AccumulatedVariables = new Dictionary<string, string>(accumulatedVariables, StringComparer.Ordinal),
                        AccumulatedLists = CloneListsSnapshot(accumulatedLists)
                    });
                    finalStatus = ChainRunStatus.Failed;
                    if (!step.ContinueOnError)
                    {
                        stopChain = true;
                    }
                    continue;
                }

                var remainingStaticCount = 0;
                for (var k = sIdx + 1; k < totalSteps; k++)
                {
                    remainingStaticCount += k < plan.StepIterationCounts.Count ? plan.StepIterationCounts[k] : 1;
                }
                globalTotal = globalIndex + stepIterationsCount + remainingStaticCount;

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
                    runtimeAxes,
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
                    var applyResult = _templateEngine.ApplyDetailed(
                        requestWithQuery,
                        mergedVars,
                        iterStep.ListDictionary,
                        globalIndex);

                    ChainStepResult stepResult;
                    var preemptiveWait = TimeSpan.Zero;
                    var nextRequestAdaptationWait = TimeSpan.Zero;
                    var usedSpecificWaitAfterExhaustion = false;

                    if (step.IsReturnOnlyStep)
                    {
                        if (cachedRunIp is null && IpResolutionHelper.ReturnExpressionMayUseIp(step.ReturnExpression))
                        {
                            cachedRunIp = await IpResolutionHelper.ResolveIpAsync(ct).ConfigureAwait(false);
                        }

                        var returnEval = _stepReturnEvaluator.Evaluate(
                            step.ReturnExpression,
                            mergedVars,
                            iterStep.ListDictionary,
                            accumulatedLists,
                            globalIndex,
                            resolvedIp: cachedRunIp);

                        var bodyText = returnEval.IsSuccess
                            ? returnEval.FormattedValue
                            : (returnEval.ErrorMessage ?? "Erreur lors de l'évaluation du retour.");
                        var byteSize = Encoding.UTF8.GetByteCount(bodyText);

                        if (returnEval.IsSuccess)
                        {
                            finalReturnValue = returnEval.FormattedValue;
                            Log.Information(
                                "Étape {StepNumber} ({StepName}) [{Iteration}/{TotalIterations}] : retour évalué ({Bytes} octets).",
                                stepNumber, stepName, iterIndex, stepIterationsCount, byteSize);
                        }
                        else
                        {
                            Log.Warning(
                                "Étape {StepNumber} ({StepName}) [{Iteration}/{TotalIterations}] : échec d'évaluation du retour ({Reason}).",
                                stepNumber, stepName, iterIndex, stepIterationsCount, returnEval.ErrorMessage);
                        }

                        stepResult = new ChainStepResult
                        {
                            Index = globalIndex,
                            StepIndex = stepNumber,
                            StepName = stepName,
                            Iteration = iterIndex,
                            TotalIterationsForStep = stepIterationsCount,
                            RequestSummary = $"RETURN {BatchExecutionHelper.TruncateForDisplay(step.ReturnExpression.Trim())}",
                            RequestUrl = string.Empty,
                            StatusCode = returnEval.IsSuccess ? 200 : 0,
                            StatusCategory = returnEval.IsSuccess ? StatusCategory.Success : StatusCategory.NetworkError,
                            DurationMs = 0,
                            SizeBytes = byteSize,
                            SizeText = BatchExecutionHelper.FormatSize(byteSize),
                            ContentType = returnEval.IsSuccess && returnEval.IsJson
                                ? "application/json; charset=utf-8"
                                : "text/plain; charset=utf-8",
                            Response = new ResponseModel
                            {
                                StatusCode = returnEval.IsSuccess ? 200 : 0,
                                ReasonPhrase = returnEval.IsSuccess ? "Retour" : "Erreur",
                                IsSuccess = returnEval.IsSuccess,
                                DurationMs = 0,
                                SizeBytes = byteSize,
                                ContentType = returnEval.IsSuccess && returnEval.IsJson
                                    ? "application/json; charset=utf-8"
                                    : "text/plain; charset=utf-8",
                                Body = bodyText,
                                ErrorMessage = returnEval.IsSuccess ? null : returnEval.ErrorMessage
                            },
                            ReturnValue = returnEval.IsSuccess ? returnEval.FormattedValue : null,
                            IsReturnOnly = true,
                            Status = returnEval.IsSuccess ? ChainStepStatus.Success : ChainStepStatus.Failed,
                            ErrorMessage = returnEval.IsSuccess ? null : returnEval.ErrorMessage,
                            VaryingParams = iterStep.VaryingItems,
                            VariableValues = iterStep.VariableItems,
                            OrderedAxisValues = iterStep.OrderedAxisItems,
                            QueryParams = iterStep.QueryItems,
                            TimestampUtc = DateTimeOffset.UtcNow
                        };
                    }
                    else if (!applyResult.IsSuccess || applyResult.Request is null)
                    {
                        var maskedUrl = BatchExecutionHelper.BuildMaskedUrlWithTemplate(
                            _templateEngine,
                            templateRequest,
                            iterStep.QueryItems,
                            mergedVars,
                            globalIndex,
                            iterStep.ListDictionary);

                        var applyError = applyResult.ErrorMessage ?? "Erreur de substitution des variables.";
                        var extractedOnFail = new Dictionary<string, string>(StringComparer.Ordinal);
                        var extractedListsOnFail = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                        ApplyDefaultsOnMissingExtractions(
                            step.Extractions,
                            extractedOnFail,
                            extractedListsOnFail,
                            accumulatedVariables,
                            accumulatedLists);

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
                            ExtractedLists = extractedListsOnFail,
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
                            _templateEngine,
                            templateRequest,
                            finalRequest.QueryParams,
                            mergedVars,
                            globalIndex,
                            iterStep.ListDictionary);

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
                        var extractedListsThisIteration = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                        var extractionErrors = new List<string>();
                        var requiredExtractionFailed = false;

                        foreach (var ext in step.Extractions)
                        {
                            var varName = ext.VariableName.Trim();
                            if (ext.IsList)
                            {
                                var listOutcome = _responseExtractor.ExtractList(response, ext);
                                if (listOutcome.IsSuccess)
                                {
                                    extractedListsThisIteration[varName] = listOutcome.Values;
                                    accumulatedLists[varName] = listOutcome.Values;
                                    Log.Information(
                                        "Étape {StepNumber} ({StepName}) : liste « {VariableName} » extraite ({Count} éléments).",
                                        stepNumber, stepName, varName, listOutcome.Values.Count);
                                }
                                else
                                {
                                    var detail = $"Extraction [[{varName}]] : {listOutcome.ErrorMessage}";
                                    Log.Warning(
                                        "Étape {StepNumber} ({StepName}) : échec d'extraction en liste pour « {VariableName} » ({Reason}).",
                                        stepNumber, stepName, varName, listOutcome.ErrorMessage);

                                    if (ext.IsRequired)
                                    {
                                        requiredExtractionFailed = true;
                                        extractionErrors.Add(detail);
                                    }

                                    IReadOnlyList<string> fallbackList = string.IsNullOrEmpty(ext.DefaultValueIfMissing)
                                        ? []
                                        : [ext.DefaultValueIfMissing];
                                    extractedListsThisIteration[varName] = fallbackList;
                                    accumulatedLists[varName] = fallbackList;
                                }

                                continue;
                            }

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
                        string? stepReturnValue = null;
                        if (!iterationFailed && step.HasReturnExpression)
                        {
                            var returnVars = new Dictionary<string, string>(mergedVars, StringComparer.Ordinal);
                            foreach (var kvp in extractedThisIteration)
                            {
                                returnVars[kvp.Key] = kvp.Value;
                            }

                            if (cachedRunIp is null && IpResolutionHelper.ReturnExpressionMayUseIp(step.ReturnExpression))
                            {
                                cachedRunIp = await IpResolutionHelper.ResolveIpAsync(ct).ConfigureAwait(false);
                            }

                            var returnEval = _stepReturnEvaluator.Evaluate(
                                step.ReturnExpression,
                                returnVars,
                                iterStep.ListDictionary,
                                accumulatedLists,
                                globalIndex,
                                resolvedIp: cachedRunIp);

                            if (returnEval.IsSuccess)
                            {
                                stepReturnValue = returnEval.FormattedValue;
                                finalReturnValue = stepReturnValue;
                            }
                            else
                            {
                                iterationFailed = true;
                                extractionErrors.Add($"Retour : {returnEval.ErrorMessage}");
                            }
                        }

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
                            ExtractedLists = extractedListsThisIteration,
                            ExtractionErrors = extractionErrors,
                            ReturnValue = stepReturnValue,
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
                        AccumulatedVariables = new Dictionary<string, string>(accumulatedVariables, StringComparer.Ordinal),
                        AccumulatedLists = CloneListsSnapshot(accumulatedLists)
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
            AllLists = accumulatedLists,
            FinalReturnValue = finalReturnValue,
            TotalDurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    internal static string? ValidateStepReturn(
        IStepReturnEvaluator evaluator,
        string returnExpression,
        IReadOnlySet<string> availableScalars,
        IReadOnlySet<string> availableLists)
    {
        var parse = evaluator.Parse(returnExpression);
        if (!parse.IsSuccess)
        {
            return $"retour invalide : {parse.ErrorMessage}";
        }

        foreach (var scalar in parse.Dependencies.RequiredScalars)
        {
            if (!availableScalars.Contains(scalar))
            {
                if (scalar == "ip" && !availableLists.Contains("ip"))
                {
                    continue;
                }

                return availableLists.Contains(scalar)
                    ? $"retour : « {scalar} » est une liste ; utilise {scalar} ou [[{scalar}]] au lieu de {{{{{scalar}}}}}."
                    : $"retour : variable inconnue {{{{{scalar}}}}}.";
            }
        }

        foreach (var list in parse.Dependencies.RequiredLists)
        {
            if (!availableLists.Contains(list))
            {
                return availableScalars.Contains(list)
                    ? $"retour : [[{list}]] est une variable simple ; utilise {list} ou {{{{{list}}}}}."
                    : $"retour : liste inconnue [[{list}]].";
            }
        }

        foreach (var identifier in parse.Dependencies.AmbiguousIdentifiers)
        {
            if (!availableScalars.Contains(identifier) &&
                !availableLists.Contains(identifier) &&
                identifier != "ip")
            {
                return $"retour : variable ou liste inconnue « {identifier} » (extrais-la d'abord ou entoure le texte de guillemets \"...\").";
            }
        }

        return null;
    }

    private static List<BatchAxis> ResolveRuntimeAxes(
        IReadOnlyList<BatchAxis> rawAxes,
        HashSet<string> usedListNames,
        Dictionary<string, IReadOnlyList<string>> accumulatedLists,
        out string? emptyListName)
    {
        emptyListName = null;
        var resolved = new List<BatchAxis>(rawAxes.Count + usedListNames.Count);
        var includedLists = new HashSet<string>(StringComparer.Ordinal);

        foreach (var axis in rawAxes)
        {
            if (axis.Kind == BatchAxisKind.ExtractedList)
            {
                includedLists.Add(axis.Name);
                if (!accumulatedLists.TryGetValue(axis.Name, out var listValues) || listValues.Count == 0)
                {
                    emptyListName ??= axis.Name;
                }
                else
                {
                    resolved.Add(BatchAxis.CreateList(BatchAxisKind.ExtractedList, axis.Name, listValues));
                }
            }
            else
            {
                resolved.Add(axis);
            }
        }

        foreach (var listName in usedListNames)
        {
            if (includedLists.Contains(listName))
            {
                continue;
            }

            if (!accumulatedLists.TryGetValue(listName, out var listValues) || listValues.Count == 0)
            {
                emptyListName ??= listName;
            }
            else
            {
                resolved.Add(BatchAxis.CreateList(BatchAxisKind.ExtractedList, listName, listValues));
            }
        }

        return resolved;
    }

    private static Dictionary<string, IReadOnlyList<string>> CloneListsSnapshot(
        Dictionary<string, IReadOnlyList<string>> source)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(source.Count, StringComparer.Ordinal);
        foreach (var kvp in source)
        {
            copy[kvp.Key] = [.. kvp.Value];
        }
        return copy;
    }

    private static void ApplyDefaultsOnMissingExtractions(
        IReadOnlyList<ChainExtraction> extractions,
        Dictionary<string, string> extractedThisIteration,
        Dictionary<string, IReadOnlyList<string>> extractedListsThisIteration,
        Dictionary<string, string> accumulatedVariables,
        Dictionary<string, IReadOnlyList<string>> accumulatedLists)
    {
        foreach (var ext in extractions)
        {
            if (string.IsNullOrWhiteSpace(ext.VariableName))
            {
                continue;
            }

            var name = ext.VariableName.Trim();
            if (ext.IsList)
            {
                IReadOnlyList<string> fallbackList = string.IsNullOrEmpty(ext.DefaultValueIfMissing)
                    ? []
                    : [ext.DefaultValueIfMissing];
                extractedListsThisIteration[name] = fallbackList;
                accumulatedLists[name] = fallbackList;
            }
            else
            {
                var fallback = ext.DefaultValueIfMissing ?? string.Empty;
                extractedThisIteration[name] = fallback;
                accumulatedVariables[name] = fallback;
            }
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

    private static ChainStepResult CreateEmptyListSkippedResult(int globalIndex, int stepNumber, string stepName, string listName) => new()
    {
        Index = globalIndex,
        StepIndex = stepNumber,
        StepName = stepName,
        Iteration = 1,
        TotalIterationsForStep = 1,
        RequestSummary = $"(ignorée : liste [[{listName}]] vide)",
        Status = ChainStepStatus.Skipped,
        StatusCategory = StatusCategory.None,
        ErrorMessage = $"La liste extraite [[{listName}]] est vide : aucune requête envoyée pour cette étape.",
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
