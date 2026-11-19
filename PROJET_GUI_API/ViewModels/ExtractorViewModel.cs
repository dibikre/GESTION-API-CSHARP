using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class ExtractorViewModel : ViewModelBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private readonly IExtractionSuggester _suggester;
    private readonly IResponseExtractor _responseExtractor;
    private readonly IClipboardService? _clipboardService;
    private readonly ChainViewModel? _chainViewModel;
    private readonly ObservableCollection<ChainStep> _emptyChainSteps = [];
    private bool _hasCapturedSelection;
    private int _capturedStart;
    private int _capturedLength;
    private bool _suppressAutoLoad;

    public ExtractorViewModel(
        IExtractionSuggester suggester,
        IResponseExtractor responseExtractor,
        IClipboardService? clipboardService = null,
        ChainViewModel? chainViewModel = null)
    {
        _suggester = suggester ?? throw new ArgumentNullException(nameof(suggester));
        _responseExtractor = responseExtractor ?? throw new ArgumentNullException(nameof(responseExtractor));
        _clipboardService = clipboardService;
        _chainViewModel = chainViewModel;

        if (_chainViewModel is not null)
        {
            _chainViewModel.Steps.CollectionChanged += OnChainStepsCollectionChanged;
            _chainViewModel.PropertyChanged += OnChainViewModelPropertyChanged;
            SyncTargetStepSelection();
        }
    }

    public static IReadOnlyList<ResponseFormat> Formats { get; } = Enum.GetValues<ResponseFormat>();

    public static IReadOnlyList<ExtractorValueKind> ValueKinds { get; } = Enum.GetValues<ExtractorValueKind>();

    public Action? SwitchToChainCallback { get; set; }

    public ObservableCollection<ExtractorNode> TreeNodes { get; } = [];

    public ObservableCollection<ChainStep> ChainSteps => _chainViewModel?.Steps ?? _emptyChainSteps;

    public bool IsChainAvailable => _chainViewModel is not null && _chainViewModel.Steps.Count > 0;

    [ObservableProperty]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AnalyzedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ResponseFormat Format { get; set; } = ResponseFormat.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonResult))]
    [NotifyPropertyChangedFor(nameof(ShowNoJsonPathNote))]
    public partial ResponseFormat DetectedFormat { get; set; } = ResponseFormat.Text;

    [ObservableProperty]
    public partial string DetectedFormatText { get; set; } = "Format non analysé";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonResult))]
    public partial bool HasLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonResult))]
    public partial bool IsTreeMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonResult))]
    public partial bool IsManualMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysisMessage))]
    public partial string? AnalysisMessage { get; set; }

    public bool HasAnalysisMessage => !string.IsNullOrWhiteSpace(AnalysisMessage);

    [ObservableProperty]
    public partial ExtractorNode? SelectedNode { get; set; }

    [ObservableProperty]
    public partial int SelectionStart { get; set; }

    [ObservableProperty]
    public partial int SelectionEnd { get; set; }

    [ObservableProperty]
    public partial int ContextLength { get; set; } = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomValueKind))]
    public partial ExtractorValueKind ValueKind { get; set; } = ExtractorValueKind.NonWhitespace;

    public bool IsCustomValueKind => ValueKind == ExtractorValueKind.Custom;

    [ObservableProperty]
    public partial string CustomPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseTrailingAnchor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestedJsonPath))]
    [NotifyPropertyChangedFor(nameof(HasAnySuggestion))]
    [NotifyPropertyChangedFor(nameof(ShowNoJsonPathNote))]
    [NotifyCanExecuteChangedFor(nameof(CopyJsonPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToChainCommand))]
    public partial string? SuggestedJsonPath { get; set; }

    public bool HasSuggestedJsonPath => !string.IsNullOrWhiteSpace(SuggestedJsonPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestedRegex))]
    [NotifyPropertyChangedFor(nameof(HasAnySuggestion))]
    [NotifyPropertyChangedFor(nameof(ShowNoJsonPathNote))]
    [NotifyCanExecuteChangedFor(nameof(CopyRegexCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToChainCommand))]
    public partial string? SuggestedRegex { get; set; }

    public bool HasSuggestedRegex => !string.IsNullOrWhiteSpace(SuggestedRegex);

    public bool HasAnySuggestion => HasSuggestedJsonPath || HasSuggestedRegex;

    public bool IsJsonResult => HasLoaded && DetectedFormat == ResponseFormat.Json && IsTreeMode;

    public bool ShowNoJsonPathNote =>
        HasAnySuggestion &&
        !IsJsonResult &&
        DetectedFormat is ResponseFormat.Xml or ResponseFormat.Html;

    [ObservableProperty]
    public partial int OccurrenceCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOccurrenceWarning))]
    public partial string? OccurrenceWarningText { get; set; }

    public bool HasOccurrenceWarning => !string.IsNullOrWhiteSpace(OccurrenceWarningText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfoMessage))]
    public partial string? InfoMessage { get; set; }

    public bool HasInfoMessage => !string.IsNullOrWhiteSpace(InfoMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResultText { get; set; }

    public bool HasTestResult => !string.IsNullOrWhiteSpace(TestResultText);

    [ObservableProperty]
    public partial LiveTestStatus TestStatus { get; set; } = LiveTestStatus.None;

    [ObservableProperty]
    public partial string TestStatusBadgeText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRegexTestResult))]
    public partial string? RegexTestResultText { get; set; }

    public bool HasRegexTestResult => !string.IsNullOrWhiteSpace(RegexTestResultText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyFeedback))]
    public partial string? CopyFeedbackText { get; set; }

    public bool HasCopyFeedback => !string.IsNullOrWhiteSpace(CopyFeedbackText);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToChainCommand))]
    public partial ChainStep? SelectedTargetStep { get; set; }

    public void LoadContent(string text, string? contentType)
    {
        _suppressAutoLoad = true;
        try
        {
            InputText = text ?? string.Empty;
            Format = DeduceFormatFromContentType(contentType);
        }
        finally
        {
            _suppressAutoLoad = false;
        }

        Load();
    }

    partial void OnFormatChanged(ResponseFormat value)
    {
        if (!_suppressAutoLoad && !string.IsNullOrWhiteSpace(InputText))
        {
            Load();
        }
    }

    partial void OnContextLengthChanged(int value)
    {
        if (value < 1)
        {
            ContextLength = 1;
            return;
        }

        if (value > 100)
        {
            ContextLength = 100;
            return;
        }

        RefreshManualSelectionIfActive();
    }

    partial void OnValueKindChanged(ExtractorValueKind value)
    {
        RefreshManualSelectionIfActive();
    }

    partial void OnCustomPatternChanged(string value)
    {
        if (ValueKind == ExtractorValueKind.Custom)
        {
            RefreshManualSelectionIfActive();
        }
    }

    partial void OnUseTrailingAnchorChanged(bool value)
    {
        RefreshManualSelectionIfActive();
    }

    [RelayCommand]
    private void Load()
    {
        ResetSuggestions();
        TreeNodes.Clear();
        _hasCapturedSelection = false;
        SelectionStart = 0;
        SelectionEnd = 0;
        SelectedNode = null;

        AnalyzedText = InputText ?? string.Empty;
        var analysis = _suggester.Analyze(AnalyzedText, Format);

        HasLoaded = true;
        DetectedFormat = analysis.DetectedFormat;
        DetectedFormatText = $"Détecté : {FormatToDisplayLabel(analysis.DetectedFormat)}";
        AnalysisMessage = analysis.StatusMessage;

        if (analysis.FallbackToText || analysis.RootNodes.Count == 0)
        {
            IsTreeMode = false;
            IsManualMode = true;
            return;
        }

        foreach (var node in analysis.RootNodes)
        {
            TreeNodes.Add(node);
        }

        IsTreeMode = true;
        IsManualMode = false;
    }

    [RelayCommand]
    private void ToggleNodeExpand(ExtractorNode? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        node.IsExpanded = !node.IsExpanded;
    }

    [RelayCommand]
    private void SelectNode(ExtractorNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (!node.IsSelectable)
        {
            if (node.HasChildren)
            {
                node.IsExpanded = !node.IsExpanded;
            }
            return;
        }

        if (SelectedNode is not null)
        {
            SelectedNode.IsSelected = false;
        }

        node.IsSelected = true;
        SelectedNode = node;
        CopyFeedbackText = null;

        var suggestion = _suggester.SuggestForNode(AnalyzedText, node);
        ApplySuggestionAndTest(suggestion);
    }

    [RelayCommand]
    private void UseSelection()
    {
        CopyFeedbackText = null;

        var start = Math.Min(SelectionStart, SelectionEnd);
        var end = Math.Max(SelectionStart, SelectionEnd);
        var length = end - start;

        if (length <= 0)
        {
            _hasCapturedSelection = false;
            ResetSuggestions();
            TestStatus = LiveTestStatus.Error;
            TestStatusBadgeText = "Aucune sélection";
            TestResultText = "Sélectionne d'abord un passage dans le texte ci-dessus.";
            return;
        }

        _hasCapturedSelection = true;
        _capturedStart = start;
        _capturedLength = length;

        var suggestion = _suggester.SuggestManualRegex(
            AnalyzedText,
            _capturedStart,
            _capturedLength,
            ContextLength,
            ValueKind,
            CustomPattern,
            UseTrailingAnchor);

        ApplySuggestionAndTest(suggestion);
    }

    [RelayCommand(CanExecute = nameof(HasSuggestedJsonPath))]
    private async Task CopyJsonPathAsync()
    {
        if (string.IsNullOrWhiteSpace(SuggestedJsonPath) || _clipboardService is null)
        {
            return;
        }

        if (await _clipboardService.SetTextAsync(SuggestedJsonPath))
        {
            CopyFeedbackText = "Chemin JSONPath copié dans le presse-papiers.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSuggestedRegex))]
    private async Task CopyRegexAsync()
    {
        if (string.IsNullOrWhiteSpace(SuggestedRegex) || _clipboardService is null)
        {
            return;
        }

        if (await _clipboardService.SetTextAsync(SuggestedRegex))
        {
            CopyFeedbackText = "Expression régulière copiée dans le presse-papiers.";
        }
    }

    private bool CanExecuteSendToChain()
    {
        return IsChainAvailable && HasAnySuggestion;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSendToChain))]
    private void SendToChain()
    {
        if (_chainViewModel is null || _chainViewModel.Steps.Count == 0 || !HasAnySuggestion)
        {
            return;
        }

        var targetStep = SelectedTargetStep ?? _chainViewModel.SelectedStep ?? _chainViewModel.Steps.FirstOrDefault();
        if (targetStep is null)
        {
            return;
        }

        var useJsonPath = HasSuggestedJsonPath;
        var extraction = new ChainExtraction
        {
            Source = ChainExtractionSource.Body,
            Kind = useJsonPath ? ExtractionKind.JsonPath : ExtractionKind.Regex,
            Expression = useJsonPath ? SuggestedJsonPath! : SuggestedRegex!,
            RegexGroup = 1,
            VariableName = string.Empty,
            IsRequired = true
        };

        targetStep.Extractions.Add(extraction);
        _chainViewModel.SelectedStep = targetStep;
        SwitchToChainCallback?.Invoke();
    }

    private void RefreshManualSelectionIfActive()
    {
        if (!_hasCapturedSelection || !IsManualMode)
        {
            return;
        }

        var suggestion = _suggester.SuggestManualRegex(
            AnalyzedText,
            _capturedStart,
            _capturedLength,
            ContextLength,
            ValueKind,
            CustomPattern,
            UseTrailingAnchor);

        ApplySuggestionAndTest(suggestion);
    }

    private void ApplySuggestionAndTest(ExtractionSuggestion suggestion)
    {
        SuggestedJsonPath = suggestion.JsonPath;
        SuggestedRegex = suggestion.Regex;
        OccurrenceCount = suggestion.OccurrenceCount;
        OccurrenceWarningText = suggestion.OccurrenceWarning;
        InfoMessage = suggestion.InfoMessage;

        if (!HasSuggestedJsonPath && !HasSuggestedRegex)
        {
            TestStatus = LiveTestStatus.Error;
            TestStatusBadgeText = "Aucune correspondance";
            TestResultText = suggestion.InfoMessage ?? "Aucune expression proposée.";
            RegexTestResultText = null;
            return;
        }

        var tempResponse = new ResponseModel { Body = AnalyzedText };

        if (HasSuggestedJsonPath)
        {
            var jsonOutcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
            {
                Source = ChainExtractionSource.Body,
                Kind = ExtractionKind.JsonPath,
                Expression = SuggestedJsonPath!
            });

            if (jsonOutcome.IsSuccess)
            {
                TestStatus = LiveTestStatus.Success;
                TestStatusBadgeText = "1 correspondance";
                TestResultText = jsonOutcome.Value;
            }
            else
            {
                TestStatus = LiveTestStatus.Error;
                TestStatusBadgeText = "Aucune correspondance";
                TestResultText = jsonOutcome.ErrorMessage ?? "Échec d'extraction JSONPath.";
            }

            if (HasSuggestedRegex)
            {
                var regexOutcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
                {
                    Source = ChainExtractionSource.Body,
                    Kind = ExtractionKind.Regex,
                    Expression = SuggestedRegex!,
                    RegexGroup = 1
                });

                if (regexOutcome.IsSuccess)
                {
                    var count = CountMatchesSafe(AnalyzedText, SuggestedRegex!);
                    RegexTestResultText = count > 1
                        ? $"Test regex : {count} correspondances (1re capturée : « {regexOutcome.Value} »)"
                        : $"Test regex : valeur capturée « {regexOutcome.Value} »";
                }
                else
                {
                    RegexTestResultText = $"Test regex : {regexOutcome.ErrorMessage}";
                }
            }
            else
            {
                RegexTestResultText = null;
            }

            return;
        }

        // Mode Regex principal (XML / HTML / Texte)
        RegexTestResultText = null;
        var outcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
        {
            Source = ChainExtractionSource.Body,
            Kind = ExtractionKind.Regex,
            Expression = SuggestedRegex!,
            RegexGroup = 1
        });

        if (outcome.IsSuccess)
        {
            var matchCount = CountMatchesSafe(AnalyzedText, SuggestedRegex!);
            if (matchCount > 1)
            {
                TestStatus = LiveTestStatus.Warning;
                TestStatusBadgeText = $"{matchCount} correspondances";
                TestResultText = $"{matchCount} correspondances trouvées (1re capturée : {outcome.Value})";
            }
            else
            {
                TestStatus = LiveTestStatus.Success;
                TestStatusBadgeText = "1 correspondance";
                TestResultText = outcome.Value;
            }
        }
        else
        {
            TestStatus = LiveTestStatus.Error;
            TestStatusBadgeText = "Aucune correspondance";
            TestResultText = outcome.ErrorMessage ?? "Aucune correspondance trouvée.";
        }
    }

    private void ResetSuggestions()
    {
        SuggestedJsonPath = null;
        SuggestedRegex = null;
        OccurrenceCount = 0;
        OccurrenceWarningText = null;
        InfoMessage = null;
        TestResultText = null;
        TestStatus = LiveTestStatus.None;
        TestStatusBadgeText = string.Empty;
        RegexTestResultText = null;
        CopyFeedbackText = null;
    }

    private void OnChainStepsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsChainAvailable));
        SyncTargetStepSelection();
        SendToChainCommand.NotifyCanExecuteChanged();
    }

    private void OnChainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChainViewModel.SelectedStep))
        {
            SyncTargetStepSelection();
        }
    }

    private void SyncTargetStepSelection()
    {
        if (_chainViewModel is null || _chainViewModel.Steps.Count == 0)
        {
            SelectedTargetStep = null;
            return;
        }

        if (SelectedTargetStep is null || !_chainViewModel.Steps.Contains(SelectedTargetStep))
        {
            SelectedTargetStep = _chainViewModel.SelectedStep ?? _chainViewModel.Steps[0];
        }
    }

    private static ResponseFormat DeduceFormatFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return ResponseFormat.Auto;
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return ResponseFormat.Json;
        }

        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return ResponseFormat.Html;
        }

        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return ResponseFormat.Xml;
        }

        return ResponseFormat.Auto;
    }

    private static string FormatToDisplayLabel(ResponseFormat format) => format switch
    {
        ResponseFormat.Json => "JSON",
        ResponseFormat.Xml => "XML",
        ResponseFormat.Html => "HTML",
        ResponseFormat.Text => "Texte",
        _ => "Auto"
    };

    private static int CountMatchesSafe(string input, string pattern)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern))
        {
            return 0;
        }

        try
        {
            return Regex.Matches(input, pattern, RegexOptions.CultureInvariant, RegexTimeout).Count;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
