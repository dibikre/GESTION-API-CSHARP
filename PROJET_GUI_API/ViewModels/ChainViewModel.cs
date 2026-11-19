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

namespace ApiTester.ViewModels;

public partial class ChainViewModel : ViewModelBase
{
    private readonly ICurlParser _curlParser;
    private readonly ITemplateEngine _templateEngine;
    private readonly IChainRunner _chainRunner;
    private readonly IResponseExtractor _responseExtractor;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IBatchResultWriter _batchResultWriter;
    private readonly RateLimitPolicy _sharedRateLimitPolicy;
    private bool _isBulkUpdating;

    public ChainViewModel(
        ICurlParser curlParser,
        ITemplateEngine templateEngine,
        IChainRunner chainRunner,
        IResponseExtractor responseExtractor,
        IFolderPickerService folderPickerService,
        IBatchResultWriter batchResultWriter,
        BatchViewModel? batchViewModel = null)
    {
        _curlParser = curlParser ?? throw new ArgumentNullException(nameof(curlParser));
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        _chainRunner = chainRunner ?? throw new ArgumentNullException(nameof(chainRunner));
        _responseExtractor = responseExtractor ?? throw new ArgumentNullException(nameof(responseExtractor));
        _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        _batchResultWriter = batchResultWriter ?? throw new ArgumentNullException(nameof(batchResultWriter));
        _sharedRateLimitPolicy = batchViewModel?.RateLimitPolicy ?? new RateLimitPolicy();

        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasChainStats));
        };
        AccumulatedVariables.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAccumulatedVariables));
        Steps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSteps));
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

    public ChainDefinition Definition { get; } = new();

    public ObservableCollection<ChainStep> Steps => Definition.Steps;

    public bool HasSteps => Steps.Count > 0;

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

            var names = GetAvailableExtractedVariableNamesBefore(SelectedStep);
            return names.Count == 0
                ? string.Empty
                : $"Variables extraites des étapes précédentes : {string.Join(", ", names.Select(n => $"{{{{{n}}}}}"))}";
        }
    }

    public bool HasAvailableExtractedVariables => !string.IsNullOrWhiteSpace(AvailableExtractedVariablesText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalRequestsText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial int TotalRequests { get; private set; }

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

    public string TotalRequestsText => TotalRequests <= 1
        ? $"{TotalRequests} requête prévue"
        : $"{TotalRequests} requêtes prévues";

    [ObservableProperty]
    public partial bool KeepBodies { get; set; } = true;

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

    public ObservableCollection<ChainAccumulatedVariableItem> AccumulatedVariables { get; } = [];

    public bool HasAccumulatedVariables => AccumulatedVariables.Count > 0;

    public ObservableCollection<ChainStepResult> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    public partial ChainStepResult? SelectedResult { get; set; }

    public ResponseViewModel SelectedResponse { get; } = new();

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

    partial void OnSelectedStepChanged(ChainStep? value)
    {
        OnPropertyChanged(nameof(AvailableExtractedVariablesText));
        OnPropertyChanged(nameof(HasAvailableExtractedVariables));
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedResultChanged(ChainStepResult? value)
    {
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
        if (SelectedStep is null || extraction is null)
        {
            return;
        }

        var response = SelectedStep.LastResponse;
        if (response is null)
        {
            extraction.LastExtractedValue = null;
            extraction.ErrorText = "Exécute d'abord cette étape pour disposer d'une réponse à tester.";
            return;
        }

        var outcome = _responseExtractor.Extract(response, extraction);
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
        RefreshAllStepsAndPlan();
        if (!CanRun())
        {
            return;
        }

        Results.Clear();
        AccumulatedVariables.Clear();
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
                Results.Add(report.Item);

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

                if (report.Item.Response is not null &&
                    report.Item.StepIndex >= 1 &&
                    report.Item.StepIndex <= stepByRank.Count)
                {
                    var liveStep = stepByRank[report.Item.StepIndex - 1];
                    liveStep.LastResponse = report.Item.Response;
                    liveStep.LastResponseSummary = report.Item.StatusCode > 0
                        ? $"HTTP {report.Item.StatusCode} ({report.Item.DurationMs} ms)"
                        : "Erreur réseau";
                }
            }

            if (report.AccumulatedVariables is not null)
            {
                UpdateAccumulatedVariablesDisplay(report.AccumulatedVariables);
            }

            OnPropertyChanged(nameof(ChainStatsSummaryText));
        });

        Func<ChainStepResult, CancellationToken, Task>? onResultAsync = saveStarted
            ? (result, token) => _batchResultWriter.WriteAsync(result.ToBatchResultItem(), token)
            : null;

        try
        {
            var runResult = await _chainRunner.RunAsync(snapshots, runOptions, progress, onResultAsync, ct);
            UpdateAccumulatedVariablesDisplay(runResult.AllVariables);
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

    private void UpdateAccumulatedVariablesDisplay(IReadOnlyDictionary<string, string> variables)
    {
        AccumulatedVariables.Clear();
        foreach (var kvp in variables.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            AccumulatedVariables.Add(new ChainAccumulatedVariableItem
            {
                Name = kvp.Key,
                RawValue = kvp.Value
            });
        }
    }

    private void AttachStepHandlers(ChainStep step)
    {
        step.PropertyChanged += OnStepPropertyChanged;
        step.Headers.CollectionChanged += OnStepKeyValueCollectionChanged;
        step.FormFields.CollectionChanged += OnStepKeyValueCollectionChanged;
        step.VaryingParams.CollectionChanged += OnStepVaryingParamsChanged;
        step.Extractions.CollectionChanged += OnStepExtractionsChanged;
    }

    private void DetachStepHandlers(ChainStep step)
    {
        step.PropertyChanged -= OnStepPropertyChanged;
        step.Headers.CollectionChanged -= OnStepKeyValueCollectionChanged;
        step.FormFields.CollectionChanged -= OnStepKeyValueCollectionChanged;
        step.VaryingParams.CollectionChanged -= OnStepVaryingParamsChanged;
        step.Extractions.CollectionChanged -= OnStepExtractionsChanged;

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
        if (!_isBulkUpdating)
        {
            RefreshAllStepsAndPlan();
        }
    }

    private void RefreshAllStepsAndPlan()
    {
        var extractedSoFar = new HashSet<string>(StringComparer.Ordinal);
        var count = Steps.Count;

        for (var i = 0; i < count; i++)
        {
            var step = Steps[i];
            step.Rank = i + 1;
            step.CanMoveUp = i > 0;
            step.CanMoveDown = i < count - 1;

            SyncStepVariablesAndAxes(step, extractedSoFar);

            foreach (var ext in step.Extractions)
            {
                if (!string.IsNullOrWhiteSpace(ext.VariableName))
                {
                    extractedSoFar.Add(ext.VariableName.Trim());
                }
            }
        }

        var plan = _chainRunner.BuildPlan(Steps);
        TotalRequests = plan.TotalRequests;
        ExceedsHardLimit = plan.ExceedsHardLimit;
        PlanError = plan.ValidationError;
        NeedsConfirmation = !plan.ExceedsHardLimit && plan.TotalRequests > BatchRunner.ConfirmationThreshold;

        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].TotalIterations = i < plan.StepIterationCounts.Count
                ? plan.StepIterationCounts[i]
                : 1;
        }

        ChainWarnings.Clear();
        foreach (var w in CurlParser.GroupAndOrderWarnings(plan.Warnings))
        {
            ChainWarnings.Add(w);
        }

        OnPropertyChanged(nameof(HasChainWarnings));
        OnPropertyChanged(nameof(AvailableExtractedVariablesText));
        OnPropertyChanged(nameof(HasAvailableExtractedVariables));

        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
        RemoveStepCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private void SyncStepVariablesAndAxes(ChainStep step, HashSet<string> availableFromPrevious)
    {
        var template = step.BuildTemplateRequest();
        var extracted = _templateEngine.ExtractVariables(template);
        var extractedByName = extracted.ToDictionary(v => v.Name, StringComparer.Ordinal);

        var existingByName = new Dictionary<string, BatchVariable>(StringComparer.Ordinal);
        for (var i = step.Variables.Count - 1; i >= 0; i--)
        {
            var existing = step.Variables[i];
            if (extractedByName.TryGetValue(existing.Name, out var info))
            {
                existing.UsedIn = availableFromPrevious.Contains(existing.Name) && !existing.IsConfigured
                    ? $"{info.LocationsDisplay} · extraite"
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
                UsedIn = availableFromPrevious.Contains(item.Name)
                    ? $"{item.LocationsDisplay} · extraite"
                    : item.LocationsDisplay,
                IsUsed = true
            };
            created.PropertyChanged += OnStepVariablePropertyChanged;
            step.Variables.Add(created);
        }

        step.UsedVariablesCount = step.Variables.Count(v => v.IsUsed);

        var errors = new List<string>();
        foreach (var unknownDollar in _templateEngine.GetUnknownPredefinedMarkers(template))
        {
            errors.Add($"{{{{{unknownDollar}}}}} : variable prédéfinie inconnue ($index, $guid, $timestamp).");
        }

        foreach (var ext in extracted)
        {
            if (step.Variables.FirstOrDefault(v => v.Name == ext.Name) is not { } variable)
            {
                continue;
            }

            if (!variable.IsConfigured)
            {
                if (!availableFromPrevious.Contains(ext.Name))
                {
                    errors.Add($"{{{{{ext.Name}}}}} : saisis une valeur ou coche « Faire varier ».");
                }
            }
            else if (variable.HasError)
            {
                errors.Add($"{{{{{ext.Name}}}}} : {variable.ErrorText}");
            }
        }

        step.VariablesError = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);

        var newAxes = BatchExecutionHelper.BuildDefaultStepAxes(step.VaryingParams, step.Variables);
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

    private List<string> GetAvailableExtractedVariableNamesBefore(ChainStep targetStep)
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
                    if (seen.Add(name))
                    {
                        list.Add(name);
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
