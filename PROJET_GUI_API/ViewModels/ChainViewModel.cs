using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ApiTester.ViewModels;

public partial class ChainViewModel : ViewModelBase
{
    private readonly ICurlParser _curlParser;
    private readonly ITemplateEngine _templateEngine;
    private readonly IChainRunner _chainRunner;
    private readonly IResponseExtractor _responseExtractor;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IBatchResultWriter _batchResultWriter;
    private readonly IOverlayEditorService? _overlayEditor;
    private readonly ICookieEditorService? _cookieEditor;
    private readonly IClipboardService? _clipboardService;
    private readonly IChainCliGenerator _chainCliGenerator;
    private readonly IChainPipelineFileService _pipelineFileService;
    private readonly IStepReturnEvaluator _stepReturnEvaluator;
    private readonly IExtractionSuggester _extractionSuggester;
    private readonly RateLimitPolicy _sharedRateLimitPolicy;
    private CancellationTokenSource? _liveExtractionTestCts;
    private Dictionary<string, string> _lastAccumulatedVariables = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<string>> _lastAccumulatedLists = new(StringComparer.Ordinal);
    private readonly List<ChainStepResult> _rawResults = [];
    private string? _cachedSyntheticSuggestionKey;
    private ResponseModel? _cachedSyntheticSuggestionResponse;
    private bool _isBulkUpdating;
    private bool _isSortingResults;
    private string? _lastPipelineDirectory;
    private string? _lastPipelineFileName;

