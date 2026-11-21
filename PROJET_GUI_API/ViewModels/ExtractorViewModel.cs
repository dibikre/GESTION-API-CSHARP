using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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
    private static readonly CultureInfo FrenchCulture = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Regex ArrayIndexRegex = new(@"\[\s*-?\d+\s*\]", RegexOptions.Compiled);
    private readonly IExtractionSuggester _suggester;
    private readonly IResponseExtractor _responseExtractor;
    private readonly IClipboardService? _clipboardService;
    private readonly IOverlayEditorService? _overlayEditor;
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
        ChainViewModel? chainViewModel = null,
        IOverlayEditorService? overlayEditor = null)
    {
        _suggester = suggester ?? throw new ArgumentNullException(nameof(suggester));
        _responseExtractor = responseExtractor ?? throw new ArgumentNullException(nameof(responseExtractor));
        _clipboardService = clipboardService;
        _chainViewModel = chainViewModel;
        _overlayEditor = overlayEditor;

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
    [NotifyPropertyChangedFor(nameof(InputCharCountText))]
    public partial string InputText { get; set; } = string.Empty;

    public string InputCharCountText
    {
        get
        {
            var count = InputText?.Length ?? 0;
            return count switch
            {
                0 => "0 caractère",
                1 => "1 caractère",
                _ => $"{count.ToString("N0", FrenchCulture).Replace('\u202F', ' ').Replace('\u00A0', ' ')} caractères"
            };
        }
    }

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

    public bool IsTreeMode
    {
        get => _isTreeMode;
        set
        {
            if (SetProperty(ref _isTreeMode, value))
            {
                OnPropertyChanged(nameof(IsJsonResult));
            }
        }
    }
    private bool _isTreeMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonResult))]
    public partial bool IsManualMode { get; set; }

    public bool HasTreeNodes => TreeNodes.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysisMessage))]
    public partial string? AnalysisMessage { get; set; }

    public bool HasAnalysisMessage => !string.IsNullOrWhiteSpace(AnalysisMessage);

    [ObservableProperty]
    public partial ExtractorNode? SelectedNode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleExpandAllLabel))]
    [NotifyPropertyChangedFor(nameof(ToggleExpandAllTooltip))]
    public partial bool HasAnyExpandedNode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleExpandAllCommand))]
    public partial bool HasExpandableNodes { get; set; }

    public string ToggleExpandAllLabel => HasAnyExpandedNode ? "Tout replier" : "Tout déplier";

    public string ToggleExpandAllTooltip => HasAnyExpandedNode
        ? "Replier tous les nœuds de l'arbre"
        : "Déplier tous les nœuds de l'arbre";

    [ObservableProperty]
    public partial int SelectionStart { get; set; }

    [ObservableProperty]
    public partial int SelectionEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectionPreview))]
    public partial string? SelectionPreviewText { get; set; }

    public bool HasSelectionPreview => !string.IsNullOrWhiteSpace(SelectionPreviewText);

    [ObservableProperty]
    public partial int ContextLength { get; set; } = 15;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomValueKind))]
    public partial ExtractorValueKind ValueKind { get; set; } = ExtractorValueKind.Auto;

    public bool IsCustomValueKind => ValueKind == ExtractorValueKind.Custom;

    [ObservableProperty]
    public partial string CustomPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseTrailingAnchor { get; set; }

    [ObservableProperty]
    public partial bool ExtractAsList { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestedJsonPath))]
    [NotifyPropertyChangedFor(nameof(HasAnySuggestion))]
    [NotifyPropertyChangedFor(nameof(ShowNoJsonPathNote))]
    [NotifyCanExecuteChangedFor(nameof(CopyJsonPathCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToChainCommand))]
    public partial string? SuggestedJsonPath { get; set; }

    public bool HasSuggestedJsonPath => !string.IsNullOrWhiteSpace(SuggestedJsonPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestedHtmlPath))]
    [NotifyPropertyChangedFor(nameof(HasAnySuggestion))]
    [NotifyPropertyChangedFor(nameof(ShowNoJsonPathNote))]
    [NotifyPropertyChangedFor(nameof(HtmlPathSectionTitle))]
    [NotifyCanExecuteChangedFor(nameof(CopyHtmlPathCommand))]
    public partial string? SuggestedHtmlPath { get; set; }

    public bool HasSuggestedHtmlPath => !string.IsNullOrWhiteSpace(SuggestedHtmlPath);

    public string HtmlPathSectionTitle => DetectedFormat == ResponseFormat.Xml
        ? "Chemin XPath"
        : "Chemin HTMLPath (XPath)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestedRegex))]
    [NotifyPropertyChangedFor(nameof(HasAnySuggestion))]
    [NotifyPropertyChangedFor(nameof(ShowNoJsonPathNote))]
    [NotifyCanExecuteChangedFor(nameof(CopyRegexCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToChainCommand))]
    public partial string? SuggestedRegex { get; set; }

    public bool HasSuggestedRegex => !string.IsNullOrWhiteSpace(SuggestedRegex);

    public bool HasAnySuggestion => HasSuggestedJsonPath || HasSuggestedHtmlPath || HasSuggestedRegex;

    public bool IsJsonResult => HasLoaded && DetectedFormat == ResponseFormat.Json && IsTreeMode;

    public bool ShowNoJsonPathNote =>
        HasAnySuggestion &&
        !IsJsonResult &&
        !HasSuggestedHtmlPath &&
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

    partial void OnInputTextChanged(string value)
    {
        _hasCapturedSelection = false;
        SelectionStart = 0;
        SelectionEnd = 0;
        SelectionPreviewText = null;
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

    partial void OnExtractAsListChanged(bool value)
    {
        if (IsTreeMode && SelectedNode is not null)
        {
            var suggestion = _suggester.SuggestForNode(InputText ?? string.Empty, SelectedNode);
            ApplySuggestionAndTest(suggestion);
        }
        else if (IsManualMode && _hasCapturedSelection)
        {
            RefreshManualSelectionIfActive();
        }
    }

    [RelayCommand]
    private void EnlargeInputText()
    {
        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            "Réponse collée",
            InputText,
            IsReadOnly: false,
            OnSave: text =>
            {
                InputText = text;
                Load();
            }));
    }

    [RelayCommand]
    private void Load()
    {
        ResetSuggestions();
        TreeNodes.Clear();
        _hasCapturedSelection = false;
        SelectionStart = 0;
        SelectionEnd = 0;
        SelectionPreviewText = null;
        SelectedNode = null;

        var sourceText = InputText ?? string.Empty;
        var analysis = _suggester.Analyze(sourceText, Format);

        HasLoaded = true;
        DetectedFormat = analysis.DetectedFormat;
        DetectedFormatText = $"Détecté : {FormatToDisplayLabel(analysis.DetectedFormat)}";
        AnalysisMessage = analysis.StatusMessage;

        if (analysis.FallbackToText || analysis.RootNodes.Count == 0)
        {
            IsTreeMode = false;
            IsManualMode = true;
            OnPropertyChanged(nameof(HasTreeNodes));
            OnPropertyChanged(nameof(IsJsonResult));
            UpdateTreeExpandState();
            return;
        }

        foreach (var node in analysis.RootNodes)
        {
            TreeNodes.Add(node);
        }

        IsTreeMode = true;
        IsManualMode = false;
        OnPropertyChanged(nameof(HasTreeNodes));
        OnPropertyChanged(nameof(IsJsonResult));
        UpdateTreeExpandState();
    }

    [RelayCommand]
    private void ShowTreeView()
    {
        if (!HasTreeNodes)
        {
            return;
        }

        IsTreeMode = true;
        IsManualMode = false;
        OnPropertyChanged(nameof(IsJsonResult));
    }

    [RelayCommand]
    private void ShowManualView()
    {
        if (!HasLoaded)
        {
            return;
        }

        IsTreeMode = false;
        IsManualMode = true;
        OnPropertyChanged(nameof(IsJsonResult));
    }

    [RelayCommand(CanExecute = nameof(HasExpandableNodes))]
    private void ToggleExpandAll()
    {
        if (TreeNodes.Count == 0)
        {
            UpdateTreeExpandState();
            return;
        }

        var expandAll = !HasAnyExpandedNode;
        SetSubtreeExpanded(TreeNodes, expandAll);
        UpdateTreeExpandState();
    }

    [RelayCommand]
    private void ToggleNodeExpand(ExtractorNode? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        node.IsExpanded = !node.IsExpanded;
        UpdateTreeExpandState();
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
                UpdateTreeExpandState();
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

        var suggestion = _suggester.SuggestForNode(InputText ?? string.Empty, node);
        ApplySuggestionAndTest(suggestion);
    }

    public void UpdateTextSelection(int start, int end, string? selectedTextFromControl)
    {
        var (resolvedStart, resolvedLength, preview) = ResolveSelectionInInputText(start, end, selectedTextFromControl);
        SelectionStart = resolvedStart;
        SelectionEnd = resolvedStart + resolvedLength;
        SelectionPreviewText = preview;
    }

    public void UseSelectionFromView(int start, int end, string? selectedTextFromControl)
    {
        if (start != end || !string.IsNullOrEmpty(selectedTextFromControl))
        {
            UpdateTextSelection(start, end, selectedTextFromControl);
        }

        UseSelection();
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
            InputText ?? string.Empty,
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

    [RelayCommand(CanExecute = nameof(HasSuggestedHtmlPath))]
    private async Task CopyHtmlPathAsync()
    {
        if (string.IsNullOrWhiteSpace(SuggestedHtmlPath) || _clipboardService is null)
        {
            return;
        }

        if (await _clipboardService.SetTextAsync(SuggestedHtmlPath))
        {
            CopyFeedbackText = "Chemin HTMLPath copié dans le presse-papiers.";
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
        return IsChainAvailable && (HasSuggestedJsonPath || HasSuggestedRegex);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSendToChain))]
    private void SendToChain()
    {
        if (_chainViewModel is null || _chainViewModel.Steps.Count == 0 || (!HasSuggestedJsonPath && !HasSuggestedRegex))
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
            IsRequired = true,
            IsList = ExtractAsList
        };

        targetStep.Extractions.Add(extraction);
        _chainViewModel.SelectedStep = targetStep;
        SwitchToChainCallback?.Invoke();
    }

    private void RefreshManualSelectionIfActive()
    {
        if (!_hasCapturedSelection)
        {
            return;
        }

        var suggestion = _suggester.SuggestManualRegex(
            InputText ?? string.Empty,
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
        var jsonPath = suggestion.JsonPath;
        if (ExtractAsList && !string.IsNullOrWhiteSpace(jsonPath))
        {
            jsonPath = ArrayIndexRegex.Replace(jsonPath, "[*]");
        }

        SuggestedJsonPath = jsonPath;
        SuggestedHtmlPath = suggestion.HtmlPath;
        SuggestedRegex = suggestion.Regex;
        OccurrenceCount = suggestion.OccurrenceCount;
        OccurrenceWarningText = ExtractAsList ? null : suggestion.OccurrenceWarning;
        InfoMessage = suggestion.InfoMessage;

        if (!HasSuggestedJsonPath && !HasSuggestedHtmlPath && !HasSuggestedRegex)
        {
            TestStatus = LiveTestStatus.Error;
            TestStatusBadgeText = "Aucune correspondance";
            TestResultText = suggestion.InfoMessage ?? "Aucune expression proposée.";
            RegexTestResultText = null;
            return;
        }

        var sourceText = InputText ?? string.Empty;
        var tempResponse = new ResponseModel { Body = sourceText };

        if (HasSuggestedJsonPath)
        {
            if (ExtractAsList)
            {
                var listOutcome = _responseExtractor.ExtractList(tempResponse, new ChainExtraction
                {
                    VariableName = "preview",
                    Source = ChainExtractionSource.Body,
                    Kind = ExtractionKind.JsonPath,
                    Expression = SuggestedJsonPath!,
                    IsList = true
                });

                if (listOutcome.IsSuccess)
                {
                    TestStatus = LiveTestStatus.Success;
                    TestStatusBadgeText = listOutcome.Values.Count <= 1
                        ? $"{listOutcome.Values.Count} élément"
                        : $"{listOutcome.Values.Count} éléments";
                    var preview = string.Join(" | ", listOutcome.Values.Take(8));
                    TestResultText = listOutcome.Values.Count > 8
                        ? $"{preview} … (+{listOutcome.Values.Count - 8})"
                        : preview;
                }
                else
                {
                    TestStatus = LiveTestStatus.Error;
                    TestStatusBadgeText = "Aucune correspondance";
                    TestResultText = listOutcome.ErrorMessage ?? "Échec d'extraction en liste JSONPath.";
                }

                if (HasSuggestedRegex)
                {
                    var regexListOutcome = _responseExtractor.ExtractList(tempResponse, new ChainExtraction
                    {
                        VariableName = "preview",
                        Source = ChainExtractionSource.Body,
                        Kind = ExtractionKind.Regex,
                        Expression = SuggestedRegex!,
                        RegexGroup = 1,
                        IsList = true
                    });

                    RegexTestResultText = regexListOutcome.IsSuccess
                        ? $"Test regex en liste : {regexListOutcome.Values.Count} élément(s) capturé(s)"
                        : $"Test regex : {regexListOutcome.ErrorMessage}";
                }
                else
                {
                    RegexTestResultText = null;
                }

                return;
            }

            var jsonOutcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
            {
                VariableName = "preview",
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
                    VariableName = "preview",
                    Source = ChainExtractionSource.Body,
                    Kind = ExtractionKind.Regex,
                    Expression = SuggestedRegex!,
                    RegexGroup = 1
                });

                if (regexOutcome.IsSuccess)
                {
                    var count = CountMatchesSafe(sourceText, SuggestedRegex!);
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
        if (!HasSuggestedRegex)
        {
            if (HasSuggestedHtmlPath)
            {
                TestStatus = LiveTestStatus.Success;
                TestStatusBadgeText = "HTMLPath";
                TestResultText = $"Chemin généré : {SuggestedHtmlPath}";
            }
            else
            {
                TestStatus = LiveTestStatus.None;
                TestStatusBadgeText = string.Empty;
                TestResultText = null;
            }
            return;
        }

        if (ExtractAsList)
        {
            var regexListOutcome = _responseExtractor.ExtractList(tempResponse, new ChainExtraction
            {
                VariableName = "preview",
                Source = ChainExtractionSource.Body,
                Kind = ExtractionKind.Regex,
                Expression = SuggestedRegex!,
                RegexGroup = 1,
                IsList = true
            });

            if (regexListOutcome.IsSuccess)
            {
                TestStatus = LiveTestStatus.Success;
                TestStatusBadgeText = regexListOutcome.Values.Count <= 1
                    ? $"{regexListOutcome.Values.Count} élément"
                    : $"{regexListOutcome.Values.Count} éléments";
                var preview = string.Join(" | ", regexListOutcome.Values.Take(8));
                TestResultText = regexListOutcome.Values.Count > 8
                    ? $"{preview} … (+{regexListOutcome.Values.Count - 8})"
                    : preview;
            }
            else
            {
                TestStatus = LiveTestStatus.Error;
                TestStatusBadgeText = "Aucune correspondance";
                TestResultText = regexListOutcome.ErrorMessage ?? "Aucune correspondance trouvée.";
            }

            return;
        }

        var outcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
        {
            VariableName = "preview",
            Source = ChainExtractionSource.Body,
            Kind = ExtractionKind.Regex,
            Expression = SuggestedRegex!,
            RegexGroup = 1
        });

        if (outcome.IsSuccess)
        {
            var matchCount = CountMatchesSafe(sourceText, SuggestedRegex!);
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
        SuggestedHtmlPath = null;
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

    private (int Start, int Length, string? Preview) ResolveSelectionInInputText(
        int rawStart,
        int rawEnd,
        string? controlSelectedText)
    {
        var input = InputText ?? string.Empty;
        if (string.IsNullOrEmpty(input))
        {
            return (0, 0, null);
        }

        var minPos = Math.Clamp(Math.Min(rawStart, rawEnd), 0, input.Length);
        var maxPos = Math.Clamp(Math.Max(rawStart, rawEnd), 0, input.Length);
        var rawLength = maxPos - minPos;

        if (!string.IsNullOrEmpty(controlSelectedText))
        {
            if (minPos + controlSelectedText.Length <= input.Length &&
                string.Equals(input.Substring(minPos, controlSelectedText.Length), controlSelectedText, StringComparison.Ordinal))
            {
                return (minPos, controlSelectedText.Length, FormatSelectionPreview(controlSelectedText));
            }

            var candidates = new List<string> { controlSelectedText };
            if (controlSelectedText.Contains('\n') && !controlSelectedText.Contains("\r\n", StringComparison.Ordinal))
            {
                candidates.Add(controlSelectedText.Replace("\n", "\r\n", StringComparison.Ordinal));
            }

            foreach (var candidate in candidates)
            {
                var bestIdx = -1;
                var bestDist = int.MaxValue;
                var searchStart = Math.Max(0, minPos - 256);
                var idx = input.IndexOf(candidate, searchStart, StringComparison.Ordinal);
                while (idx >= 0)
                {
                    var dist = Math.Abs(idx - minPos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = idx;
                    }

                    if (idx > minPos + 4096)
                    {
                        break;
                    }

                    idx = input.IndexOf(candidate, idx + 1, StringComparison.Ordinal);
                }

                if (bestIdx < 0 && searchStart > 0)
                {
                    bestIdx = input.IndexOf(candidate, StringComparison.Ordinal);
                }

                if (bestIdx >= 0)
                {
                    return (bestIdx, candidate.Length, FormatSelectionPreview(candidate));
                }
            }
        }

        if (rawLength <= 0)
        {
            return (minPos, 0, null);
        }

        var slice = input.Substring(minPos, rawLength);
        return (minPos, rawLength, FormatSelectionPreview(slice));
    }

    private static string FormatSelectionPreview(string selected)
    {
        var clean = selected.Replace("\r\n", " ", StringComparison.Ordinal)
                            .Replace('\n', ' ')
                            .Replace('\r', ' ');
        var shortText = clean.Length <= 24 ? clean : $"{clean[..23]}…";
        return $"Sélection : « {shortText} » ({selected.Length} car.)";
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

    private void UpdateTreeExpandState()
    {
        var hasExpandable = false;
        var hasVisibleExpanded = false;

        foreach (var node in TreeNodes)
        {
            if (node.HasChildren)
            {
                hasExpandable = true;
                if (node.IsExpanded)
                {
                    hasVisibleExpanded = true;
                    break;
                }
            }
        }

        HasExpandableNodes = hasExpandable;
        HasAnyExpandedNode = hasVisibleExpanded;
    }

    private static void SetSubtreeExpanded(IEnumerable<ExtractorNode> roots, bool isExpanded)
    {
        var stack = new Stack<ExtractorNode>();
        foreach (var root in roots)
        {
            stack.Push(root);
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!current.HasChildren)
            {
                continue;
            }

            current.IsExpanded = isExpanded;
            foreach (var child in current.Children)
            {
                if (child.HasChildren)
                {
                    stack.Push(child);
                }
            }
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
