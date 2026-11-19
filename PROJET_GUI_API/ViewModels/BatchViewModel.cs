using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class BatchViewModel : ViewModelBase
{
    private const int VariableSyncDebounceMs = 250;
    private const int OrderPreviewLimit = 12;

    private readonly record struct AxisIdentity(BatchAxisKind Kind, string Name, int Occurrence);

    private readonly ICurlParser _curlParser;
    private readonly ITemplateEngine _templateEngine;
    private readonly IBatchRunner _batchRunner;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IBatchResultWriter _batchResultWriter;
    private readonly List<string> _parseWarnings = [];
    private readonly List<AxisIdentity> _rememberedAxisOrder = [];
    private AuthConfig _parsedAuth = new();
    private int _parsedTimeoutSeconds = 30;
    private bool _isBulkUpdating;
    private CancellationTokenSource? _variableSyncCts;
    private int _lastRateLimitedFailureRetries;

    public BatchViewModel(
        ICurlParser curlParser,
        ITemplateEngine templateEngine,
        IBatchRunner batchRunner,
        IFolderPickerService folderPickerService,
        IBatchResultWriter batchResultWriter)
    {
        _curlParser = curlParser ?? throw new ArgumentNullException(nameof(curlParser));
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        _batchRunner = batchRunner ?? throw new ArgumentNullException(nameof(batchRunner));
        _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        _batchResultWriter = batchResultWriter ?? throw new ArgumentNullException(nameof(batchResultWriter));

        VaryingParams.CollectionChanged += OnVaryingParamsCollectionChanged;
        Headers.CollectionChanged += OnEditableItemsCollectionChanged;
        FormFields.CollectionChanged += OnEditableItemsCollectionChanged;
        Warnings.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(WarningsSummaryText));
        };
        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasBatchStats));
        };
        PreviewItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPreviewItems));
        Axes.CollectionChanged += (_, _) => NotifyOrderPanelStateChanged();
        OrderPreview.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOrderPreview));
        RateLimitPolicy.PropertyChanged += (_, _) => ValidateAndSyncRateLimitPolicy();
    }

    public static IReadOnlyList<BatchMode> BatchModes { get; } = Enum.GetValues<BatchMode>();
    public static IReadOnlyList<SaveMode> SaveModes { get; } = Enum.GetValues<SaveMode>();
    public static IReadOnlyList<GroupedFormat> GroupedFormats { get; } = Enum.GetValues<GroupedFormat>();
    public static IReadOnlyList<VariableMode> VariableModes { get; } = Enum.GetValues<VariableMode>();
    public static IReadOnlyList<NumberBase> NumberBases { get; } = Enum.GetValues<NumberBase>();
    public static IReadOnlyList<RandomKind> RandomKinds { get; } = Enum.GetValues<RandomKind>();
    public static IReadOnlyList<BodyType> BodyTypes { get; } = RequestViewModel.BodyTypes;

    public Action<RequestModel>? OpenInSingleRequestCallback { get; set; }

    [ObservableProperty]
    public partial string CurlText { get; set; } = string.Empty;

    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningsSummaryText => Warnings.Count <= 1
        ? $"{Warnings.Count} avertissement"
        : $"{Warnings.Count} avertissements";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInSingleRequestCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool HasParsed { get; set; }

    [ObservableProperty]
    public partial HttpMethodType Method { get; set; } = HttpMethodType.Get;

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    public ObservableCollection<KeyValueItem> Headers { get; } = [];

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormBody))]
    public partial BodyType BodyType { get; set; } = BodyType.None;

    public bool IsFormBody => BodyType == BodyType.FormUrlEncoded;

    public ObservableCollection<KeyValueItem> FormFields { get; } = [];

    public ObservableCollection<VaryingParam> VaryingParams { get; } = [];

    public ObservableCollection<BatchVariable> Variables { get; } = [];

    public ObservableCollection<BatchAxis> Axes { get; } = [];

    public bool HasMultipleAxes => Axes.Count >= 2;

    public bool ShowOrderPanel => Axes.Count >= 2 && SelectedMode == BatchMode.Combinations;

    public bool IsOrderIgnored => Axes.Count >= 2 && SelectedMode == BatchMode.Parallel;

    [ObservableProperty]
    public partial string OrderSummaryText { get; private set; } = string.Empty;

    public ObservableCollection<BatchOrderPreviewRow> OrderPreview { get; } = [];

    public bool HasOrderPreview => OrderPreview.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrderPreviewOverflow))]
    public partial string OrderPreviewOverflowText { get; private set; } = string.Empty;

    public bool HasOrderPreviewOverflow => !string.IsNullOrWhiteSpace(OrderPreviewOverflowText);

    [ObservableProperty]
    public partial int UsedVariablesCount { get; private set; }

    [ObservableProperty]
    public partial bool HasVariables { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVariablesError))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? VariablesError { get; private set; }

    public bool HasVariablesError => !string.IsNullOrWhiteSpace(VariablesError);

    public ObservableCollection<BatchPreviewItem> PreviewItems { get; } = [];

    public bool HasPreviewItems => PreviewItems.Count > 0;

    [ObservableProperty]
    public partial bool IsPreviewOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenInSingleRequestWarning))]
    public partial string? OpenInSingleRequestWarning { get; private set; }

    public bool HasOpenInSingleRequestWarning => !string.IsNullOrWhiteSpace(OpenInSingleRequestWarning);

    [ObservableProperty]
    public partial BatchMode SelectedMode { get; set; } = BatchMode.Combinations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLowPauseWarning))]
    [NotifyPropertyChangedFor(nameof(NeedsLowPauseConfirmation))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial int DelayMs { get; set; } = RateLimitPolicy.DefaultPauseFloorMs;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsLowPauseConfirmed { get; set; }

    public bool ShowLowPauseWarning => DelayMs < RateLimitPolicy.LowPauseWarningThresholdMs;

    public bool NeedsLowPauseConfirmation => DelayMs < RateLimitPolicy.LowPauseWarningThresholdMs;

    public RateLimitPolicy RateLimitPolicy { get; } = new();

    [ObservableProperty]
    public partial string RateLimitMaxRetryAfterSecondsText { get; set; } = "120";

    [ObservableProperty]
    public partial string RateLimitBackoffInitialMsText { get; set; } = "500";

    [ObservableProperty]
    public partial string RateLimitBackoffMultiplierText { get; set; } = "2";

    [ObservableProperty]
    public partial string RateLimitBackoffMaxMsText { get; set; } = "30000";

    [ObservableProperty]
    public partial string RateLimitMaxRetriesText { get; set; } = "3";

    [ObservableProperty]
    public partial string RateLimitExtraStatusCodesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RateLimitQuotaThresholdText { get; set; } = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRateLimitError))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? RateLimitValidationError { get; private set; }

    public bool HasRateLimitError => !string.IsNullOrWhiteSpace(RateLimitValidationError);

    [ObservableProperty]
    public partial bool StopOnFailure { get; set; }

    [ObservableProperty]
    public partial bool KeepBodies { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalRequestsText))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial int TotalRequests { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool NeedsConfirmation { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsConfirmed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool ExceedsHardLimit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanError))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? PlanError { get; set; }

    public bool HasPlanError => !string.IsNullOrWhiteSpace(PlanError);

    public string TotalRequestsText => TotalRequests <= 1
        ? $"{TotalRequests} requête sera envoyée"
        : $"{TotalRequests} requêtes seront envoyées";

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
    public partial int SavedCount { get; set; }

    [ObservableProperty]
    public partial int SaveErrorCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveSummary))]
    public partial string SaveSummaryText { get; set; } = string.Empty;

    public bool HasSaveSummary => !string.IsNullOrWhiteSpace(SaveSummaryText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string? SaveError { get; set; }

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveOutputPath))]
    public partial string ActiveOutputPathText { get; set; } = string.Empty;

    public bool HasActiveOutputPath => !string.IsNullOrWhiteSpace(ActiveOutputPathText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBatchStats))]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int ProgressCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int ProgressTotal { get; set; }

    [ObservableProperty]
    public partial int RetryCount { get; private set; }

    [ObservableProperty]
    public partial int SuccessCount { get; private set; }

    [ObservableProperty]
    public partial int RateLimitedCount { get; private set; }

    [ObservableProperty]
    public partial int RateLimitedFailedCount { get; private set; }

    [ObservableProperty]
    public partial int OtherErrorCount { get; private set; }

    public bool HasBatchStats => IsRunning || Results.Count > 0;

    public bool HasRateLimitOccurrences => RateLimitedCount > 0 || (IsRunning && RetryCount > 0);

    public string SuccessSummaryPart =>
        $"{SuccessCount} {(SuccessCount <= 1 ? "réussite" : "réussites")}, ";

    public string RateLimitSummaryPart
    {
        get
        {
            if (RateLimitedCount == 0 && IsRunning && RetryCount > 0)
            {
                var retryLabel = RetryCount <= 1 ? "nouvelle tentative en cours" : "nouvelles tentatives en cours";
                return $"{RetryCount} {retryLabel}";
            }

            var baseText = $"{RateLimitedCount} {(RateLimitedCount <= 1 ? "limitation de débit" : "limitations de débit")}";
            if (RateLimitedFailedCount == 1)
            {
                var retryDetail = _lastRateLimitedFailureRetries == 0
                    ? "sans nouvelle tentative"
                    : $"après {_lastRateLimitedFailureRetries} {(_lastRateLimitedFailureRetries == 1 ? "tentative" : "tentatives")}";
                return $"{baseText} (dont 1 échec {retryDetail})";
            }

            if (RateLimitedFailedCount > 1)
            {
                return $"{baseText} (dont {RateLimitedFailedCount} échecs)";
            }

            return baseText;
        }
    }

    public string OtherErrorSummaryPart =>
        $", {OtherErrorCount} {(OtherErrorCount <= 1 ? "autre erreur" : "autres erreurs")}";

    public string RateLimitSummaryText => $"{SuccessSummaryPart}{RateLimitSummaryPart}{OtherErrorSummaryPart}";

    public string ProgressText => $"{ProgressCurrent} / {ProgressTotal}";

    public ObservableCollection<BatchResultItem> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    public partial BatchResultItem? SelectedResult { get; set; }

    public ResponseViewModel SelectedResponse { get; } = new();

    partial void OnUrlChanged(string value)
    {
        if (!_isBulkUpdating)
        {
            ScheduleVariableSync();
        }
    }

    partial void OnBodyChanged(string value)
    {
        if (_isBulkUpdating)
        {
            return;
        }

        if (BodyType == BodyType.None && !string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.TrimStart();
            var contentType = Headers.LastOrDefault(h =>
                h.IsEnabled && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;

            BodyType = (trimmed.StartsWith('{') || trimmed.StartsWith('[') ||
                        (contentType is not null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase)))
                ? BodyType.Json
                : BodyType.Text;
        }

        ScheduleVariableSync();
    }

    partial void OnBodyTypeChanged(BodyType value)
    {
        if (!_isBulkUpdating)
        {
            ScheduleVariableSync();
        }
    }

    partial void OnDelayMsChanged(int value)
    {
        IsLowPauseConfirmed = false;
        RateLimitPolicy.PauseFloorMs = Math.Max(0, value);
    }

    partial void OnRateLimitMaxRetryAfterSecondsTextChanged(string value) => ValidateAndSyncRateLimitPolicy();

    partial void OnRateLimitBackoffInitialMsTextChanged(string value) => ValidateAndSyncRateLimitPolicy();

    partial void OnRateLimitBackoffMultiplierTextChanged(string value) => ValidateAndSyncRateLimitPolicy();

    partial void OnRateLimitBackoffMaxMsTextChanged(string value) => ValidateAndSyncRateLimitPolicy();

    partial void OnRateLimitMaxRetriesTextChanged(string value) => ValidateAndSyncRateLimitPolicy();

    partial void OnRateLimitExtraStatusCodesTextChanged(string value) => ValidateAndSyncRateLimitPolicy();

    partial void OnRateLimitQuotaThresholdTextChanged(string value) => ValidateAndSyncRateLimitPolicy();

    partial void OnSelectedModeChanged(BatchMode value)
    {
        IsConfirmed = false;
        RecalculatePlan();
        RefreshAxesMetadataAndOrderPreview();
        NotifyOrderPanelStateChanged();
    }

    partial void OnSelectedResultChanged(BatchResultItem? value)
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
            IsSuccess = value.StatusCode is >= 200 and < 300,
            DurationMs = value.DurationMs,
            SizeBytes = 0,
            ContentType = "text/plain",
            Body = "(Corps non conservé — cochez « Conserver les corps » avant le lancement pour inspecter le détail.)"
        });
    }

    [RelayCommand]
    private void Parse()
    {
        var parseResult = _curlParser.Parse(CurlText);

        _parseWarnings.Clear();
        _parseWarnings.AddRange(parseResult.Warnings);

        _isBulkUpdating = true;
        try
        {
            var req = parseResult.Request;
            Method = req.Method;
            Url = req.Url;
            Body = req.Body;
            BodyType = req.BodyType;
            _parsedAuth = req.Auth;
            _parsedTimeoutSeconds = req.TimeoutSeconds > 0 ? req.TimeoutSeconds : 30;

            Headers.Clear();
            foreach (var header in req.Headers)
            {
                Headers.Add(new KeyValueItem(header.Key, header.Value, header.IsEnabled));
            }

            FormFields.Clear();
            foreach (var field in req.FormFields)
            {
                FormFields.Add(new KeyValueItem(field.Key, field.Value, field.IsEnabled));
            }

            VaryingParams.Clear();
            foreach (var queryParam in req.QueryParams)
            {
                VaryingParams.Add(new VaryingParam(queryParam.Key, queryParam.Value, queryParam.IsEnabled));
            }

            _rememberedAxisOrder.Clear();
            Axes.Clear();
            OrderPreview.Clear();
            OrderSummaryText = string.Empty;
            OrderPreviewOverflowText = string.Empty;

            HasParsed = !string.IsNullOrWhiteSpace(Url);
            IsConfirmed = false;
            OpenInSingleRequestWarning = null;
            PreviewItems.Clear();
            IsPreviewOpen = false;
        }
        finally
        {
            _isBulkUpdating = false;
        }

        SyncVariablesWithTemplate();
    }

    [RelayCommand]
    private void Clear()
    {
        CancelPendingVariableSync();
        _isBulkUpdating = true;
        try
        {
            CurlText = string.Empty;
            _parseWarnings.Clear();
            Warnings.Clear();
            HasParsed = false;
            Method = HttpMethodType.Get;
            Url = string.Empty;
            Headers.Clear();
            FormFields.Clear();
            Body = string.Empty;
            BodyType = BodyType.None;
            _parsedAuth = new AuthConfig();
            _parsedTimeoutSeconds = 30;
            VaryingParams.Clear();

            foreach (var variable in Variables)
            {
                variable.PropertyChanged -= OnBatchVariablePropertyChanged;
            }
            Variables.Clear();
            _rememberedAxisOrder.Clear();
            Axes.Clear();
            OrderSummaryText = string.Empty;
            OrderPreview.Clear();
            OrderPreviewOverflowText = string.Empty;

            UsedVariablesCount = 0;
            HasVariables = false;
            VariablesError = null;
            PreviewItems.Clear();
            IsPreviewOpen = false;
            OpenInSingleRequestWarning = null;

            TotalRequests = 0;
            NeedsConfirmation = false;
            IsConfirmed = false;
            ExceedsHardLimit = false;
            PlanError = null;
            ProgressCurrent = 0;
            ProgressTotal = 0;
            ResetRateLimitStats();
            LastOutputPath = string.Empty;
            SavedCount = 0;
            SaveErrorCount = 0;
            SaveSummaryText = string.Empty;
            SaveError = null;
            ActiveOutputPathText = string.Empty;
            Results.Clear();
            SelectedResult = null;
            SelectedResponse.Clear();
        }
        finally
        {
            _isBulkUpdating = false;
        }
    }

    private bool CanOpenInSingleRequest() => HasParsed;

    [RelayCommand(CanExecute = nameof(CanOpenInSingleRequest))]
    private void OpenInSingleRequest()
    {
        var requestModel = BuildTemplateRequest();
        OpenInSingleRequestWarning = _templateEngine.ContainsAnyMarker(requestModel)
            ? "Cette requête contient des {{variables}} qui ne seront pas remplacées dans l'onglet Requête."
            : null;
        OpenInSingleRequestCallback?.Invoke(requestModel);
    }

    [RelayCommand]
    private void AddHeader()
    {
        Headers.Add(new KeyValueItem());
    }

    [RelayCommand]
    private void RemoveHeader(KeyValueItem? item)
    {
        if (item is not null)
        {
            Headers.Remove(item);
        }
    }

    [RelayCommand]
    private void AddFormField()
    {
        FormFields.Add(new KeyValueItem());
    }

    [RelayCommand]
    private void RemoveFormField(KeyValueItem? item)
    {
        if (item is not null)
        {
            FormFields.Remove(item);
        }
    }

    [RelayCommand]
    private void AddVaryingParam()
    {
        VaryingParams.Add(new VaryingParam());
    }

    [RelayCommand]
    private void RemoveVaryingParam(VaryingParam? item)
    {
        if (item is not null)
        {
            VaryingParams.Remove(item);
        }
    }

    private bool CanMoveAxisUp(BatchAxis? axis) =>
        axis is not null && !IsOrderIgnored && axis.CanMoveUp;

    [RelayCommand(CanExecute = nameof(CanMoveAxisUp))]
    private void MoveAxisUp(BatchAxis? axis)
    {
        if (axis is null || IsOrderIgnored)
        {
            return;
        }

        var index = Axes.IndexOf(axis);
        if (index <= 0)
        {
            return;
        }

        SwapAxesAt(index, index - 1);
    }

    private bool CanMoveAxisDown(BatchAxis? axis) =>
        axis is not null && !IsOrderIgnored && axis.CanMoveDown;

    [RelayCommand(CanExecute = nameof(CanMoveAxisDown))]
    private void MoveAxisDown(BatchAxis? axis)
    {
        if (axis is null || IsOrderIgnored)
        {
            return;
        }

        var index = Axes.IndexOf(axis);
        if (index < 0 || index >= Axes.Count - 1)
        {
            return;
        }

        SwapAxesAt(index, index + 1);
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
        if (string.IsNullOrWhiteSpace(LastOutputPath))
        {
            return;
        }

        await _folderPickerService.OpenFolderAsync(LastOutputPath);
    }

    private bool CanPreview() =>
        HasParsed &&
        !IsRunning &&
        !HasVariablesError &&
        string.IsNullOrEmpty(PlanError) &&
        TotalRequests > 0;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private void Preview()
    {
        SyncVariablesWithTemplate();
        if (!CanPreview())
        {
            return;
        }

        var preview = _batchRunner.GeneratePreview(
            BuildTemplateRequest(),
            VaryingParams.ToList(),
            Variables.ToList(),
            Axes.ToList(),
            SelectedMode,
            maxCount: 3);

        PreviewItems.Clear();
        foreach (var item in preview)
        {
            PreviewItems.Add(item);
        }

        IsPreviewOpen = PreviewItems.Count > 0;
    }

    private bool CanRun() =>
        HasParsed &&
        !IsRunning &&
        TotalRequests > 0 &&
        !ExceedsHardLimit &&
        !HasVariablesError &&
        !HasRateLimitError &&
        string.IsNullOrEmpty(PlanError) &&
        (!NeedsConfirmation || IsConfirmed) &&
        (!NeedsLowPauseConfirmation || IsLowPauseConfirmed);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRun))]
    private async Task RunAsync(CancellationToken ct)
    {
        SyncVariablesWithTemplate();
        ValidateAndSyncRateLimitPolicy();
        if (!CanRun())
        {
            return;
        }

        Results.Clear();
        SelectedResult = null;
        SelectedResponse.Clear();
        ResetRateLimitStats();
        SaveError = null;
        SaveSummaryText = string.Empty;
        SavedCount = 0;
        SaveErrorCount = 0;
        LastOutputPath = string.Empty;
        ActiveOutputPathText = string.Empty;

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
                ActiveOutputPathText = $"Enregistrement dans : {_batchResultWriter.OutputPath}";
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

        var templateRequest = BuildTemplateRequest();
        var paramMap = new Dictionary<VaryingParam, VaryingParam>(VaryingParams.Count);
        var paramsSnapshot = new List<VaryingParam>(VaryingParams.Count);
        foreach (var p in VaryingParams)
        {
            var cloned = new VaryingParam
            {
                Name = p.Name,
                Value = p.Value,
                IsEnabled = p.IsEnabled,
                IsVarying = p.IsVarying,
                Start = p.Start,
                End = p.End,
                Step = p.Step
            };
            paramMap[p] = cloned;
            paramsSnapshot.Add(cloned);
        }

        var variableMap = new Dictionary<BatchVariable, BatchVariable>(Variables.Count);
        var variablesSnapshot = new List<BatchVariable>(Variables.Count);
        foreach (var v in Variables)
        {
            var cloned = v.Clone();
            variableMap[v] = cloned;
            variablesSnapshot.Add(cloned);
        }

        var axesSnapshot = new List<BatchAxis>(Axes.Count);
        foreach (var axis in Axes)
        {
            VaryingParam? snapParam = axis.SourceParam is not null && paramMap.TryGetValue(axis.SourceParam, out var sp)
                ? sp
                : null;
            BatchVariable? snapVar = axis.SourceVariable is not null && variableMap.TryGetValue(axis.SourceVariable, out var sv)
                ? sv
                : null;
            axesSnapshot.Add(axis.CloneWithSources(snapParam, snapVar));
        }

        var options = new BatchRunOptions
        {
            Mode = SelectedMode,
            DelayMs = Math.Max(0, DelayMs),
            StopOnFailure = StopOnFailure,
            KeepBodies = KeepBodies,
            RateLimitPolicy = RateLimitPolicy.Clone()
        };

        ProgressCurrent = 0;
        ProgressTotal = TotalRequests;
        IsRunning = true;
        NotifyRateLimitStatsChanged();

        var progress = new Progress<BatchProgressReport>(report =>
        {
            ProgressCurrent = report.Index;
            ProgressTotal = report.Total;
            RetryCount = report.RetryCount;

            if (!report.IsRetryUpdate)
            {
                Results.Add(report.Item);
                RecordBatchItemStats(report.Item);
            }
            else
            {
                NotifyRateLimitStatsChanged();
            }
        });

        Func<BatchResultItem, CancellationToken, Task>? onResultAsync = saveStarted
            ? (item, token) => _batchResultWriter.WriteAsync(item, token)
            : null;

        try
        {
            await _batchRunner.RunAsync(
                templateRequest,
                paramsSnapshot,
                variablesSnapshot,
                axesSnapshot,
                options,
                progress,
                onResultAsync,
                ct);
        }
        catch (OperationCanceledException)
        {
            // Annulation demandée par l'utilisateur : les résultats déjà reçus restent affichés.
        }
        finally
        {
            if (saveStarted)
            {
                await _batchResultWriter.CompleteAsync();
                LastOutputPath = _batchResultWriter.OutputPath;
                SavedCount = _batchResultWriter.SavedCount;
                SaveErrorCount = _batchResultWriter.ErrorCount;
                SaveSummaryText = $"{SavedCount} enregistrées, {SaveErrorCount} en échec";
            }

            ActiveOutputPathText = string.Empty;
            IsRunning = false;
            NotifyRateLimitStatsChanged();
        }
    }

    private RequestModel BuildTemplateRequest()
    {
        return new RequestModel
        {
            Method = Method,
            Url = Url,
            QueryParams = VaryingParams
                .Select(p => new KeyValueItem(p.Name, p.Value, p.IsEnabled))
                .ToList(),
            Headers = Headers
                .Select(h => new KeyValueItem(h.Key, h.Value, h.IsEnabled))
                .ToList(),
            BodyType = BodyType,
            Body = Body,
            FormFields = FormFields
                .Select(f => new KeyValueItem(f.Key, f.Value, f.IsEnabled))
                .ToList(),
            Auth = new AuthConfig
            {
                Type = _parsedAuth.Type,
                Token = _parsedAuth.Token,
                Username = _parsedAuth.Username,
                Password = _parsedAuth.Password,
                ApiKeyName = _parsedAuth.ApiKeyName,
                ApiKeyValue = _parsedAuth.ApiKeyValue,
                ApiKeyInHeader = _parsedAuth.ApiKeyInHeader
            },
            TimeoutSeconds = _parsedTimeoutSeconds
        };
    }

    private void OnEditableItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (KeyValueItem item in e.OldItems)
            {
                item.PropertyChanged -= OnEditableItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (KeyValueItem item in e.NewItems)
            {
                item.PropertyChanged += OnEditableItemPropertyChanged;
            }
        }

        if (!_isBulkUpdating)
        {
            ScheduleVariableSync();
        }
    }

    private void OnEditableItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isBulkUpdating)
        {
            ScheduleVariableSync();
        }
    }

    private void OnVaryingParamsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (VaryingParam item in e.OldItems)
            {
                item.PropertyChanged -= OnVaryingParamPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (VaryingParam item in e.NewItems)
            {
                item.PropertyChanged += OnVaryingParamPropertyChanged;
            }
        }

        if (!_isBulkUpdating)
        {
            IsConfirmed = false;
            ScheduleVariableSync();
            RecalculatePlan();
        }
    }

    private void OnVaryingParamPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isBulkUpdating)
        {
            return;
        }

        IsConfirmed = false;
        if (e.PropertyName is nameof(VaryingParam.Value)
            or nameof(VaryingParam.IsEnabled)
            or nameof(VaryingParam.IsVarying)
            or nameof(VaryingParam.Name)
            or nameof(VaryingParam.Start)
            or nameof(VaryingParam.End)
            or nameof(VaryingParam.Step))
        {
            ScheduleVariableSync();
        }

        RecalculatePlan();
    }

    private void OnBatchVariablePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            or nameof(BatchVariable.SeedUsed)
            or nameof(BatchVariable.MinText)
            or nameof(BatchVariable.MaxText)
            or nameof(BatchVariable.ByteCount)
            or nameof(BatchVariable.TextLength)
            or nameof(BatchVariable.IncludeLowercase)
            or nameof(BatchVariable.IncludeUppercase)
            or nameof(BatchVariable.IncludeDigits)
            or nameof(BatchVariable.IncludeDashUnderscore)
            or nameof(BatchVariable.IsConfigured)
            or nameof(BatchVariable.IsUsed)
            or nameof(BatchVariable.ValueCount)
            or nameof(BatchVariable.ValuesPreview)
            or nameof(BatchVariable.ErrorText))
        {
            IsConfirmed = false;
            UpdateVariablesValidation();
            RecalculatePlan();
            ScheduleVariableSync();
        }
    }

    private void ScheduleVariableSync()
    {
        CancelPendingVariableSync();
        var cts = new CancellationTokenSource();
        _variableSyncCts = cts;
        _ = DebounceVariableSyncAsync(cts.Token);
    }

    private void CancelPendingVariableSync()
    {
        if (_variableSyncCts is not null)
        {
            _variableSyncCts.Cancel();
            _variableSyncCts.Dispose();
            _variableSyncCts = null;
        }
    }

    private async Task DebounceVariableSyncAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(VariableSyncDebounceMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested)
        {
            SyncVariablesWithTemplate();
        }
    }

    private void SyncVariablesWithTemplate()
    {
        CancelPendingVariableSync();

        var template = BuildTemplateRequest();
        var extracted = _templateEngine.ExtractVariables(template);
        var extractedByName = extracted.ToDictionary(v => v.Name, StringComparer.Ordinal);

        var existingByName = new Dictionary<string, BatchVariable>(StringComparer.Ordinal);
        for (var i = Variables.Count - 1; i >= 0; i--)
        {
            var existing = Variables[i];
            if (extractedByName.TryGetValue(existing.Name, out var info))
            {
                existing.UsedIn = info.LocationsDisplay;
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
                existing.PropertyChanged -= OnBatchVariablePropertyChanged;
                Variables.RemoveAt(i);
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
                UsedIn = item.LocationsDisplay,
                IsUsed = true
            };
            created.PropertyChanged += OnBatchVariablePropertyChanged;
            Variables.Add(created);
        }

        UsedVariablesCount = Variables.Count(v => v.IsUsed);
        HasVariables = Variables.Count > 0;
        UpdateVariablesValidation();
        SyncAxes();
        RecalculatePlan();
    }

    private sealed class AxisCandidate
    {
        public required AxisIdentity Identity { get; init; }
        public required BatchAxisKind Kind { get; init; }
        public required string Name { get; init; }
        public VaryingParam? SourceParam { get; init; }
        public BatchVariable? SourceVariable { get; init; }
        public required Action<BatchAxis> ApplyConfig { get; init; }
    }

    private void SyncAxes()
    {
        var candidates = BuildAxisCandidates();

        // Associer d'abord les axes existants à leurs candidats par référence source puis par identité
        var remainingCandidates = new List<AxisCandidate>(candidates);
        var matchedAxes = new Dictionary<BatchAxis, AxisCandidate>();

        foreach (var existing in Axes)
        {
            AxisCandidate? match = null;

            if (existing.Kind == BatchAxisKind.UrlParam && existing.SourceParam is not null)
            {
                match = remainingCandidates.FirstOrDefault(c =>
                    c.Kind == BatchAxisKind.UrlParam && ReferenceEquals(c.SourceParam, existing.SourceParam));
            }
            else if (existing.Kind == BatchAxisKind.Variable && existing.SourceVariable is not null)
            {
                match = remainingCandidates.FirstOrDefault(c =>
                    c.Kind == BatchAxisKind.Variable && ReferenceEquals(c.SourceVariable, existing.SourceVariable));
            }

            match ??= remainingCandidates.FirstOrDefault(c =>
                c.Kind == existing.Kind && string.Equals(c.Name, existing.Name, StringComparison.Ordinal));

            if (match is not null)
            {
                remainingCandidates.Remove(match);
                matchedAxes[existing] = match;
            }
        }

        // Retirer les axes disparus (tout en conservant leur identité dans _rememberedAxisOrder)
        for (var i = Axes.Count - 1; i >= 0; i--)
        {
            var existing = Axes[i];
            if (!matchedAxes.TryGetValue(existing, out var match))
            {
                Axes.RemoveAt(i);
                continue;
            }

            var oldIdentity = new AxisIdentity(existing.Kind, existing.Name, GetIdentityOccurrence(existing));
            if (oldIdentity != match.Identity)
            {
                ReplaceIdentityInRememberedOrder(oldIdentity, match.Identity);
            }

            existing.Name = match.Name;
            existing.SourceParam = match.SourceParam;
            existing.SourceVariable = match.SourceVariable;
            match.ApplyConfig(existing);
        }

        // Ajouter les axes réapparus à leur position précédente ou les nouveaux axes à la fin
        foreach (var candidate in remainingCandidates)
        {
            var newAxis = new BatchAxis
            {
                Kind = candidate.Kind,
                Name = candidate.Name,
                DisplayName = candidate.Name,
                SourceParam = candidate.SourceParam,
                SourceVariable = candidate.SourceVariable
            };
            candidate.ApplyConfig(newAxis);

            var rememberedIndex = _rememberedAxisOrder.IndexOf(candidate.Identity);
            if (rememberedIndex < 0)
            {
                _rememberedAxisOrder.Add(candidate.Identity);
                Axes.Add(newAxis);
            }
            else
            {
                var insertIndex = FindInsertIndexFromRememberedOrder(rememberedIndex);
                Axes.Insert(insertIndex, newAxis);
            }
        }

        RefreshAxesMetadataAndOrderPreview();
        NotifyOrderPanelStateChanged();
    }

    private List<AxisCandidate> BuildAxisCandidates()
    {
        var candidates = new List<AxisCandidate>();
        var urlOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var param in VaryingParams)
        {
            if (!param.IsEnabled || !param.IsVarying || string.IsNullOrWhiteSpace(param.Name))
            {
                continue;
            }

            var name = param.Name.Trim();
            if (!BatchAxis.TryComputeRangeCount(param.Start, param.End, param.Step, 0, out var count, out _) || count <= 0)
            {
                continue;
            }

            urlOccurrences.TryGetValue(name, out var occ);
            urlOccurrences[name] = occ + 1;

            var capturedStart = param.Start;
            var capturedEnd = param.End;
            var capturedStep = param.Step;

            candidates.Add(new AxisCandidate
            {
                Identity = new AxisIdentity(BatchAxisKind.UrlParam, name, occ),
                Kind = BatchAxisKind.UrlParam,
                Name = name,
                SourceParam = param,
                ApplyConfig = axis => axis.ConfigureRange(capturedStart, capturedEnd, capturedStep, 0)
            });
        }

        foreach (var variable in Variables)
        {
            if (!variable.IsUsed || !variable.IsVarying || !variable.IsConfigured || variable.HasError ||
                variable.ValueCount <= 0 || string.IsNullOrWhiteSpace(variable.Name))
            {
                continue;
            }

            var name = variable.Name;
            if (variable.Mode == VariableMode.Range)
            {
                var capturedStart = variable.ParsedStart;
                var capturedEnd = variable.ParsedEnd;
                var capturedStep = variable.ParsedStep;
                var capturedBase = variable.Base;
                var capturedWidth = variable.Width;
                var capturedUpper = variable.UppercaseHex;
                var capturedPrefix = variable.UsePrefix;

                candidates.Add(new AxisCandidate
                {
                    Identity = new AxisIdentity(BatchAxisKind.Variable, name, 0),
                    Kind = BatchAxisKind.Variable,
                    Name = name,
                    SourceVariable = variable,
                    ApplyConfig = axis => axis.ConfigureRange(
                        capturedStart,
                        capturedEnd,
                        capturedStep,
                        capturedBase,
                        capturedWidth,
                        capturedUpper,
                        capturedPrefix)
                });
            }
            else if (variable.Mode == VariableMode.List)
            {
                var values = BatchVariable.ParseListValues(variable.ListText);
                if (values.Count == 0)
                {
                    continue;
                }

                candidates.Add(new AxisCandidate
                {
                    Identity = new AxisIdentity(BatchAxisKind.Variable, name, 0),
                    Kind = BatchAxisKind.Variable,
                    Name = name,
                    SourceVariable = variable,
                    ApplyConfig = axis => axis.ConfigureList(values)
                });
            }
            else if (variable.Mode == VariableMode.Random)
            {
                var values = variable.GetRandomValuesSnapshot();
                if (values.Count == 0)
                {
                    continue;
                }

                candidates.Add(new AxisCandidate
                {
                    Identity = new AxisIdentity(BatchAxisKind.Variable, name, 0),
                    Kind = BatchAxisKind.Variable,
                    Name = name,
                    SourceVariable = variable,
                    ApplyConfig = axis => axis.ConfigureList(values)
                });
            }
        }

        return candidates;
    }

    private int FindInsertIndexFromRememberedOrder(int rememberedIndex)
    {
        for (var i = 0; i < Axes.Count; i++)
        {
            var axisIdentity = GetIdentityForAxis(Axes[i]);
            var axisRememberedIdx = _rememberedAxisOrder.IndexOf(axisIdentity);
            if (axisRememberedIdx > rememberedIndex)
            {
                return i;
            }
        }

        return Axes.Count;
    }

    private AxisIdentity GetIdentityForAxis(BatchAxis axis) =>
        new(axis.Kind, axis.Name, GetIdentityOccurrence(axis));

    private int GetIdentityOccurrence(BatchAxis targetAxis)
    {
        var occurrence = 0;
        foreach (var axis in Axes)
        {
            if (ReferenceEquals(axis, targetAxis))
            {
                return occurrence;
            }

            if (axis.Kind == targetAxis.Kind && string.Equals(axis.Name, targetAxis.Name, StringComparison.Ordinal))
            {
                occurrence++;
            }
        }

        return 0;
    }

    private void ReplaceIdentityInRememberedOrder(AxisIdentity oldIdentity, AxisIdentity newIdentity)
    {
        var idx = _rememberedAxisOrder.IndexOf(oldIdentity);
        if (idx >= 0 && !_rememberedAxisOrder.Contains(newIdentity))
        {
            _rememberedAxisOrder[idx] = newIdentity;
        }
    }

    private void SwapAxesAt(int firstIndex, int secondIndex)
    {
        var firstIdentity = GetIdentityForAxis(Axes[firstIndex]);
        var secondIdentity = GetIdentityForAxis(Axes[secondIndex]);

        Axes.Move(firstIndex, secondIndex);

        var idxA = _rememberedAxisOrder.IndexOf(firstIdentity);
        var idxB = _rememberedAxisOrder.IndexOf(secondIdentity);
        if (idxA >= 0 && idxB >= 0)
        {
            (_rememberedAxisOrder[idxA], _rememberedAxisOrder[idxB]) =
                (_rememberedAxisOrder[idxB], _rememberedAxisOrder[idxA]);
        }

        RefreshAxesMetadataAndOrderPreview();
        NotifyOrderPanelStateChanged();
    }

    private void RefreshAxesMetadataAndOrderPreview()
    {
        var urlNames = new HashSet<string>(StringComparer.Ordinal);
        var varNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var axis in Axes)
        {
            if (axis.Kind == BatchAxisKind.UrlParam)
            {
                urlNames.Add(axis.Name);
            }
            else
            {
                varNames.Add(axis.Name);
            }
        }

        var ignored = IsOrderIgnored;
        var count = Axes.Count;

        for (var i = 0; i < count; i++)
        {
            var axis = Axes[i];
            var isDuplicateName = axis.Kind == BatchAxisKind.UrlParam
                ? varNames.Contains(axis.Name)
                : urlNames.Contains(axis.Name);

            axis.DisplayName = isDuplicateName
                ? (axis.Kind == BatchAxisKind.UrlParam ? $"{axis.Name} (URL)" : $"{axis.Name} (variable)")
                : axis.Name;

            axis.Rank = i + 1;
            axis.CanMoveUp = i > 0 && !ignored;
            axis.CanMoveDown = i < count - 1 && !ignored;

            if (count == 1)
            {
                axis.RoleText = "Axe unique";
                axis.IsOuterRole = false;
                axis.IsInnerRole = false;
            }
            else if (i == 0)
            {
                axis.RoleText = "Boucle externe";
                axis.IsOuterRole = true;
                axis.IsInnerRole = false;
            }
            else if (i == count - 1)
            {
                axis.RoleText = "Boucle interne";
                axis.IsOuterRole = false;
                axis.IsInnerRole = true;
            }
            else
            {
                axis.RoleText = string.Empty;
                axis.IsOuterRole = false;
                axis.IsInnerRole = false;
            }
        }

        OrderSummaryText = BuildOrderSummaryText();
        RebuildOrderPreview();

        MoveAxisUpCommand.NotifyCanExecuteChanged();
        MoveAxisDownCommand.NotifyCanExecuteChanged();
    }

    private string BuildOrderSummaryText()
    {
        if (Axes.Count < 2)
        {
            return string.Empty;
        }

        if (Axes.Count == 2)
        {
            return $"Pour chaque valeur de {Axes[0].DisplayName}, toutes les valeurs de {Axes[1].DisplayName} sont parcourues avant de passer à la valeur suivante de {Axes[0].DisplayName}.";
        }

        var clauses = string.Join(", ", Axes
            .Take(Axes.Count - 1)
            .Select((a, idx) => idx == 0
                ? $"Pour chaque valeur de {a.DisplayName}"
                : $"pour chaque valeur de {a.DisplayName}"));

        var innermost = Axes[^1].DisplayName;
        var parent = Axes[^2].DisplayName;
        return $"{clauses}, toutes les valeurs de {innermost} sont parcourues avant de passer à la valeur suivante de {parent}.";
    }

    private void RebuildOrderPreview()
    {
        OrderPreview.Clear();
        OrderPreviewOverflowText = string.Empty;

        if (!ShowOrderPanel)
        {
            return;
        }

        long totalCombinations = 1;
        foreach (var axis in Axes)
        {
            if (axis.ValueCount <= 0)
            {
                return;
            }

            var product = (Int128)totalCombinations * (Int128)axis.ValueCount;
            totalCombinations = product >= long.MaxValue ? long.MaxValue : (long)product;
        }

        var take = (int)Math.Min(OrderPreviewLimit, totalCombinations);
        IReadOnlyList<string>? previousValues = null;

        for (var n = 0; n < take; n++)
        {
            var values = _batchRunner.GetCombination(Axes, n);
            var cells = new List<BatchOrderPreviewCell>(Axes.Count);

            for (var col = 0; col < Axes.Count; col++)
            {
                var full = values[col];
                var display = full.Length > 40 ? $"{full[..40]}…" : full;
                var changed = previousValues is not null &&
                              !string.Equals(full, previousValues[col], StringComparison.Ordinal);

                cells.Add(new BatchOrderPreviewCell
                {
                    DisplayValue = display,
                    FullValue = full,
                    HasChanged = changed
                });
            }

            OrderPreview.Add(new BatchOrderPreviewRow
            {
                Index = n + 1,
                Cells = cells
            });

            previousValues = values;
        }

        if (totalCombinations > OrderPreviewLimit)
        {
            var remaining = totalCombinations - OrderPreviewLimit;
            OrderPreviewOverflowText = totalCombinations == long.MaxValue
                ? "… et de nombreuses autres"
                : remaining == 1
                    ? "… et 1 autre"
                    : $"… et {remaining:N0} autres";
        }
    }

    private void NotifyOrderPanelStateChanged()
    {
        OnPropertyChanged(nameof(HasMultipleAxes));
        OnPropertyChanged(nameof(ShowOrderPanel));
        OnPropertyChanged(nameof(IsOrderIgnored));
    }

    private void UpdateVariablesValidation()
    {
        var template = BuildTemplateRequest();
        var errors = new List<string>();

        foreach (var unknownDollar in _templateEngine.GetUnknownPredefinedMarkers(template))
        {
            errors.Add($"{{{{{unknownDollar}}}}} : variable prédéfinie inconnue (disponibles : $index, $guid, $timestamp).");
        }

        var extracted = _templateEngine.ExtractVariables(template);
        var definedByName = Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);

        foreach (var ext in extracted)
        {
            if (!definedByName.TryGetValue(ext.Name, out var variable))
            {
                errors.Add($"{{{{{ext.Name}}}}} : variable utilisée mais jamais définie.");
            }
            else if (!variable.IsConfigured)
            {
                errors.Add($"{{{{{ext.Name}}}}} : saisis une valeur ou coche « Faire varier ».");
            }
            else if (variable.HasError)
            {
                errors.Add($"{{{{{ext.Name}}}}} : {variable.ErrorText}");
            }
        }

        VariablesError = errors.Count == 0
            ? null
            : string.Join(Environment.NewLine, errors);
    }

    private void RecalculatePlan()
    {
        if (!HasParsed)
        {
            TotalRequests = 0;
            NeedsConfirmation = false;
            ExceedsHardLimit = false;
            PlanError = null;
            RefreshWarnings([]);
            return;
        }

        if (HasVariablesError)
        {
            TotalRequests = 0;
            NeedsConfirmation = false;
            ExceedsHardLimit = false;
            PlanError = null;
            RefreshWarnings([]);
            return;
        }

        var plan = _batchRunner.BuildPlan(VaryingParams, Variables, SelectedMode);
        TotalRequests = plan.TotalRequests;
        ExceedsHardLimit = plan.ExceedsHardLimit;
        PlanError = plan.ValidationError;
        NeedsConfirmation = !plan.ExceedsHardLimit && plan.TotalRequests > BatchRunner.ConfirmationThreshold;

        RefreshWarnings(plan.Warnings);
    }

    private void RefreshWarnings(IEnumerable<string> planWarnings)
    {
        Warnings.Clear();
        foreach (var w in CurlParser.MergeAndOrderWarnings(_parseWarnings, planWarnings))
        {
            Warnings.Add(w);
        }
    }

    private void ValidateAndSyncRateLimitPolicy()
    {
        if (!int.TryParse(RateLimitMaxRetryAfterSecondsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRetryAfter))
        {
            RateLimitValidationError = "Plafond Retry-After : saisis un entier >= 0.";
            return;
        }

        if (!int.TryParse(RateLimitBackoffInitialMsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialMs))
        {
            RateLimitValidationError = "Délai initial de backoff : saisis un entier >= 0.";
            return;
        }

        var normalizedMultiplierText = RateLimitBackoffMultiplierText.Trim().Replace(',', '.');
        if (!double.TryParse(normalizedMultiplierText, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
        {
            RateLimitValidationError = "Multiplicateur de backoff : saisis un nombre supérieur à 1.";
            return;
        }

        if (!int.TryParse(RateLimitBackoffMaxMsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxMs))
        {
            RateLimitValidationError = "Délai maximal de backoff : saisis un entier >= 0.";
            return;
        }

        if (!int.TryParse(RateLimitMaxRetriesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRetries))
        {
            RateLimitValidationError = "Tentatives max par requête : saisis un entier entre 0 et 20.";
            return;
        }

        if (!int.TryParse(RateLimitQuotaThresholdText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quotaThreshold))
        {
            RateLimitValidationError = "Seuil de ralentissement préventif : saisis un entier >= 0.";
            return;
        }

        if (!RateLimitPolicy.TryParseExtraStatusCodes(RateLimitExtraStatusCodesText, out var extraCodes, out var codesError))
        {
            RateLimitValidationError = codesError;
            return;
        }

        RateLimitPolicy.MaxRetryAfterSeconds = maxRetryAfter;
        RateLimitPolicy.BackoffInitialMs = initialMs;
        RateLimitPolicy.BackoffMultiplier = multiplier;
        RateLimitPolicy.BackoffMaxMs = maxMs;
        RateLimitPolicy.MaxRetriesPerRequest = maxRetries;
        RateLimitPolicy.QuotaThreshold = quotaThreshold;
        RateLimitPolicy.ExtraStatusCodes = extraCodes;
        RateLimitPolicy.PauseFloorMs = Math.Max(0, DelayMs);

        RateLimitValidationError = RateLimitPolicy.TryValidate(out var policyError)
            ? null
            : policyError;
    }

    private void ResetRateLimitStats()
    {
        RetryCount = 0;
        SuccessCount = 0;
        RateLimitedCount = 0;
        RateLimitedFailedCount = 0;
        OtherErrorCount = 0;
        _lastRateLimitedFailureRetries = 0;
        NotifyRateLimitStatsChanged();
    }

    private void RecordBatchItemStats(BatchResultItem item)
    {
        var isSuccess = string.IsNullOrEmpty(item.ErrorMessage) && item.StatusCode is >= 200 and < 300;
        if (isSuccess)
        {
            SuccessCount++;
        }

        if (item.WasRateLimited)
        {
            RateLimitedCount++;
            if (!isSuccess)
            {
                RateLimitedFailedCount++;
                _lastRateLimitedFailureRetries = item.RetryAttempts;
            }
        }
        else if (!isSuccess)
        {
            OtherErrorCount++;
        }

        NotifyRateLimitStatsChanged();
    }

    private void NotifyRateLimitStatsChanged()
    {
        OnPropertyChanged(nameof(HasBatchStats));
        OnPropertyChanged(nameof(HasRateLimitOccurrences));
        OnPropertyChanged(nameof(SuccessSummaryPart));
        OnPropertyChanged(nameof(RateLimitSummaryPart));
        OnPropertyChanged(nameof(OtherErrorSummaryPart));
        OnPropertyChanged(nameof(RateLimitSummaryText));
    }

    private static string GetDefaultOutputFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "ApiTester", "lots");
    }
}