    public ChainViewModel(
        ICurlParser curlParser,
        ITemplateEngine templateEngine,
        IChainRunner chainRunner,
        IResponseExtractor responseExtractor,
        IFolderPickerService folderPickerService,
        IBatchResultWriter batchResultWriter,
        BatchViewModel? batchViewModel = null,
        IOverlayEditorService? overlayEditor = null,
        IChainCliGenerator? chainCliGenerator = null,
        ICookieEditorService? cookieEditor = null,
        IStepReturnEvaluator? stepReturnEvaluator = null,
        IClipboardService? clipboardService = null,
        IChainPipelineFileService? pipelineFileService = null,
        IExtractionSuggester? extractionSuggester = null)
    {
        _curlParser = curlParser ?? throw new ArgumentNullException(nameof(curlParser));
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        _chainRunner = chainRunner ?? throw new ArgumentNullException(nameof(chainRunner));
        _responseExtractor = responseExtractor ?? throw new ArgumentNullException(nameof(responseExtractor));
        _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        _batchResultWriter = batchResultWriter ?? throw new ArgumentNullException(nameof(batchResultWriter));
        _overlayEditor = overlayEditor;
        _cookieEditor = cookieEditor;
        _clipboardService = clipboardService;
        _chainCliGenerator = chainCliGenerator ?? new ChainCliGenerator();
        _pipelineFileService = pipelineFileService ?? new ChainPipelineFileService();
        _stepReturnEvaluator = stepReturnEvaluator ?? new StepReturnEvaluator();
        _extractionSuggester = extractionSuggester ?? new ExtractionSuggester();
        _sharedRateLimitPolicy = batchViewModel?.RateLimitPolicy ?? new RateLimitPolicy();
        SelectedResponse = new ResponseViewModel(overlayEditor);

        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasChainStats));
        };
        DeclaredVariables.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDeclaredVariables));
        Steps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSteps));
            GenerateCliCommand.NotifyCanExecuteChanged();
            SavePipelineCommand.NotifyCanExecuteChanged();
        };
        SelectedResponse.ExtractRequested += (_, args) => ExtractToLabCallback?.Invoke(args.Body, args.ContentType);

        AddStep();
    }

    public static IReadOnlyList<HttpMethodType> HttpMethods { get; } = Enum.GetValues<HttpMethodType>();
    public static IReadOnlyList<BodyType> BodyTypes { get; } = RequestViewModel.BodyTypes;
    public static IReadOnlyList<BatchMode> BatchModes { get; } = BatchViewModel.BatchModes;
    public static IReadOnlyList<ExtractionKind> ExtractionKinds { get; } = Enum.GetValues<ExtractionKind>();
    public static IReadOnlyList<ChainExtractionSource> ExtractionSources { get; } = Enum.GetValues<ChainExtractionSource>();
    public static IReadOnlyList<SaveMode> SaveModes { get; } = BatchViewModel.SaveModes;
    public static IReadOnlyList<GroupedFormat> GroupedFormats { get; } = BatchViewModel.GroupedFormats;
    public static IReadOnlyList<ChainCliLanguage> CliLanguages { get; } = Enum.GetValues<ChainCliLanguage>();
    public static IReadOnlyList<ChainResultSortMode> ResultSortModes { get; } = Enum.GetValues<ChainResultSortMode>();

    public ChainDefinition Definition { get; } = new();

    public ObservableCollection<ChainStep> Steps => Definition.Steps;

    public bool HasSteps => Steps.Count > 0;

    public IExtractionSuggester ExtractionSuggester => _extractionSuggester;

    public Func<ChainExtraction, IReadOnlyList<RealtimeExtractionSuggestion>> GetSourceVariableSuggestionsFunc =>
        GetAvailableSourceVariables;

    public Func<ChainExtraction, ResponseModel?> GetSuggestionResponseFunc =>
        GetSuggestionResponse;

    public RateLimitPolicy GlobalRateLimitPolicy => _sharedRateLimitPolicy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStep))]
    [NotifyPropertyChangedFor(nameof(AvailableExtractedVariablesText))]
    [NotifyPropertyChangedFor(nameof(HasAvailableExtractedVariables))]
    public partial ChainStep? SelectedStep { get; set; }

    public bool HasSelectedStep => SelectedStep is not null;

    public string AvailableExtractedVariablesText
    {
        get
        {
            if (SelectedStep is null)
            {
                return string.Empty;
            }

            var tokens = GetAvailableExtractedTokensBefore(SelectedStep);
            return tokens.Count == 0
                ? string.Empty
                : $"Variables extraites des étapes précédentes : {string.Join(", ", tokens)}";
        }
    }

    public bool HasAvailableExtractedVariables => !string.IsNullOrWhiteSpace(AvailableExtractedVariablesText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalRequestsText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial int TotalRequests { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalRequestsText))]
    public partial bool HasDynamicRequestsCount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool NeedsConfirmation { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsConfirmed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool ExceedsHardLimit { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanError))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? PlanError { get; private set; }

    public bool HasPlanError => !string.IsNullOrWhiteSpace(PlanError);

    public ObservableCollection<string> ChainWarnings { get; } = [];

    public bool HasChainWarnings => ChainWarnings.Count > 0;

    public string TotalRequestsText => HasDynamicRequestsCount
        ? (TotalRequests <= 1 ? "≥ 1 requête (listes dynamiques)" : $"≥ {TotalRequests} requêtes (listes dynamiques)")
        : (TotalRequests <= 1 ? $"{TotalRequests} requête prévue" : $"{TotalRequests} requêtes prévues");

    [ObservableProperty]
    public partial bool KeepBodies { get; set; } = true;

    [ObservableProperty]
    public partial ChainCliLanguage SelectedCliLanguage { get; set; } = ChainCliLanguage.Python;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChainStats))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveStepCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStepUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveStepDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial int ProgressCurrent { get; set; }

    [ObservableProperty]
    public partial int ProgressTotal { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SuccessCount { get; private set; }

    [ObservableProperty]
    public partial int FailedCount { get; private set; }

    [ObservableProperty]
    public partial int SkippedCount { get; private set; }

    [ObservableProperty]
    public partial int RetryCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFinalStatusText))]
    public partial string FinalStatusText { get; private set; } = string.Empty;

    public bool HasFinalStatusText => !string.IsNullOrWhiteSpace(FinalStatusText);

    public bool HasChainStats => IsRunning || Results.Count > 0;

    public string ChainStatsSummaryText =>
        $"{SuccessCount} {(SuccessCount <= 1 ? "réussie" : "réussies")}, " +
        $"{FailedCount} {(FailedCount <= 1 ? "échouée" : "échouées")}, " +
        $"{SkippedCount} {(SkippedCount <= 1 ? "ignorée" : "ignorées")}" +
        (RetryCount > 0 ? $" · {RetryCount} {(RetryCount <= 1 ? "nouvelle tentative" : "nouvelles tentatives")}" : string.Empty);

    public ObservableCollection<ChainDeclaredVariableItem> DeclaredVariables { get; } = [];

    public bool HasDeclaredVariables => DeclaredVariables.Count > 0;

    public ObservableCollection<ChainStepResult> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    public partial ChainResultSortMode SelectedResultSortMode { get; set; } = ChainResultSortMode.ChronologicalAsc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedReturnValue))]
    [NotifyPropertyChangedFor(nameof(SelectedReturnPreviewText))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedReturnCommand))]
    [NotifyCanExecuteChangedFor(nameof(InspectSelectedReturnCommand))]
    public partial ChainStepResult? SelectedResult { get; set; }

    public bool HasSelectedReturnValue => SelectedResult?.HasReturnValue == true;

    public string SelectedReturnPreviewText => SelectedResult is { HasReturnValue: true, ReturnValue: { } ret }
        ? BatchExecutionHelper.TruncateForDisplay(ret)
        : string.Empty;

    public ResponseViewModel SelectedResponse { get; }

    public Action<string, string>? ExtractToLabCallback { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPerRequestOptions))]
    [NotifyPropertyChangedFor(nameof(ShowGroupedOptions))]
    [NotifyPropertyChangedFor(nameof(CanChooseFolder))]
    public partial SaveMode SaveMode { get; set; } = SaveMode.None;

    [ObservableProperty]
    public partial string OutputFolder { get; set; } = GetDefaultOutputFolder();

    [ObservableProperty]
    public partial GroupedFormat GroupedFormat { get; set; } = GroupedFormat.JsonLines;

    [ObservableProperty]
    public partial bool CreateTimestampSubfolder { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeSummaryCsv { get; set; } = true;

    public bool ShowPerRequestOptions => SaveMode is SaveMode.PerRequest or SaveMode.Both;
    public bool ShowGroupedOptions => SaveMode is SaveMode.Grouped or SaveMode.Both;
    public bool CanChooseFolder => SaveMode != SaveMode.None;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    public partial string LastOutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveSummary))]
    public partial string SaveSummaryText { get; set; } = string.Empty;

    public bool HasSaveSummary => !string.IsNullOrWhiteSpace(SaveSummaryText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string? SaveError { get; set; }

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePipelineCommand))]
    public partial bool IsSavingPipeline { get; set; }

    [ObservableProperty]
    public partial string LastSavedPipelinePath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPipelineSaveStatus))]
    public partial string PipelineSaveStatusText { get; set; } = string.Empty;

    public bool HasPipelineSaveStatus => !string.IsNullOrWhiteSpace(PipelineSaveStatusText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPipelineSaveError))]
    public partial string? PipelineSaveError { get; set; }

    public bool HasPipelineSaveError => !string.IsNullOrWhiteSpace(PipelineSaveError);

    partial void OnSelectedStepChanged(ChainStep? value)
    {
        CancelPendingLiveExtractionTest();
        OnPropertyChanged(nameof(AvailableExtractedVariablesText));
        OnPropertyChanged(nameof(HasAvailableExtractedVariables));
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedResultSortModeChanged(ChainResultSortMode value)
    {
        RebuildSortedResults();
    }

    partial void OnSelectedResultChanged(ChainStepResult? value)
    {
        if (_isSortingResults)
        {
            return;
        }

        if (value is null)
        {
            SelectedResponse.Clear();
            return;
        }

        if (value.Response is not null)
        {
            SelectedResponse.Update(value.Response);
            return;
        }

        if (!string.IsNullOrWhiteSpace(value.ErrorMessage))
        {
            SelectedResponse.SetError(value.ErrorMessage);
            return;
        }

        SelectedResponse.Update(new ResponseModel
        {
            StatusCode = value.StatusCode,
            ReasonPhrase = string.Empty,
            IsSuccess = value.Status == ChainStepStatus.Success,
            DurationMs = value.DurationMs,
            SizeBytes = 0,
            ContentType = "text/plain",
            Body = value.Status == ChainStepStatus.Skipped
                ? "(Étape ignorée suite à un échec précédent.)"
                : "(Corps non conservé — coche « Conserver les corps » avant le lancement.)"
        });
    }

    private bool CanModifySteps() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanModifySteps))]
    private void AddStep()
    {
        var step = new ChainStep
        {
            Name = $"Étape {Steps.Count + 1}",
            DelayAfterMs = 500
        };
        AttachStepHandlers(step);
        Steps.Add(step);
        SelectedStep = step;
        RefreshAllStepsAndPlan();
    }

    [RelayCommand(CanExecute = nameof(CanModifySteps))]
    private void DuplicateStep(ChainStep? step)
    {
        if (step is null)
        {
            return;
        }

        var clone = step.SnapshotForRun();
        clone.Name = $"{step.Name} (copie)";
        clone.LastResponse = step.LastResponse;
        clone.LastResponseSummary = step.LastResponseSummary;

        AttachStepHandlers(clone);
        var idx = Steps.IndexOf(step);
        if (idx >= 0)
        {
            Steps.Insert(idx + 1, clone);
        }
        else
        {
            Steps.Add(clone);
        }

        SelectedStep = clone;
        RefreshAllStepsAndPlan();
    }

    private bool CanRemoveStep(ChainStep? step) => !IsRunning && step is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveStep))]
    private void RemoveStep(ChainStep? step)
    {
        if (step is null)
        {
            return;
        }

        var idx = Steps.IndexOf(step);
        if (idx < 0)
        {
            return;
        }

        DetachStepHandlers(step);
        Steps.RemoveAt(idx);

        if (ReferenceEquals(SelectedStep, step))
        {
            SelectedStep = Steps.Count > 0 ? Steps[Math.Min(idx, Steps.Count - 1)] : null;
        }

        RefreshAllStepsAndPlan();
    }

    private bool CanMoveStepUp(ChainStep? step) => !IsRunning && step is not null && step.CanMoveUp;

    [RelayCommand(CanExecute = nameof(CanMoveStepUp))]
    private void MoveStepUp(ChainStep? step)
    {
        if (step is null)
        {
            return;
        }

        var idx = Steps.IndexOf(step);
        if (idx <= 0)
        {
            return;
        }

        Steps.Move(idx, idx - 1);
        SelectedStep = step;
        RefreshAllStepsAndPlan();
    }

    private bool CanMoveStepDown(ChainStep? step) => !IsRunning && step is not null && step.CanMoveDown;

    [RelayCommand(CanExecute = nameof(CanMoveStepDown))]
    private void MoveStepDown(ChainStep? step)
    {
        if (step is null)
        {
            return;
        }

        var idx = Steps.IndexOf(step);
        if (idx < 0 || idx >= Steps.Count - 1)
        {
            return;
        }

        Steps.Move(idx, idx + 1);
        SelectedStep = step;
        RefreshAllStepsAndPlan();
    }

    [RelayCommand]
    private void EnlargeStepCurl()
    {
        if (SelectedStep is null)
        {
            return;
        }

        var step = SelectedStep;
        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            "Commande curl",
            step.CurlText,
            IsReadOnly: false,
            OnSave: text => step.CurlText = text));
    }

    [RelayCommand]
    private void EnlargeStepBody()
    {
        if (SelectedStep is null)
        {
            return;
        }

        var step = SelectedStep;
        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            "Corps de la requête",
            step.Body,
            IsReadOnly: false,
            OnSave: text => step.Body = text));
    }

    [RelayCommand]
    private void EnlargeVariableList(BatchVariable? variable)
    {
        if (variable is null)
        {
            return;
        }

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            $"Liste de valeurs : {variable.Name}",
            variable.ListText,
            IsReadOnly: false,
            OnSave: text => variable.ListText = text));
    }

    [RelayCommand]
    private void EnlargeStepReturn()
    {
        if (SelectedStep is null)
        {
            return;
        }

        var step = SelectedStep;
        var title = string.IsNullOrWhiteSpace(step.Name)
            ? "Expression de retour"
            : $"Expression de retour — {step.Name.Trim()}";

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            title,
            step.ReturnExpression,
            IsReadOnly: false,
            OnSave: text => step.ReturnExpression = text));
    }

    [RelayCommand]
    private async Task TestStepReturnAsync()
    {
        if (SelectedStep is null)
        {
            return;
        }

        var step = SelectedStep;
        if (!step.HasReturnExpression)
        {
            step.LastReturnValue = string.Empty;
            step.ReturnError = "Saisis une expression de retour à tester (ex. return ma_variable).";
            return;
        }

        var mergedScalars = new Dictionary<string, string>(_lastAccumulatedVariables, StringComparer.Ordinal);
        var mergedLists = _lastAccumulatedLists.ToDictionary(
            k => k.Key,
            v => v.Value,
            StringComparer.Ordinal);

        foreach (var candidate in Steps)
        {
            foreach (var v in candidate.Variables)
            {
                if (!v.IsUsed || !v.IsConfigured || v.HasError || string.IsNullOrWhiteSpace(v.Name))
                {
                    continue;
                }

                if (!v.IsVarying)
                {
                    mergedScalars[v.Name] = v.FixedValue;
                }
                else
                {
                    var axis = candidate.Axes.FirstOrDefault(a =>
                        a.Kind == BatchAxisKind.Variable &&
                        string.Equals(a.Name, v.Name, StringComparison.Ordinal));
                    if (axis is not null && axis.ValueCount > 0)
                    {
                        mergedScalars[v.Name] = axis.GetValue(0);
                    }
                }
            }

            foreach (var ext in candidate.Extractions)
            {
                if (string.IsNullOrWhiteSpace(ext.VariableName))
                {
                    continue;
                }

                var varName = ext.VariableName.Trim();
                if (ext.IsList)
                {
                    if (ext.LastExtractedList is { Count: > 0 } testedList)
                    {
                        mergedLists[varName] = testedList;
                    }
                }
                else if (ext.LastExtractedValue is not null)
                {
                    mergedScalars[varName] = ext.LastExtractedValue;
                }
            }

            if (ReferenceEquals(candidate, step))
            {
                break;
            }
        }

        var activeListItems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var listMarker in _templateEngine.ExtractListVariables(step.BuildTemplateRequest()))
        {
            if (mergedLists.TryGetValue(listMarker.Name, out var listVals) && listVals.Count > 0)
            {
                activeListItems[listMarker.Name] = listVals[0];
            }
            else if (VariablePathHelper.TrySplit(listMarker.Name, out var listRoot, out var listJsonPath) &&
                     listJsonPath is not null &&
                     mergedScalars.TryGetValue(listRoot, out var rootJson))
            {
                var jpList = ResponseExtractor.ExtractListJsonPathFromBody(rootJson, listJsonPath);
                if (jpList.IsSuccess && jpList.Values.Count > 0)
                {
                    activeListItems[listMarker.Name] = jpList.Values[0];
                }
            }
        }

        string? resolvedIp = null;
        if (IpResolutionHelper.ReturnExpressionMayUseIp(step.ReturnExpression))
        {
            resolvedIp = await IpResolutionHelper.ResolveIpAsync();
        }

        var eval = _stepReturnEvaluator.Evaluate(
            step.ReturnExpression,
            mergedScalars,
            activeListItems,
            mergedLists,
            globalIndex: 1,
            resolvedIp: resolvedIp);

        if (eval.IsSuccess)
        {
            step.ReturnError = null;
            step.LastReturnValue = eval.FormattedValue;
        }
        else
        {
            step.ReturnError = eval.ErrorMessage;
        }
    }

    [RelayCommand]
    private async Task CopyStepReturnAsync()
    {
        if (SelectedStep is not { HasLastReturnValue: true } step || _clipboardService is null)
        {
            return;
        }

        await _clipboardService.SetTextAsync(step.LastReturnValue);
    }

    [RelayCommand]
    private void InspectStepReturn()
    {
        if (SelectedStep is not { HasLastReturnValue: true } step)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(step.Name)
            ? "Valeur retournée"
            : $"Valeur retournée — {step.Name.Trim()}";

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            title,
            step.LastReturnValue,
            IsReadOnly: true,
            OnSave: null));
    }

    private bool CanActOnSelectedReturn() => SelectedResult?.HasReturnValue == true;

    [RelayCommand(CanExecute = nameof(CanActOnSelectedReturn))]
    private async Task CopySelectedReturnAsync()
    {
        if (SelectedResult?.ReturnValue is null || _clipboardService is null)
        {
            return;
        }

        await _clipboardService.SetTextAsync(SelectedResult.ReturnValue);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelectedReturn))]
    private void InspectSelectedReturn()
    {
        if (SelectedResult is not { HasReturnValue: true, ReturnValue: { } value })
        {
            return;
        }

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            $"Valeur retournée — {SelectedResult.StepIterationLabel}",
            value,
            IsReadOnly: true,
            OnSave: null));
    }

    [RelayCommand]
    private async Task CopyResultAsCurlAsync(ChainStepResult? result)
    {
        if (result is null || !result.CanCopyAsCurl || result.ExecutedRequest is null || _clipboardService is null)
        {
            return;
        }

        var curlCommand = CurlCommandBuilder.Build(result.ExecutedRequest);
        if (!string.IsNullOrWhiteSpace(curlCommand))
        {
            await _clipboardService.SetTextAsync(curlCommand);
        }
    }

    [RelayCommand]
    private void ParseStepCurl()
    {
        if (SelectedStep is null)
        {
            return;
        }

        var step = SelectedStep;
        var parseResult = _curlParser.Parse(step.CurlText);

        step.ParseWarnings.Clear();
        step.ParseWarnings.AddRange(parseResult.Warnings);

        _isBulkUpdating = true;
        try
        {
            var req = parseResult.Request;
            step.Method = req.Method;
            step.Url = req.Url;
            step.Body = req.Body;
            step.BodyType = req.BodyType;
            step.ParsedAuth = req.Auth;
            step.ParsedTimeoutSeconds = req.TimeoutSeconds > 0 ? req.TimeoutSeconds : 30;

            step.Headers.Clear();
            foreach (var header in req.Headers)
            {
                step.Headers.Add(new KeyValueItem(header.Key, header.Value, header.IsEnabled));
            }

            step.FormFields.Clear();
            foreach (var field in req.FormFields)
            {
                step.FormFields.Add(new KeyValueItem(field.Key, field.Value, field.IsEnabled));
            }

            step.VaryingParams.Clear();
            foreach (var qp in req.QueryParams)
            {
                step.VaryingParams.Add(new VaryingParam(qp.Key, qp.Value, qp.IsEnabled));
            }

            step.HasParsed = !string.IsNullOrWhiteSpace(step.Url);
            IsConfirmed = false;
        }
        finally
        {
            _isBulkUpdating = false;
        }

        RefreshAllStepsAndPlan();
    }

    [RelayCommand]
    private void InitManualStep()
    {
        if (SelectedStep is null)
        {
            return;
        }

        SelectedStep.HasParsed = true;
        RefreshAllStepsAndPlan();
    }

    [RelayCommand]
    private void AddStepHeader() => SelectedStep?.Headers.Add(new KeyValueItem());

    [RelayCommand]
    private void RemoveStepHeader(KeyValueItem? item)
    {
        if (item is not null)
        {
            SelectedStep?.Headers.Remove(item);
        }
    }

    [RelayCommand]
    private void EditCookieHeader(KeyValueItem? item)
    {
        if (item is null)
        {
            return;
        }

        _cookieEditor?.RequestOpen(new CookieEditorRequest(
            item.Value,
            updatedHeader => item.Value = updatedHeader));
    }

    [RelayCommand]
    private void AddStepFormField() => SelectedStep?.FormFields.Add(new KeyValueItem());

    [RelayCommand]
    private void RemoveStepFormField(KeyValueItem? item)
    {
        if (item is not null)
        {
            SelectedStep?.FormFields.Remove(item);
        }
    }

    [RelayCommand]
    private void AddStepVaryingParam() => SelectedStep?.VaryingParams.Add(new VaryingParam());

    [RelayCommand]
    private void RemoveStepVaryingParam(VaryingParam? item)
    {
        if (item is not null)
        {
            SelectedStep?.VaryingParams.Remove(item);
        }
    }

    [RelayCommand]
    private void AddExtraction()
    {
        if (SelectedStep is null)
        {
            return;
        }

        SelectedStep.Extractions.Add(new ChainExtraction());
        RefreshAllStepsAndPlan();
    }

    [RelayCommand]
    private void RemoveExtraction(ChainExtraction? item)
    {
        if (item is not null && SelectedStep is not null)
        {
            SelectedStep.Extractions.Remove(item);
            RefreshAllStepsAndPlan();
        }
    }

    [RelayCommand]
    private void TestExtraction(ChainExtraction? extraction)
    {
        CancelPendingLiveExtractionTest();
        EvaluateExtraction(extraction, allowEmptyVariablePreview: false);
    }

    private void EvaluateExtraction(ChainExtraction? extraction, bool allowEmptyVariablePreview)
    {
        if (SelectedStep is null || extraction is null)
        {
            return;
        }

        if (extraction.IsVariableSource)
        {
            EvaluateVariableSourceExtraction(SelectedStep, extraction, allowEmptyVariablePreview);
            return;
        }

        var response = SelectedStep.LastResponse;
        if (response is null)
        {
            extraction.LastExtractedValue = null;
            extraction.ErrorText = "Exécute d'abord cette étape pour disposer d'une réponse à tester.";
            return;
        }

        if (allowEmptyVariablePreview &&
            extraction.Kind != ExtractionKind.FullResponse &&
            string.IsNullOrWhiteSpace(extraction.Expression) &&
            (extraction.Source != ChainExtractionSource.Header || string.IsNullOrWhiteSpace(extraction.HeaderName)))
        {
            extraction.ClearPreviewState();
            return;
        }

        var targetForEvaluation = extraction;
        if (allowEmptyVariablePreview && string.IsNullOrWhiteSpace(extraction.VariableName))
        {
            targetForEvaluation = extraction.Clone();
            targetForEvaluation.VariableName = "preview";
        }

        if (extraction.IsList)
        {
            var rawOutcome = _responseExtractor.ExtractRawList(response, targetForEvaluation);
            ApplyListEvaluationOutcome(extraction, rawOutcome);
            return;
        }

        ApplyScalarEvaluationOutcome(extraction, _responseExtractor.Extract(response, targetForEvaluation));
    }

    private void EvaluateVariableSourceExtraction(
        ChainStep step,
        ChainExtraction extraction,
        bool allowEmptyVariablePreview)
    {
        if (allowEmptyVariablePreview &&
            (string.IsNullOrWhiteSpace(extraction.SourceVariableName) ||
             (extraction.Kind != ExtractionKind.FullResponse && string.IsNullOrWhiteSpace(extraction.Expression))))
        {
            extraction.ClearPreviewState();
            return;
        }

        if (string.IsNullOrWhiteSpace(extraction.SourceVariableName))
        {
            extraction.LastRawExtractedList = null;
            extraction.LastExtractedList = null;
            extraction.LastExtractedValue = null;
            extraction.ErrorText = "Saisis d'abord le nom de la variable source.";
            RefreshDeclaredVariables();
            return;
        }

        var sourceValues = ResolvePreviewSourceVariableValues(step, extraction, extraction.SourceVariableName);
        if (sourceValues.Count == 0)
        {
            extraction.LastRawExtractedList = null;
            extraction.LastExtractedList = null;
            extraction.LastExtractedValue = null;
            extraction.ErrorText =
                $"Aucune valeur disponible pour la variable source « {extraction.SourceVariableName.Trim()} » (exécute ou teste d'abord son extraction).";
            RefreshDeclaredVariables();
            return;
        }

        var targetForEvaluation = extraction;
        if (allowEmptyVariablePreview && string.IsNullOrWhiteSpace(extraction.VariableName))
        {
            targetForEvaluation = extraction.Clone();
            targetForEvaluation.VariableName = "preview";
        }

        if (extraction.IsList)
        {
            var rawOutcome = _responseExtractor.ExtractRawListFromVariable(sourceValues, targetForEvaluation);
            ApplyListEvaluationOutcome(extraction, rawOutcome);
            return;
        }

        ApplyScalarEvaluationOutcome(extraction, _responseExtractor.ExtractFromVariable(sourceValues, targetForEvaluation));
    }

    private void ApplyListEvaluationOutcome(ChainExtraction extraction, ListExtractionOutcome rawOutcome)
    {
        if (!rawOutcome.IsSuccess)
        {
            extraction.LastRawExtractedList = null;
            extraction.LastExtractedList = null;
            extraction.LastExtractedValue = null;
            extraction.ErrorText = rawOutcome.ErrorMessage;
            RefreshDeclaredVariables();
            return;
        }

        extraction.LastRawExtractedList = rawOutcome.Values;
        var finalValues = rawOutcome.Values;
        if (extraction.HasConditions)
        {
            var filteredOutcome = ExtractionConditionEngine.ApplyToList(
                rawOutcome.Values,
                extraction.Conditions,
                extraction.ConditionMatchMode,
                extraction.DeduplicateList,
                failWhenEmpty: true);
            if (!filteredOutcome.IsSuccess)
            {
                extraction.LastExtractedList = null;
                extraction.LastExtractedValue = null;
                extraction.ErrorText = filteredOutcome.ErrorMessage;
                RefreshDeclaredVariables();
                return;
            }

            finalValues = filteredOutcome.Values;
        }

        extraction.LastExtractedList = finalValues;
        if (BatchExecutionHelper.IsSensitiveName(extraction.VariableName))
        {
            extraction.LastExtractedValue = $"[{finalValues.Count} éléments] ***";
        }
        else
        {
            var preview = BatchExecutionHelper.TruncateForDisplay(string.Join(", ", finalValues.Take(5)));
            extraction.LastExtractedValue = $"[{finalValues.Count} éléments] {preview}";
        }
        extraction.ErrorText = null;
        RefreshDeclaredVariables();
    }

    private void ApplyScalarEvaluationOutcome(ChainExtraction extraction, ExtractionOutcome outcome)
    {
        extraction.LastRawExtractedList = null;
        extraction.LastExtractedList = null;
        if (outcome.IsSuccess)
        {
            extraction.LastExtractedValue = outcome.Value;
            extraction.ErrorText = null;
        }
        else
        {
            extraction.LastExtractedValue = null;
            extraction.ErrorText = outcome.ErrorMessage;
        }

        RefreshDeclaredVariables();
    }

    public IReadOnlyList<RealtimeExtractionSuggestion> GetAvailableSourceVariables(ChainExtraction? targetExtraction)
    {
        if (SelectedStep is null || targetExtraction is null)
        {
            return [];
        }

        var result = new List<RealtimeExtractionSuggestion>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var currentStepIdx = Steps.IndexOf(SelectedStep);
        if (currentStepIdx < 0)
        {
            currentStepIdx = Steps.Count;
        }

        void AddOrUpdateCandidate(string name, bool isList, IReadOnlyList<string> values, string detailText)
        {
            var previewText = string.Empty;
            var badgeText = isList ? "liste" : "var";
            if (values.Count > 0)
            {
                if (BatchExecutionHelper.IsSensitiveName(name))
                {
                    previewText = "***";
                }
                else if (isList || values.Count > 1)
                {
                    var sample = BatchExecutionHelper.TruncateForDisplay(string.Join(", ", values.Take(3)), 64);
                    previewText = $"[{values.Count} élém.] {sample}";
                }
                else
                {
                    previewText = BatchExecutionHelper.TruncateForDisplay(values[0] ?? string.Empty, 64);
                }

                if (isList || values.Count > 1)
                {
                    badgeText = $"liste ({values.Count})";
                }
            }

            var suggestion = new RealtimeExtractionSuggestion(
                Expression: name,
                SuggestedVariableName: string.Empty,
                PreviewValue: previewText,
                BadgeText: badgeText,
                DetailText: detailText,
                SuggestsList: false,
                MatchCount: values.Count);

            if (indexByName.TryGetValue(name, out var existingIdx))
            {
                result[existingIdx] = suggestion;
            }
            else
            {
                indexByName[name] = result.Count;
                result.Add(suggestion);
            }
        }

        for (var s = 0; s < currentStepIdx; s++)
        {
            var prevStep = Steps[s];
            foreach (var ext in prevStep.Extractions)
            {
                if (string.IsNullOrWhiteSpace(ext.VariableName))
                {
                    continue;
                }

                var varName = ext.VariableName.Trim();
                var values = ResolveExtractionPreviewValues(prevStep, ext, []);
                AddOrUpdateCandidate(
                    varName,
                    ext.IsList,
                    values,
                    $"Étape {s + 1} · {GetKindShortLabel(ext.Kind)}");
            }
        }

        foreach (var v in SelectedStep.Variables)
        {
            if (!v.IsConfigured || v.HasError || string.IsNullOrWhiteSpace(v.Name))
            {
                continue;
            }

            var localValues = GetConfiguredVariablePreviewValues(v);
            AddOrUpdateCandidate(
                v.Name.Trim(),
                v.IsVarying && v.ValueCount > 1,
                localValues,
                "Variable locale de l'étape");
        }

        foreach (var ext in SelectedStep.Extractions)
        {
            if (ReferenceEquals(ext, targetExtraction))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(ext.VariableName))
            {
                continue;
            }

            var varName = ext.VariableName.Trim();
            var values = ResolveExtractionPreviewValues(SelectedStep, ext, []);
            AddOrUpdateCandidate(
                varName,
                ext.IsList,
                values,
                $"Même étape · {GetKindShortLabel(ext.Kind)}");
        }

        return result;
    }

    public ResponseModel? GetSuggestionResponse(ChainExtraction? extraction)
    {
        if (SelectedStep is null || extraction is null)
        {
            return null;
        }

        if (!extraction.IsVariableSource)
        {
            return SelectedStep.LastResponse;
        }

        var sourceVar = extraction.SourceVariableName?.Trim();
        if (string.IsNullOrEmpty(sourceVar))
        {
            return null;
        }

        var values = ResolvePreviewSourceVariableValues(SelectedStep, extraction, sourceVar);
        if (values.Count == 0)
        {
            return null;
        }

        string body;
        string contentType;
        if (values.Count == 1)
        {
            body = values[0] ?? string.Empty;
            contentType = DetectSyntheticContentType(body);
        }
        else if (TryBuildSyntheticJsonArray(values, out var jsonArray))
        {
            body = jsonArray;
            contentType = "application/json; charset=utf-8";
        }
        else
        {
            body = string.Join(Environment.NewLine, values);
            contentType = DetectSyntheticContentType(body);
        }

        var cacheKey = $"{sourceVar}\u0000{contentType}\u0000{body.Length}\u0000{body}";
        if (string.Equals(_cachedSyntheticSuggestionKey, cacheKey, StringComparison.Ordinal) &&
            _cachedSyntheticSuggestionResponse is not null)
        {
            return _cachedSyntheticSuggestionResponse;
        }

        var synthetic = new ResponseModel
        {
            StatusCode = 200,
            ReasonPhrase = "Variable",
            IsSuccess = true,
            ContentType = contentType,
            Body = body,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(body)
        };

        _cachedSyntheticSuggestionKey = cacheKey;
        _cachedSyntheticSuggestionResponse = synthetic;
        return synthetic;
    }

    private IReadOnlyList<string> ResolvePreviewSourceVariableValues(
        ChainStep currentStep,
        ChainExtraction targetExtraction,
        string? sourceVariableName,
        HashSet<string>? visited = null)
    {
        var key = sourceVariableName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            return [];
        }

        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(key))
        {
            return [];
        }

        try
        {
            // 1. Extractions situées strictement avant la règle cible dans la même étape (la dernière gagne).
            ChainExtraction? sameStepMatch = null;
            foreach (var ext in currentStep.Extractions)
            {
                if (ReferenceEquals(ext, targetExtraction))
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(ext.VariableName) &&
                    string.Equals(ext.VariableName.Trim(), key, StringComparison.Ordinal))
                {
                    sameStepMatch = ext;
                }
            }

            if (sameStepMatch is not null)
            {
                return ResolveExtractionPreviewValues(currentStep, sameStepMatch, visited);
            }

            // 2. Variable locale configurée sur l'étape courante.
            var localVar = currentStep.Variables.FirstOrDefault(v =>
                v.IsConfigured && !v.HasError && string.Equals(v.Name?.Trim(), key, StringComparison.Ordinal));
            if (localVar is not null)
            {
                return GetConfiguredVariablePreviewValues(localVar);
            }

            // 3. Extractions des étapes précédentes (en remontant pour privilégier la plus récente).
            var currentStepIdx = Steps.IndexOf(currentStep);
            var limitIdx = currentStepIdx >= 0 ? currentStepIdx : Steps.Count;
            for (var s = limitIdx - 1; s >= 0; s--)
            {
                var prevStep = Steps[s];
                for (var e = prevStep.Extractions.Count - 1; e >= 0; e--)
                {
                    var prevExt = prevStep.Extractions[e];
                    if (!string.IsNullOrWhiteSpace(prevExt.VariableName) &&
                        string.Equals(prevExt.VariableName.Trim(), key, StringComparison.Ordinal))
                    {
                        var directValues = ResolveExtractionPreviewValues(prevStep, prevExt, visited);
                        if (directValues.Count > 0)
                        {
                            return directValues;
                        }
                    }
                }
            }

            // 4. Dernières valeurs accumulées lors d'une exécution complète de la chaîne.
            if (_lastAccumulatedLists.TryGetValue(key, out var accList) && accList.Count > 0)
            {
                return accList;
            }

            if (_lastAccumulatedVariables.TryGetValue(key, out var accScalar))
            {
                return [accScalar];
            }

            return [];
        }
        finally
        {
            visited.Remove(key);
        }
    }

    private IReadOnlyList<string> ResolveExtractionPreviewValues(
        ChainStep ownerStep,
        ChainExtraction ext,
        HashSet<string> visited)
    {
        if (ext.IsList)
        {
            if (ext.LastExtractedList is { Count: > 0 } cachedList)
            {
                return cachedList;
            }

            var evalTarget = string.IsNullOrWhiteSpace(ext.VariableName) ? CloneWithPreviewName(ext) : ext;
            if (ext.IsVariableSource)
            {
                var parentValues = ResolvePreviewSourceVariableValues(ownerStep, ext, ext.SourceVariableName, visited);
                if (parentValues.Count > 0)
                {
                    var outcome = _responseExtractor.ExtractListFromVariable(parentValues, evalTarget);
                    if (outcome.IsSuccess && outcome.Values.Count > 0)
                    {
                        return outcome.Values;
                    }
                }
            }
            else if (ownerStep.LastResponse is not null)
            {
                var outcome = _responseExtractor.ExtractList(ownerStep.LastResponse, evalTarget);
                if (outcome.IsSuccess && outcome.Values.Count > 0)
                {
                    return outcome.Values;
                }
            }

            return [];
        }

        if (ext.LastExtractedValue is not null)
        {
            return [ext.LastExtractedValue];
        }

        var scalarTarget = string.IsNullOrWhiteSpace(ext.VariableName) ? CloneWithPreviewName(ext) : ext;
        if (ext.IsVariableSource)
        {
            var parentValues = ResolvePreviewSourceVariableValues(ownerStep, ext, ext.SourceVariableName, visited);
            if (parentValues.Count > 0)
            {
                var outcome = _responseExtractor.ExtractFromVariable(parentValues, scalarTarget);
                if (outcome.IsSuccess)
                {
                    return [outcome.Value];
                }
            }
        }
        else if (ownerStep.LastResponse is not null)
        {
            var outcome = _responseExtractor.Extract(ownerStep.LastResponse, scalarTarget);
            if (outcome.IsSuccess)
            {
                return [outcome.Value];
            }
        }

        return [];
    }

    private static ChainExtraction CloneWithPreviewName(ChainExtraction ext)
    {
        var copy = ext.Clone();
        copy.VariableName = "preview";
        return copy;
    }

    private static IReadOnlyList<string> GetConfiguredVariablePreviewValues(BatchVariable variable)
    {
        if (!variable.IsVarying)
        {
            return [variable.FixedValue ?? string.Empty];
        }

        if (variable.Mode == VariableMode.List)
        {
            return BatchVariable.ParseListValues(variable.ListText);
        }

        if (variable.ValueCount > 0)
        {
            try
            {
                var count = (int)Math.Min(variable.ValueCount, 10);
                var preview = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    preview.Add(variable.GetValue(i));
                }
                return preview;
            }
            catch (Exception)
            {
                // Ignorer si la plage n'est pas encore valide.
            }
        }

        return [];
    }

    private static string GetKindShortLabel(ExtractionKind kind) => kind switch
    {
        ExtractionKind.JsonPath => "JSONPath",
        ExtractionKind.Regex => "Regex",
        ExtractionKind.FullResponse => "Réponse complète",
        _ => kind.ToString()
    };

    private static string DetectSyntheticContentType(string body)
    {
        var trimmed = body.TrimStart('\uFEFF').TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return "application/json; charset=utf-8";
        }

        if (trimmed.StartsWith('<'))
        {
            return trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                ? "text/html; charset=utf-8"
                : "application/xml; charset=utf-8";
        }

        return "text/plain; charset=utf-8";
    }

    private static bool TryBuildSyntheticJsonArray(IReadOnlyList<string> values, out string jsonArray)
    {
        var jsonItems = new List<string>(Math.Min(values.Count, 20));
        var limit = Math.Min(values.Count, 20);
        for (var i = 0; i < limit; i++)
        {
            var trimmed = (values[i] ?? string.Empty).TrimStart('\uFEFF').Trim();
            if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            {
                jsonArray = string.Empty;
                return false;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                jsonItems.Add(trimmed);
            }
            catch (System.Text.Json.JsonException)
            {
                jsonArray = string.Empty;
                return false;
            }
        }

        if (jsonItems.Count == 0)
        {
            jsonArray = string.Empty;
            return false;
        }

        jsonArray = $"[{string.Join(",", jsonItems)}]";
        return true;
    }

    private async void ScheduleLiveExtractionTest(ChainExtraction extraction)
    {
        CancelPendingLiveExtractionTest();

        if (SelectedStep is null || (!extraction.IsVariableSource && SelectedStep.LastResponse is null))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _liveExtractionTestCts = cts;
        var expectedStep = SelectedStep;

        try
        {
            await Task.Delay(95, token);

            if (token.IsCancellationRequested ||
                !ReferenceEquals(_liveExtractionTestCts, cts) ||
                !ReferenceEquals(SelectedStep, expectedStep) ||
                (!extraction.IsVariableSource && expectedStep.LastResponse is null) ||
                !expectedStep.Extractions.Contains(extraction))
            {
                return;
            }

            EvaluateExtraction(extraction, allowEmptyVariablePreview: true);
        }
        catch (OperationCanceledException)
        {
            // Annulé par une saisie ultérieure ou un test explicite.
        }
        catch (ObjectDisposedException)
        {
            // CancellationTokenSource déjà libéré.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Erreur lors de l'évaluation en temps réel de l'extraction ({ErrorType}).", ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_liveExtractionTestCts, cts))
            {
                _liveExtractionTestCts = null;
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Ignorer si déjà libéré.
                }
            }
        }
    }

    private void CancelPendingLiveExtractionTest()
    {
        if (_liveExtractionTestCts is null)
        {
            return;
        }

        try
        {
            _liveExtractionTestCts.Cancel();
            _liveExtractionTestCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Ignore if already disposed.
        }
        finally
        {
            _liveExtractionTestCts = null;
        }
    }

    [RelayCommand]
    private void InspectExtractedList(ChainExtraction? extraction)
    {
        if (extraction is null || BatchExecutionHelper.IsSensitiveName(extraction.VariableName))
        {
            return;
        }

        IReadOnlyList<string>? rawItems = extraction.LastRawExtractedList;
        if (rawItems is null || rawItems.Count == 0)
        {
            if (extraction.IsVariableSource && SelectedStep is not null)
            {
                var sourceValues = ResolvePreviewSourceVariableValues(SelectedStep, extraction, extraction.SourceVariableName);
                if (sourceValues.Count > 0)
                {
                    var rawOutcome = _responseExtractor.ExtractRawListFromVariable(sourceValues, extraction);
                    if (rawOutcome.IsSuccess && rawOutcome.Values.Count > 0)
                    {
                        extraction.LastRawExtractedList = rawOutcome.Values;
                        rawItems = rawOutcome.Values;
                    }
                }
            }
            else if (SelectedStep?.LastResponse is not null)
            {
                var rawOutcome = _responseExtractor.ExtractRawList(SelectedStep.LastResponse, extraction);
                if (rawOutcome.IsSuccess && rawOutcome.Values.Count > 0)
                {
                    extraction.LastRawExtractedList = rawOutcome.Values;
                    rawItems = rawOutcome.Values;
                }
            }
        }

        if (rawItems is null || rawItems.Count == 0)
        {
            rawItems = extraction.LastExtractedList;
        }

        if (rawItems is null || rawItems.Count == 0)
        {
            return;
        }

        var varName = string.IsNullOrWhiteSpace(extraction.VariableName)
            ? "liste"
            : extraction.VariableName.Trim();

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            $"Liste [[{varName}]] — {rawItems.Count} élément(s)",
            string.Join(Environment.NewLine, extraction.LastExtractedList ?? rawItems),
            IsReadOnly: true,
            OnSave: null,
            ExtractionConditions: new ExtractionConditionsOverlayRequest(
                varName,
                rawItems,
                extraction.Conditions,
                extraction.ConditionMatchMode,
                extraction.DeduplicateList,
                (newConditions, newMode, newDedup) => ApplyExtractionConditions(extraction, rawItems, newConditions, newMode, newDedup))));
    }

    private void ApplyExtractionConditions(
        ChainExtraction extraction,
        IReadOnlyList<string> rawItems,
        IReadOnlyList<ChainExtractionCondition> newConditions,
        ExtractionConditionMatchMode newMode,
        bool newDedup)
    {
        _isBulkUpdating = true;
        try
        {
            extraction.Conditions.Clear();
            foreach (var cond in newConditions)
            {
                extraction.Conditions.Add(cond.Clone());
            }
            extraction.ConditionMatchMode = newMode;
            extraction.DeduplicateList = newDedup;
            extraction.NotifyConditionsChanged();
        }
        finally
        {
            _isBulkUpdating = false;
        }

        if (SelectedStep?.LastResponse is not null || extraction.IsVariableSource)
        {
            TestExtraction(extraction);
        }
        else
        {
            var filtered = ExtractionConditionEngine.ApplyToList(
                rawItems,
                extraction.Conditions,
                extraction.ConditionMatchMode,
                extraction.DeduplicateList,
                failWhenEmpty: true);
            if (filtered.IsSuccess)
            {
                extraction.LastExtractedList = filtered.Values;
                var preview = BatchExecutionHelper.TruncateForDisplay(string.Join(", ", filtered.Values.Take(5)));
                extraction.LastExtractedValue = $"[{filtered.Values.Count} éléments] {preview}";
                extraction.ErrorText = null;
            }
            else
            {
                extraction.LastExtractedList = null;
                extraction.LastExtractedValue = null;
                extraction.ErrorText = filtered.ErrorMessage;
            }
            RefreshDeclaredVariables();
        }

        RefreshAllStepsAndPlan();
    }

    [RelayCommand]
    private async Task CopyDeclaredVariableAsync(ChainDeclaredVariableItem? item)
    {
        if (item is null || !item.CanCopyOrInspect || _clipboardService is null)
        {
            return;
        }

        await _clipboardService.SetTextAsync(item.RawContentForCopy);
    }

    [RelayCommand]
    private void InspectDeclaredVariable(ChainDeclaredVariableItem? item)
    {
        if (item is null || !item.CanCopyOrInspect)
        {
            return;
        }

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            $"Contenu de {item.TokenName}",
            item.RawContentForCopy,
            IsReadOnly: true,
            OnSave: null));
    }

    [RelayCommand]
    private void MoveStepAxisUp(BatchAxis? axis)
    {
        if (SelectedStep is null || axis is null || SelectedStep.IsOrderIgnored)
        {
            return;
        }

        var idx = SelectedStep.Axes.IndexOf(axis);
        if (idx <= 0)
        {
            return;
        }

        SelectedStep.Axes.Move(idx, idx - 1);
        RefreshAxisMetadata(SelectedStep);
    }

    [RelayCommand]
    private void MoveStepAxisDown(BatchAxis? axis)
    {
        if (SelectedStep is null || axis is null || SelectedStep.IsOrderIgnored)
        {
            return;
        }

        var idx = SelectedStep.Axes.IndexOf(axis);
        if (idx < 0 || idx >= SelectedStep.Axes.Count - 1)
        {
            return;
        }

        SelectedStep.Axes.Move(idx, idx + 1);
        RefreshAxisMetadata(SelectedStep);
    }

    private bool CanBrowseFolder() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanBrowseFolder))]
    private async Task BrowseFolderAsync()
    {
        var selected = await _folderPickerService.PickFolderAsync(OutputFolder);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            OutputFolder = selected;
            SaveError = null;
        }
    }

    private bool CanOpenOutputFolder() =>
        !IsRunning &&
        !string.IsNullOrWhiteSpace(LastOutputPath) &&
        Directory.Exists(LastOutputPath);

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private async Task OpenOutputFolderAsync()
    {
        if (!string.IsNullOrWhiteSpace(LastOutputPath))
        {
            await _folderPickerService.OpenFolderAsync(LastOutputPath);
        }
    }

    private bool CanGenerateCli() => Steps.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGenerateCli))]
    private void GenerateCli()
    {
        if (Steps.Count == 0)
        {
            return;
        }

        var script = _chainCliGenerator.Generate(Steps, SelectedCliLanguage);
        var languageLabel = SelectedCliLanguage switch
        {
            ChainCliLanguage.Python => "Python",
            ChainCliLanguage.JavaScript => "JavaScript",
            ChainCliLanguage.Php => "PHP",
            _ => SelectedCliLanguage.ToString()
        };

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            $"Script CLI {languageLabel} — Chaîne ({Steps.Count} {(Steps.Count <= 1 ? "étape" : "étapes")})",
            script,
            IsReadOnly: true,
            OnSave: null));
    }

    private bool CanSavePipeline() => Steps.Count > 0 && !IsSavingPipeline;

    [RelayCommand(CanExecute = nameof(CanSavePipeline))]
    private async Task SavePipelineAsync()
    {
        if (Steps.Count == 0 || IsSavingPipeline)
        {
            return;
        }

        IsSavingPipeline = true;
        PipelineSaveError = null;
        PipelineSaveStatusText = string.Empty;

        try
        {
            var saveOptions = new BatchSaveOptions
            {
                SaveMode = SaveMode,
                OutputFolder = OutputFolder,
                GroupedFormat = GroupedFormat,
                CreateTimestampSubfolder = CreateTimestampSubfolder,
                IncludeSummaryCsv = IncludeSummaryCsv
            };

            var document = ChainPipelineSerializer.CreateDocument(
                Steps,
                KeepBodies,
                SelectedCliLanguage,
                _sharedRateLimitPolicy,
                saveOptions);

            var result = await _pipelineFileService.SavePipelineAsync(
                document,
                _lastPipelineDirectory,
                _lastPipelineFileName);

            if (result.IsCancelled)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                PipelineSaveError = result.ErrorMessage ?? "Impossible d'enregistrer le pipeline.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.FilePath))
            {
                LastSavedPipelinePath = result.FilePath;
                _lastPipelineDirectory = Path.GetDirectoryName(result.FilePath);
                _lastPipelineFileName = Path.GetFileName(result.FilePath);
            }
            else if (!string.IsNullOrWhiteSpace(result.FileName))
            {
                LastSavedPipelinePath = result.FileName;
                _lastPipelineFileName = result.FileName;
            }

            var displayName = !string.IsNullOrWhiteSpace(result.FileName)
                ? result.FileName
                : "pipeline.apit";
            PipelineSaveStatusText = $"Pipeline enregistré : {displayName}";
        }
        finally
        {
            IsSavingPipeline = false;
        }
    }

    private bool CanRun() =>
        !IsRunning &&
        Steps.Count > 0 &&
        TotalRequests > 0 &&
        !ExceedsHardLimit &&
        string.IsNullOrEmpty(PlanError) &&
        (!NeedsConfirmation || IsConfirmed);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRun))]
    private async Task RunAsync(CancellationToken ct)
    {
        CancelPendingLiveExtractionTest();
        RefreshAllStepsAndPlan();
        if (!CanRun())
        {
            return;
        }

        _rawResults.Clear();
        Results.Clear();
        _lastAccumulatedVariables.Clear();
        _lastAccumulatedLists.Clear();
        RefreshDeclaredVariables();
        foreach (var step in Steps)
        {
            step.LastReturnValue = string.Empty;
        }
        SelectedResult = null;
        SelectedResponse.Clear();
        SuccessCount = 0;
        FailedCount = 0;
        SkippedCount = 0;
        RetryCount = 0;
        FinalStatusText = string.Empty;
        SaveError = null;
        SaveSummaryText = string.Empty;
        LastOutputPath = string.Empty;
        OnPropertyChanged(nameof(ChainStatsSummaryText));

        var saveStarted = false;
        if (SaveMode != SaveMode.None)
        {
            var saveOptions = new BatchSaveOptions
            {
                SaveMode = SaveMode,
                OutputFolder = OutputFolder,
                GroupedFormat = GroupedFormat,
                CreateTimestampSubfolder = CreateTimestampSubfolder,
                IncludeSummaryCsv = IncludeSummaryCsv
            };

            try
            {
                await _batchResultWriter.BeginAsync(saveOptions, TotalRequests, ct);
                saveStarted = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SaveError = $"Impossible de préparer le dossier d'enregistrement : {ex.Message}";
                return;
            }
        }

        var stepByRank = Steps.ToList();
        var snapshots = stepByRank.Select(s => s.SnapshotForRun()).ToList();
        var runOptions = new ChainRunOptions
        {
            KeepBodies = KeepBodies,
            GlobalRateLimitPolicy = _sharedRateLimitPolicy.Clone()
        };

        ProgressCurrent = 0;
        ProgressTotal = TotalRequests;
        ProgressText = $"Étape 1/{snapshots.Count}";
        IsRunning = true;

        var progress = new Progress<ChainProgressReport>(report =>
        {
            ProgressCurrent = report.GlobalIndex;
            ProgressTotal = report.GlobalTotal;
            RetryCount = report.RetryCount;

            ProgressText = report.StepTotalIterations > 1
                ? $"Étape {report.StepIndex}/{report.TotalSteps}, requête {report.Iteration}/{report.StepTotalIterations}"
                : $"Étape {report.StepIndex}/{report.TotalSteps}";

            if (!report.IsRetryUpdate && report.Item is not null)
            {
                report.Item.SequenceOrder = _rawResults.Count;
                _rawResults.Add(report.Item);
                InsertResultSorted(report.Item);
                if (SelectedResult is null)
                {
                    SelectedResult = report.Item;
                }

                switch (report.Item.Status)
                {
                    case ChainStepStatus.Success:
                        SuccessCount++;
                        break;
                    case ChainStepStatus.Failed:
                        FailedCount++;
                        break;
                    case ChainStepStatus.Skipped:
                        SkippedCount++;
                        break;
                }

                if (report.Item.StepIndex >= 1 &&
                    report.Item.StepIndex <= stepByRank.Count)
                {
                    var liveStep = stepByRank[report.Item.StepIndex - 1];
                    if (report.Item.Response is not null && !report.Item.IsReturnOnly)
                    {
                        liveStep.LastResponse = report.Item.Response;
                        liveStep.LastResponseSummary = report.Item.StatusCode > 0
                            ? $"HTTP {report.Item.StatusCode} ({report.Item.DurationMs} ms)"
                            : "Erreur réseau";
                    }

                    if (!string.IsNullOrEmpty(report.Item.ReturnValue))
                    {
                        liveStep.LastReturnValue = report.Item.ReturnValue;
                    }
                }
            }

            if (report.AccumulatedVariables is not null)
            {
                _lastAccumulatedVariables = new Dictionary<string, string>(
                    report.AccumulatedVariables,
                    StringComparer.Ordinal);
            }

            if (report.AccumulatedLists is not null)
            {
                _lastAccumulatedLists = report.AccumulatedLists.ToDictionary(
                    k => k.Key,
                    v => (IReadOnlyList<string>)v.Value.ToList(),
                    StringComparer.Ordinal);
            }

            if (report.AccumulatedVariables is not null || report.AccumulatedLists is not null)
            {
                RefreshDeclaredVariables();
            }

            OnPropertyChanged(nameof(ChainStatsSummaryText));
        });

        Func<ChainStepResult, CancellationToken, Task>? onResultAsync = saveStarted
            ? (result, token) => _batchResultWriter.WriteAsync(result.ToBatchResultItem(), token)
            : null;

        try
        {
            var runResult = await _chainRunner.RunAsync(snapshots, runOptions, progress, onResultAsync, ct);
            _lastAccumulatedVariables = new Dictionary<string, string>(runResult.AllVariables, StringComparer.Ordinal);
            _lastAccumulatedLists = runResult.AllLists.ToDictionary(
                k => k.Key,
                v => (IReadOnlyList<string>)v.Value.ToList(),
                StringComparer.Ordinal);
            RefreshDeclaredVariables();
            FinalStatusText = runResult.FinalStatus switch
            {
                ChainRunStatus.Success => $"Chaîne réussie ({runResult.TotalDurationMs} ms)",
                ChainRunStatus.Failed => $"Chaîne terminée avec erreur ({runResult.TotalDurationMs} ms)",
                ChainRunStatus.Cancelled => "Chaîne annulée",
                _ => string.Empty
            };
        }
        catch (OperationCanceledException)
        {
            FinalStatusText = "Chaîne annulée";
        }
        finally
        {
            if (saveStarted)
            {
                await _batchResultWriter.CompleteAsync();
                LastOutputPath = _batchResultWriter.OutputPath;
                SaveSummaryText = $"{_batchResultWriter.SavedCount} enregistrées, {_batchResultWriter.ErrorCount} en échec";
            }

            IsRunning = false;
            OnPropertyChanged(nameof(ChainStatsSummaryText));
        }
    }

    private void InsertResultSorted(ChainStepResult item)
    {
        if (Results.Count == 0 || CompareResults(item, Results[^1], SelectedResultSortMode) >= 0)
        {
            Results.Add(item);
            return;
        }

        for (var i = 0; i < Results.Count; i++)
        {
            if (CompareResults(item, Results[i], SelectedResultSortMode) < 0)
            {
                Results.Insert(i, item);
                return;
            }
        }

        Results.Add(item);
    }

    private void RebuildSortedResults()
    {
        if (_rawResults.Count == 0)
        {
            Results.Clear();
            return;
        }

        var previousSelection = SelectedResult;
        var ordered = new List<ChainStepResult>(_rawResults);
        ordered.Sort((left, right) => CompareResults(left, right, SelectedResultSortMode));

        _isSortingResults = true;
        try
        {
            Results.Clear();
            foreach (var item in ordered)
            {
                Results.Add(item);
            }

            if (previousSelection is not null && Results.Contains(previousSelection))
            {
                SelectedResult = previousSelection;
            }
        }
        finally
        {
            _isSortingResults = false;
        }

        if (previousSelection is not null && !ReferenceEquals(SelectedResult, previousSelection))
        {
            SelectedResult = Results.FirstOrDefault();
        }
    }

    private static int CompareResults(ChainStepResult left, ChainStepResult right, ChainResultSortMode sortMode)
    {
        var comparison = sortMode switch
        {
            ChainResultSortMode.ChronologicalAsc => left.SequenceOrder.CompareTo(right.SequenceOrder),
            ChainResultSortMode.ChronologicalDesc => right.SequenceOrder.CompareTo(left.SequenceOrder),
            ChainResultSortMode.SizeDesc => right.SizeBytes.CompareTo(left.SizeBytes),
            ChainResultSortMode.SizeAsc => left.SizeBytes.CompareTo(right.SizeBytes),
            _ => left.SequenceOrder.CompareTo(right.SequenceOrder)
        };

        return comparison != 0
            ? comparison
            : left.SequenceOrder.CompareTo(right.SequenceOrder);
    }

    private void RefreshDeclaredVariables()
    {
        var previousStates = new Dictionary<string, bool>(DeclaredVariables.Count, StringComparer.Ordinal);
        foreach (var existing in DeclaredVariables)
        {
            previousStates[existing.Key] = existing.IsExpanded;
        }

        var rebuilt = ChainDeclaredVariableBuilder.Build(
            Steps,
            _lastAccumulatedVariables,
            _lastAccumulatedLists,
            previousStates);

        DeclaredVariables.Clear();
        foreach (var item in rebuilt)
        {
            DeclaredVariables.Add(item);
        }
    }

    private void AttachStepHandlers(ChainStep step)
    {
        step.PropertyChanged += OnStepPropertyChanged;
        step.Headers.CollectionChanged += OnStepKeyValueCollectionChanged;
        step.FormFields.CollectionChanged += OnStepKeyValueCollectionChanged;
        step.VaryingParams.CollectionChanged += OnStepVaryingParamsChanged;
        step.Extractions.CollectionChanged += OnStepExtractionsChanged;
        foreach (var ext in step.Extractions)
        {
            ext.PropertyChanged += OnNestedItemPropertyChanged;
        }
    }

    private void DetachStepHandlers(ChainStep step)
    {
        step.PropertyChanged -= OnStepPropertyChanged;
        step.Headers.CollectionChanged -= OnStepKeyValueCollectionChanged;
        step.FormFields.CollectionChanged -= OnStepKeyValueCollectionChanged;
        step.VaryingParams.CollectionChanged -= OnStepVaryingParamsChanged;
        step.Extractions.CollectionChanged -= OnStepExtractionsChanged;

        foreach (var ext in step.Extractions)
        {
            ext.PropertyChanged -= OnNestedItemPropertyChanged;
        }

        foreach (var variable in step.Variables)
        {
            variable.PropertyChanged -= OnStepVariablePropertyChanged;
        }
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isBulkUpdating || sender is not ChainStep step)
        {
            return;
        }

        if (e.PropertyName is nameof(ChainStep.Url)
            or nameof(ChainStep.Body)
            or nameof(ChainStep.BodyType)
            or nameof(ChainStep.ReturnExpression)
            or nameof(ChainStep.SelectedMode)
            or nameof(ChainStep.Name)
            or nameof(ChainStep.HasParsed))
        {
            if (e.PropertyName == nameof(ChainStep.Body) &&
                step.BodyType == BodyType.None &&
                !string.IsNullOrWhiteSpace(step.Body))
            {
                var trimmed = step.Body.TrimStart();
                var ctHeader = step.Headers.LastOrDefault(h =>
                    h.IsEnabled && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
                step.BodyType = (trimmed.StartsWith('{') || trimmed.StartsWith('[') ||
                                 (ctHeader is not null && ctHeader.Contains("json", StringComparison.OrdinalIgnoreCase)))
                    ? BodyType.Json
                    : BodyType.Text;
            }

            IsConfirmed = false;
            RefreshAllStepsAndPlan();
        }
    }

    private void OnStepKeyValueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (KeyValueItem item in e.OldItems)
            {
                item.PropertyChanged -= OnNestedItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (KeyValueItem item in e.NewItems)
            {
                item.PropertyChanged += OnNestedItemPropertyChanged;
            }
        }

        if (!_isBulkUpdating)
        {
            RefreshAllStepsAndPlan();
        }
    }

    private void OnStepVaryingParamsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (VaryingParam item in e.OldItems)
            {
                item.PropertyChanged -= OnNestedItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (VaryingParam item in e.NewItems)
            {
                item.PropertyChanged += OnNestedItemPropertyChanged;
            }
        }

        if (!_isBulkUpdating)
        {
            IsConfirmed = false;
            RefreshAllStepsAndPlan();
        }
    }

    private void OnStepExtractionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChainExtraction item in e.OldItems)
            {
                item.PropertyChanged -= OnNestedItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChainExtraction item in e.NewItems)
            {
                item.PropertyChanged += OnNestedItemPropertyChanged;
            }
        }

        if (!_isBulkUpdating)
        {
            RefreshAllStepsAndPlan();
        }
    }

    private void OnStepVariablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isBulkUpdating)
        {
            return;
        }

        if (e.PropertyName is nameof(BatchVariable.IsVarying)
            or nameof(BatchVariable.FixedValue)
            or nameof(BatchVariable.Mode)
            or nameof(BatchVariable.Base)
            or nameof(BatchVariable.StartText)
            or nameof(BatchVariable.EndText)
            or nameof(BatchVariable.StepText)
            or nameof(BatchVariable.Width)
            or nameof(BatchVariable.UppercaseHex)
            or nameof(BatchVariable.UsePrefix)
            or nameof(BatchVariable.ListText)
            or nameof(BatchVariable.RandomKind)
            or nameof(BatchVariable.RandomCount)
            or nameof(BatchVariable.IsUnique)
            or nameof(BatchVariable.SeedText)
            or nameof(BatchVariable.IsConfigured)
            or nameof(BatchVariable.ValueCount)
            or nameof(BatchVariable.ErrorText))
        {
            IsConfirmed = false;
            RefreshAllStepsAndPlan();
        }
    }

    private void OnNestedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isBulkUpdating)
        {
            return;
        }

        if (e.PropertyName is nameof(ChainExtraction.LastExtractedValue)
            or nameof(ChainExtraction.LastExtractedList)
            or nameof(ChainExtraction.LastRawExtractedList)
            or nameof(ChainExtraction.ErrorText))
        {
            return;
        }

        RefreshAllStepsAndPlan();

        if (sender is not ChainExtraction extraction)
        {
            return;
        }

        if (SelectedStep is null || (!extraction.IsVariableSource && SelectedStep.LastResponse is null))
        {
            if (e.PropertyName is nameof(ChainExtraction.Expression)
                or nameof(ChainExtraction.HeaderName)
                or nameof(ChainExtraction.SourceVariableName)
                or nameof(ChainExtraction.RegexGroup)
                or nameof(ChainExtraction.Source))
            {
                extraction.ClearPreviewState();
            }
            return;
        }

        if (e.PropertyName is nameof(ChainExtraction.IsList)
            or nameof(ChainExtraction.Kind)
            or nameof(ChainExtraction.Source))
        {
            if (extraction.IsVariableSource)
            {
                if (!string.IsNullOrWhiteSpace(extraction.SourceVariableName) &&
                    (extraction.Kind == ExtractionKind.FullResponse || !string.IsNullOrWhiteSpace(extraction.Expression)))
                {
                    CancelPendingLiveExtractionTest();
                    EvaluateExtraction(extraction, allowEmptyVariablePreview: true);
                }
            }
            else if (extraction.Kind == ExtractionKind.FullResponse ||
                !string.IsNullOrWhiteSpace(extraction.Expression))
            {
                CancelPendingLiveExtractionTest();
                EvaluateExtraction(extraction, allowEmptyVariablePreview: true);
            }
            return;
        }

        if (e.PropertyName is nameof(ChainExtraction.Expression)
            or nameof(ChainExtraction.HeaderName)
            or nameof(ChainExtraction.SourceVariableName)
            or nameof(ChainExtraction.RegexGroup)
            or nameof(ChainExtraction.VariableName))
        {
            if (e.PropertyName == nameof(ChainExtraction.VariableName) &&
                extraction.Kind != ExtractionKind.FullResponse &&
                string.IsNullOrWhiteSpace(extraction.Expression))
            {
                return;
            }

            ScheduleLiveExtractionTest(extraction);
        }
    }

    private void RefreshAllStepsAndPlan()
    {
        var extractedScalarsSoFar = new HashSet<string>(StringComparer.Ordinal);
        var extractedListsSoFar = new HashSet<string>(StringComparer.Ordinal);
        var count = Steps.Count;

        for (var i = 0; i < count; i++)
        {
            var step = Steps[i];
            step.Rank = i + 1;
            step.CanMoveUp = i > 0;
            step.CanMoveDown = i < count - 1;

            SyncStepVariablesAndAxes(step, extractedScalarsSoFar, extractedListsSoFar);

            foreach (var ext in step.Extractions)
            {
                if (string.IsNullOrWhiteSpace(ext.VariableName))
                {
                    continue;
                }

                var name = ext.VariableName.Trim();
                if (ext.IsList)
                {
                    extractedListsSoFar.Add(name);
                }
                else
                {
                    extractedScalarsSoFar.Add(name);
                }
            }

            if (step.HasReturnExpression)
            {
                var returnScalars = new HashSet<string>(extractedScalarsSoFar, StringComparer.Ordinal);
                foreach (var v in step.Variables)
                {
                    if (v.IsUsed && v.IsConfigured && !v.HasError && !string.IsNullOrWhiteSpace(v.Name))
                    {
                        returnScalars.Add(v.Name);
                    }
                }

                step.ReturnError = ChainRunner.ValidateStepReturn(
                    _stepReturnEvaluator,
                    step.ReturnExpression,
                    returnScalars,
                    extractedListsSoFar);
            }
            else
            {
                step.ReturnError = null;
            }
        }

        var plan = _chainRunner.BuildPlan(Steps);
        TotalRequests = plan.TotalRequests;
        HasDynamicRequestsCount = plan.HasDynamicCounts;
        ExceedsHardLimit = plan.ExceedsHardLimit;
        PlanError = plan.ValidationError;
        NeedsConfirmation = !plan.ExceedsHardLimit && plan.TotalRequests > BatchRunner.ConfirmationThreshold;

        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].TotalIterations = i < plan.StepIterationCounts.Count
                ? plan.StepIterationCounts[i]
                : 1;
            Steps[i].HasDynamicIterations = i < plan.StepHasDynamicCounts.Count && plan.StepHasDynamicCounts[i];
        }

        ChainWarnings.Clear();
        foreach (var w in CurlParser.GroupAndOrderWarnings(plan.Warnings))
        {
            ChainWarnings.Add(w);
        }

        RefreshDeclaredVariables();

        OnPropertyChanged(nameof(HasChainWarnings));
        OnPropertyChanged(nameof(AvailableExtractedVariablesText));
        OnPropertyChanged(nameof(HasAvailableExtractedVariables));

        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
        RemoveStepCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private void SyncStepVariablesAndAxes(
        ChainStep step,
        HashSet<string> availableScalars,
        HashSet<string> availableLists)
    {
        var template = step.BuildTemplateRequest();
        var extracted = _templateEngine.ExtractVariables(template);
        var extractedLists = _templateEngine.ExtractListVariables(template);
        var bareScalarVars = _templateEngine.GetBareScalarVariables(template);
        var extractedByName = extracted.ToDictionary(v => v.Name, StringComparer.Ordinal);

        string ResolveAutoUsedIn(ExtractedVariable info)
        {
            if (availableScalars.Contains(info.Name))
            {
                return $"{info.LocationsDisplay} · extraite";
            }

            if (availableLists.Contains(info.Name) && !bareScalarVars.Contains(info.Name))
            {
                return $"{info.LocationsDisplay} · liste (accès imbriqué)";
            }

            return info.LocationsDisplay;
        }

        var existingByName = new Dictionary<string, BatchVariable>(StringComparer.Ordinal);
        for (var i = step.Variables.Count - 1; i >= 0; i--)
        {
            var existing = step.Variables[i];
            if (extractedByName.TryGetValue(existing.Name, out var info))
            {
                existing.UsedIn = !existing.IsConfigured
                    ? ResolveAutoUsedIn(info)
                    : info.LocationsDisplay;
                existing.IsUsed = true;
                existingByName[existing.Name] = existing;
            }
            else if (existing.IsConfigured)
            {
                existing.IsUsed = false;
                existing.UsedIn = string.Empty;
                existingByName[existing.Name] = existing;
            }
            else
            {
                existing.PropertyChanged -= OnStepVariablePropertyChanged;
                step.Variables.RemoveAt(i);
            }
        }

        foreach (var item in extracted)
        {
            if (existingByName.ContainsKey(item.Name))
            {
                continue;
            }

            var created = new BatchVariable
            {
                Name = item.Name,
                UsedIn = ResolveAutoUsedIn(item),
                IsUsed = true
            };
            created.PropertyChanged += OnStepVariablePropertyChanged;
            step.Variables.Add(created);
        }

        step.UsedExtractedListsText = extractedLists.Count == 0
            ? string.Empty
            : $"Listes utilisées : {string.Join(", ", extractedLists.Select(l => $"[[{l.Name}]] ({l.LocationsDisplay})"))}";

        step.UsedVariablesCount = step.Variables.Count(v => v.IsUsed) + extractedLists.Count;

        var errors = new List<string>();
        foreach (var unknownDollar in _templateEngine.GetUnknownPredefinedMarkers(template))
        {
            errors.Add($"{{{{{unknownDollar}}}}} : variable prédéfinie inconnue ($index, $guid, $timestamp).");
        }

        foreach (var listExt in extractedLists)
        {
            if (VariablePathHelper.TrySplit(listExt.Name, out var listRoot, out var listJsonPath) &&
                listJsonPath is not null)
            {
                var hasRootInStep = step.Variables.Any(v => v.Name == listRoot && v.IsConfigured && !v.HasError);
                if (!availableScalars.Contains(listRoot) && !availableLists.Contains(listRoot) && !hasRootInStep)
                {
                    errors.Add($"[[{listExt.Name}]] : aucune étape précédente n'extrait la variable « {listRoot} ».");
                }
                continue;
            }

            if (!availableLists.Contains(listExt.Name))
            {
                errors.Add(availableScalars.Contains(listExt.Name)
                    ? $"[[{listExt.Name}]] : correspond à une variable simple ; coche « En liste [[...]] » ou utilise {{{{{listExt.Name}}}}}."
                    : $"[[{listExt.Name}]] : aucune étape précédente n'extrait cette liste.");
            }
        }

        foreach (var ext in extracted)
        {
            if (step.Variables.FirstOrDefault(v => v.Name == ext.Name) is not { } variable)
            {
                continue;
            }

            if (!variable.IsConfigured)
            {
                if (!availableScalars.Contains(ext.Name))
                {
                    if (availableLists.Contains(ext.Name))
                    {
                        if (bareScalarVars.Contains(ext.Name))
                        {
                            errors.Add($"{{{{{ext.Name}}}}} : extraite en liste ; utilise [[{ext.Name}]] pour itérer sur ses valeurs (ou {{{{{ext.Name}.champ}}}} pour un accès imbriqué).");
                        }
                    }
                    else
                    {
                        errors.Add($"{{{{{ext.Name}}}}} : saisis une valeur ou coche « Faire varier ».");
                    }
                }
            }
            else if (variable.HasError)
            {
                errors.Add($"{{{{{ext.Name}}}}} : {variable.ErrorText}");
            }
        }

        step.VariablesError = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);

        var newAxes = BatchExecutionHelper.BuildDefaultStepAxes(step.VaryingParams, step.Variables);
        foreach (var listExt in extractedLists)
        {
            newAxes.Add(BatchAxis.CreateDynamicList(listExt.Name));
        }

        // Conserver l'ordre existant lorsque les axes correspondent encore
        var ordered = new List<BatchAxis>(newAxes.Count);
        foreach (var oldAxis in step.Axes)
        {
            var match = newAxes.FirstOrDefault(a =>
                a.Kind == oldAxis.Kind && string.Equals(a.Name, oldAxis.Name, StringComparison.Ordinal));
            if (match is not null)
            {
                newAxes.Remove(match);
                ordered.Add(match);
            }
        }
        ordered.AddRange(newAxes);

        step.Axes.Clear();
        foreach (var axis in ordered)
        {
            step.Axes.Add(axis);
        }

        RefreshAxisMetadata(step);

        step.Warnings.Clear();
        foreach (var w in CurlParser.GroupAndOrderWarnings(step.ParseWarnings))
        {
            step.Warnings.Add(w);
        }

        step.NotifyCollectionsChanged();
    }

    private static void RefreshAxisMetadata(ChainStep step)
    {
        var count = step.Axes.Count;
        var ignored = step.IsOrderIgnored;

        for (var i = 0; i < count; i++)
        {
            var axis = step.Axes[i];
            axis.Rank = i + 1;
            axis.CanMoveUp = i > 0 && !ignored;
            axis.CanMoveDown = i < count - 1 && !ignored;
            axis.RoleText = count == 1 ? "Axe unique" : i == 0 ? "Boucle externe" : i == count - 1 ? "Boucle interne" : string.Empty;
            axis.IsOuterRole = count > 1 && i == 0;
            axis.IsInnerRole = count > 1 && i == count - 1;
        }
    }

    private List<string> GetAvailableExtractedTokensBefore(ChainStep targetStep)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in Steps)
        {
            if (ReferenceEquals(step, targetStep))
            {
                break;
            }

            foreach (var ext in step.Extractions)
            {
                if (!string.IsNullOrWhiteSpace(ext.VariableName))
                {
                    var name = ext.VariableName.Trim();
                    var token = ext.IsList
                        ? $"[[{name}]]"
                        : ext.Kind == ExtractionKind.FullResponse
                            ? $"{{{{{name}}}}} (.jsonpath)"
                            : $"{{{{{name}}}}}";
                    if (seen.Add(token))
                    {
                        list.Add(token);
                    }
                }
            }
        }

        return list;
    }

    private static string GetDefaultOutputFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "ApiTester", "chaines");
    }
}
