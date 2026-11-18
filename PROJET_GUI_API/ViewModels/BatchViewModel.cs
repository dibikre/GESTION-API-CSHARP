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

public partial class BatchViewModel : ViewModelBase
{
    private readonly ICurlParser _curlParser;
    private readonly IBatchRunner _batchRunner;
    private readonly IFolderPickerService _folderPickerService;
    private readonly IBatchResultWriter _batchResultWriter;
    private readonly List<string> _parseWarnings = [];
    private AuthConfig _parsedAuth = new();
    private int _parsedTimeoutSeconds = 30;

    public BatchViewModel(
        ICurlParser curlParser,
        IBatchRunner batchRunner,
        IFolderPickerService folderPickerService,
        IBatchResultWriter batchResultWriter)
    {
        _curlParser = curlParser ?? throw new ArgumentNullException(nameof(curlParser));
        _batchRunner = batchRunner ?? throw new ArgumentNullException(nameof(batchRunner));
        _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        _batchResultWriter = batchResultWriter ?? throw new ArgumentNullException(nameof(batchResultWriter));

        VaryingParams.CollectionChanged += OnVaryingParamsCollectionChanged;
        Warnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWarnings));
    }

    public static IReadOnlyList<BatchMode> BatchModes { get; } = Enum.GetValues<BatchMode>();
    public static IReadOnlyList<SaveMode> SaveModes { get; } = Enum.GetValues<SaveMode>();
    public static IReadOnlyList<GroupedFormat> GroupedFormats { get; } = Enum.GetValues<GroupedFormat>();

    public Action<RequestModel>? OpenInSingleRequestCallback { get; set; }

    [ObservableProperty]
    public partial string CurlText { get; set; } = string.Empty;

    public ObservableCollection<string> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInSingleRequestCommand))]
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
    public partial BodyType BodyType { get; set; } = BodyType.None;

    public ObservableCollection<KeyValueItem> FormFields { get; } = [];

    public ObservableCollection<VaryingParam> VaryingParams { get; } = [];

    [ObservableProperty]
    public partial BatchMode SelectedMode { get; set; } = BatchMode.Combinations;

    [ObservableProperty]
    public partial int DelayMs { get; set; } = 500;

    [ObservableProperty]
    public partial bool StopOnFailure { get; set; }

    [ObservableProperty]
    public partial bool KeepBodies { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalRequestsText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial int TotalRequests { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool NeedsConfirmation { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsConfirmed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool ExceedsHardLimit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanError))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? PlanError { get; set; }

    public bool HasPlanError => !string.IsNullOrWhiteSpace(PlanError);

    public string TotalRequestsText => $"{TotalRequests} requêtes seront envoyées";

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

    public string ProgressText => $"{ProgressCurrent} / {ProgressTotal}";

    public ObservableCollection<BatchResultItem> Results { get; } = [];

    [ObservableProperty]
    public partial BatchResultItem? SelectedResult { get; set; }

    public ResponseViewModel SelectedResponse { get; } = new();

    partial void OnSelectedModeChanged(BatchMode value)
    {
        IsConfirmed = false;
        RecalculatePlan();
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

        HasParsed = !string.IsNullOrWhiteSpace(Url);
        IsConfirmed = false;
        RecalculatePlan();
    }

    [RelayCommand]
    private void Clear()
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
        TotalRequests = 0;
        NeedsConfirmation = false;
        IsConfirmed = false;
        ExceedsHardLimit = false;
        PlanError = null;
        ProgressCurrent = 0;
        ProgressTotal = 0;
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

    private bool CanOpenInSingleRequest() => HasParsed;

    [RelayCommand(CanExecute = nameof(CanOpenInSingleRequest))]
    private void OpenInSingleRequest()
    {
        var requestModel = BuildTemplateRequest();
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

    private bool CanRun() =>
        HasParsed &&
        !IsRunning &&
        TotalRequests > 0 &&
        !ExceedsHardLimit &&
        string.IsNullOrEmpty(PlanError) &&
        (!NeedsConfirmation || IsConfirmed);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRun))]
    private async Task RunAsync(CancellationToken ct)
    {
        RecalculatePlan();
        if (!CanRun())
        {
            return;
        }

        Results.Clear();
        SelectedResult = null;
        SelectedResponse.Clear();
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
        var paramsSnapshot = VaryingParams
            .Select(p => new VaryingParam
            {
                Name = p.Name,
                Value = p.Value,
                IsEnabled = p.IsEnabled,
                IsVarying = p.IsVarying,
                Start = p.Start,
                End = p.End,
                Step = p.Step
            })
            .ToList();

        var options = new BatchRunOptions
        {
            Mode = SelectedMode,
            DelayMs = Math.Max(0, DelayMs),
            StopOnFailure = StopOnFailure,
            KeepBodies = KeepBodies
        };

        ProgressCurrent = 0;
        ProgressTotal = TotalRequests;
        IsRunning = true;

        var progress = new Progress<BatchProgressReport>(report =>
        {
            ProgressCurrent = report.Index;
            ProgressTotal = report.Total;
            Results.Add(report.Item);
        });

        Func<BatchResultItem, CancellationToken, Task>? onResultAsync = saveStarted
            ? (item, token) => _batchResultWriter.WriteAsync(item, token)
            : null;

        try
        {
            await _batchRunner.RunAsync(templateRequest, paramsSnapshot, options, progress, onResultAsync, ct);
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

        IsConfirmed = false;
        RecalculatePlan();
    }

    private void OnVaryingParamPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsConfirmed = false;
        RecalculatePlan();
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

        var plan = _batchRunner.BuildPlan(VaryingParams, SelectedMode);
        TotalRequests = plan.TotalRequests;
        ExceedsHardLimit = plan.ExceedsHardLimit;
        PlanError = plan.ValidationError;
        NeedsConfirmation = !plan.ExceedsHardLimit && plan.TotalRequests > BatchRunner.ConfirmationThreshold;

        RefreshWarnings(plan.Warnings);
    }

    private void RefreshWarnings(IEnumerable<string> planWarnings)
    {
        Warnings.Clear();
        foreach (var w in _parseWarnings)
        {
            Warnings.Add(w);
        }

        foreach (var w in planWarnings)
        {
            Warnings.Add(w);
        }
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
