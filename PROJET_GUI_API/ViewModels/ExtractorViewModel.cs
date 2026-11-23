using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class ExtractorViewModel : ViewModelBase
{
    public const int LargeWrapThresholdChars = 60_000;
    public const int LargeInputCollapseThresholdChars = 150_000;
    private const int MaxTreeSearchMatches = 5_000;
    private const int TreeSearchDebounceMs = 130;
    private const int MaxTrackedRegexOccurrences = 100;
    private const int IncrementalTreeMutationThreshold = 250;
    private const int MaxResultPreviewChars = 1_500;
    private const int MaxResultPreviewLines = 6;
    private const int MaxInlineRegexSnippetChars = 240;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly CultureInfo FrenchCulture = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Regex ArrayIndexRegex = new(@"\[\s*-?\d+\s*\]", RegexOptions.Compiled);

    private readonly IExtractionSuggester _suggester;
    private readonly IResponseExtractor _responseExtractor;
    private readonly IClipboardService? _clipboardService;
    private readonly IOverlayEditorService? _overlayEditor;
    private readonly ChainViewModel? _chainViewModel;
    private readonly ObservableCollection<ChainStep> _emptyChainSteps = [];

    private readonly List<ExtractorNode> _treeSearchMatches = [];
    private readonly HashSet<ExtractorNode> _treeFilteredVisibleSet = [];
    private readonly List<ExtractorNode> _highlightedTreeNodes = [];

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _treeSearchCts;
    private CancellationTokenSource? _suggestionCts;
    private ExtractorNode? _activeTreeSearchNode;
    private string? _fullTestResultText;

    private bool _hasCapturedSelection;
    private int _capturedStart;
    private int _capturedLength;
    private bool _suppressAutoLoad;

    private sealed record SuggestionEvaluationResult(
        string? JsonPath,
        string? HtmlPath,
        string? Regex,
        int OccurrenceCount,
        string? OccurrenceWarning,
        string? InfoMessage,
        LiveTestStatus TestStatus,
        string TestStatusBadgeText,
        string? TestResultText,
        string? RegexTestResultText);

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

    public Action<ExtractorNode>? ScrollToTreeNodeCallback { get; set; }

    public ObservableCollection<ExtractorNode> TreeNodes { get; } = [];

    public BulkObservableCollection<ExtractorNode> VisibleTreeNodes { get; } = [];

    public ObservableCollection<ChainStep> ChainSteps => _chainViewModel?.Steps ?? _emptyChainSteps;

    public bool IsChainAvailable => _chainViewModel is not null && _chainViewModel.Steps.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputCharCountText))]
    [NotifyPropertyChangedFor(nameof(IsLargeInput))]
    [NotifyPropertyChangedFor(nameof(ShouldWrapInputText))]
    [NotifyPropertyChangedFor(nameof(ShowTopInputEditor))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedInputBanner))]
    [NotifyPropertyChangedFor(nameof(ShowCollapseTopEditorButton))]
    [NotifyPropertyChangedFor(nameof(CollapsedInputSummary))]
    [NotifyPropertyChangedFor(nameof(ManualCodeText))]
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

    public bool IsLargeInput => (InputText?.Length ?? 0) > LargeInputCollapseThresholdChars;

    public bool ShouldWrapInputText => (InputText?.Length ?? 0) <= LargeWrapThresholdChars;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTopInputEditor))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedInputBanner))]
    [NotifyPropertyChangedFor(nameof(ShowCollapseTopEditorButton))]
    public partial bool IsTopInputExpanded { get; set; } = true;

    public bool ShowTopInputEditor => (!HasLoaded && !IsAnalyzing) || !IsLargeInput || IsTopInputExpanded;

    public bool ShowCollapsedInputBanner => (HasLoaded || IsAnalyzing) && IsLargeInput && !IsTopInputExpanded;

    public bool ShowCollapseTopEditorButton => (HasLoaded || IsAnalyzing) && IsLargeInput && IsTopInputExpanded;

    public string CollapsedInputSummary =>
        $"Réponse volumineuse en mémoire ({InputCharCountText}) — aperçu supérieur masqué pour préserver la fluidité.";

    [ObservableProperty]
    public partial bool WrapManualCodeText { get; set; } = true;

    public string ManualCodeText => IsManualMode ? (InputText ?? string.Empty) : string.Empty;

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
    [NotifyPropertyChangedFor(nameof(ShowEmptyPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowTopInputEditor))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedInputBanner))]
    [NotifyPropertyChangedFor(nameof(ShowCollapseTopEditorButton))]
    public partial bool HasLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowTopInputEditor))]
    [NotifyPropertyChangedFor(nameof(ShowCollapsedInputBanner))]
    [NotifyPropertyChangedFor(nameof(ShowCollapseTopEditorButton))]
    public partial bool IsAnalyzing { get; set; }

    public bool ShowEmptyPrompt => !HasLoaded && !IsAnalyzing;

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
    [NotifyPropertyChangedFor(nameof(ManualCodeText))]
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
    [NotifyPropertyChangedFor(nameof(HasTreeSearchQuery))]
    [NotifyPropertyChangedFor(nameof(HasTreeSearchResultsBanner))]
    [NotifyPropertyChangedFor(nameof(ShowNoTreeSearchMatchesMessage))]
    [NotifyCanExecuteChangedFor(nameof(ClearTreeSearchCommand))]
    public partial string TreeSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoTreeSearchMatchesMessage))]
    public partial bool FilterTreeBySearch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigateTreeSearch))]
    [NotifyPropertyChangedFor(nameof(ShowNoTreeSearchMatchesMessage))]
    [NotifyCanExecuteChangedFor(nameof(NavigateNextTreeMatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(NavigatePrevTreeMatchCommand))]
    public partial int TreeSearchMatchCount { get; set; }

    [ObservableProperty]
    public partial int ActiveTreeSearchMatchIndex { get; set; } = -1;

    [ObservableProperty]
    public partial bool HasMoreTreeSearchMatches { get; set; }

    [ObservableProperty]
    public partial string TreeSearchCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTreeSearchEmpty { get; set; }

    public bool HasTreeSearchQuery => !string.IsNullOrEmpty(TreeSearchText);

    public bool HasTreeSearchResultsBanner => !string.IsNullOrEmpty(TreeSearchText);

    public bool CanNavigateTreeSearch => TreeSearchMatchCount > 0;

    private bool IsTreeFilterActive => FilterTreeBySearch && !string.IsNullOrEmpty(TreeSearchText);

    public bool ShowNoTreeSearchMatchesMessage =>
        IsTreeMode &&
        IsTreeFilterActive &&
        VisibleTreeNodes.Count == 0;

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
    [NotifyPropertyChangedFor(nameof(CanEnlargeTestResult))]
    [NotifyCanExecuteChangedFor(nameof(EnlargeTestResultCommand))]
    public partial string? TestResultText { get; set; }

    public bool HasTestResult => !string.IsNullOrWhiteSpace(TestResultText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnlargeTestResult))]
    [NotifyCanExecuteChangedFor(nameof(EnlargeTestResultCommand))]
    public partial bool IsTestResultTruncated { get; set; }

    public bool CanEnlargeTestResult =>
        HasTestResult &&
        _overlayEditor is not null &&
        (IsTestResultTruncated ||
         (_fullTestResultText?.Length ?? 0) > 120 ||
         (_fullTestResultText?.IndexOf('\n') ?? -1) >= 0);

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
        var safeText = text ?? string.Empty;
        _suppressAutoLoad = true;
        try
        {
            // Masquer l'éditeur supérieur avant d'affecter InputText sur les très gros payloads
            // afin d'éviter une mise en page synchrone inutile sur InputResponseTextBox.
            IsAnalyzing = true;
            IsTopInputExpanded = safeText.Length <= LargeInputCollapseThresholdChars;
            InputText = safeText;
            Format = DeduceFormatFromContentType(contentType);
        }
        finally
        {
            _suppressAutoLoad = false;
        }

        _ = LoadAsync();
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
            _ = LoadAsync();
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
            _ = RunNodeSuggestionAsync(SelectedNode);
        }
        else if (IsManualMode && _hasCapturedSelection)
        {
            RefreshManualSelectionIfActive();
        }
    }

    partial void OnTreeSearchTextChanged(string value)
    {
        _ = RunTreeSearchAsync(immediate: string.IsNullOrEmpty(value));
    }

    partial void OnFilterTreeBySearchChanged(bool value)
    {
        if (!string.IsNullOrEmpty(TreeSearchText))
        {
            _ = RunTreeSearchAsync(immediate: true);
        }
        else
        {
            _treeFilteredVisibleSet.Clear();
            RebuildVisibleTreeNodes();
            OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
        }
    }

    [RelayCommand]
    private void ExpandTopInputEditor()
    {
        IsTopInputExpanded = true;
    }

    [RelayCommand]
    private void CollapseTopInputEditor()
    {
        if (IsLargeInput && (HasLoaded || IsAnalyzing))
        {
            IsTopInputExpanded = false;
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
                var safeText = text ?? string.Empty;
                IsTopInputExpanded = safeText.Length <= LargeInputCollapseThresholdChars;
                InputText = safeText;
                _ = LoadAsync();
            }));
    }

    [RelayCommand(CanExecute = nameof(CanEnlargeTestResult))]
    private void EnlargeTestResult()
    {
        if (string.IsNullOrEmpty(_fullTestResultText))
        {
            return;
        }

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            "Résultat complet du test",
            _fullTestResultText,
            IsReadOnly: true,
            OnSave: null));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task LoadAsync()
    {
        CancelPendingLoad();
        CancelPendingTreeSearch();
        CancelPendingSuggestion();

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        ResetSuggestions();
        ClearTreeSearchState(resetQuery: false);
        TreeNodes.Clear();
        VisibleTreeNodes.Clear();
        _hasCapturedSelection = false;
        SelectionStart = 0;
        SelectionEnd = 0;
        SelectionPreviewText = null;
        SelectedNode = null;
        OnPropertyChanged(nameof(HasTreeNodes));
        OnPropertyChanged(nameof(IsJsonResult));
        OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
        UpdateTreeExpandState();

        var sourceText = InputText ?? string.Empty;
        var requestedFormat = Format;

        IsTopInputExpanded = sourceText.Length <= LargeInputCollapseThresholdChars;
        WrapManualCodeText = sourceText.Length <= LargeWrapThresholdChars;
        IsAnalyzing = true;
        DetectedFormatText = "Analyse en cours...";

        try
        {
            var analysis = await Task.Run(() => _suggester.Analyze(sourceText, requestedFormat, ct), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            {
                return;
            }

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

            if (!string.IsNullOrEmpty(TreeSearchText))
            {
                await RunTreeSearchAsync(immediate: true);
            }
            else
            {
                RebuildVisibleTreeNodes();
                UpdateTreeExpandState();
            }
        }
        catch (OperationCanceledException)
        {
            // Chargement remplacé ou annulé : rien à faire.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                HasLoaded = true;
                DetectedFormat = ResponseFormat.Text;
                DetectedFormatText = "Erreur d'analyse";
                AnalysisMessage = $"Impossible d'analyser la réponse ({ex.Message}) : bascule en mode texte.";
                IsTreeMode = false;
                IsManualMode = true;
                OnPropertyChanged(nameof(HasTreeNodes));
                OnPropertyChanged(nameof(IsJsonResult));
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsAnalyzing = false;
            }
        }
    }

    [RelayCommand]
    private void CancelLoad()
    {
        CancelPendingLoad();
        IsAnalyzing = false;
        DetectedFormatText = HasLoaded
            ? $"Détecté : {FormatToDisplayLabel(DetectedFormat)}"
            : "Analyse annulée";
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
        OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
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
        OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
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
        RebuildVisibleTreeNodes();
        UpdateTreeExpandState();

        if (expandAll && _activeTreeSearchNode is not null)
        {
            ScrollToTreeNodeCallback?.Invoke(_activeTreeSearchNode);
        }
    }

    [RelayCommand]
    private void ToggleNodeExpand(ExtractorNode? node)
    {
        if (node is null || !node.HasChildren)
        {
            return;
        }

        node.IsExpanded = !node.IsExpanded;

        if (IsTreeFilterActive)
        {
            RebuildVisibleTreeNodes();
            UpdateTreeExpandState();
            return;
        }

        var index = VisibleTreeNodes.IndexOf(node);
        if (index < 0)
        {
            RebuildVisibleTreeNodes();
            UpdateTreeExpandState();
            return;
        }

        if (node.IsExpanded)
        {
            var descendants = CollectVisibleDescendants(node);
            if (descendants.Count <= IncrementalTreeMutationThreshold)
            {
                for (var i = 0; i < descendants.Count; i++)
                {
                    VisibleTreeNodes.Insert(index + 1 + i, descendants[i]);
                }
            }
            else
            {
                RebuildVisibleTreeNodes();
                ScrollToTreeNodeCallback?.Invoke(node);
            }
        }
        else
        {
            var removeCount = CountVisibleDescendantsInFlatList(index, node.Depth);
            if (removeCount <= IncrementalTreeMutationThreshold)
            {
                for (var i = 0; i < removeCount; i++)
                {
                    VisibleTreeNodes.RemoveAt(index + 1);
                }
            }
            else
            {
                RebuildVisibleTreeNodes();
                ScrollToTreeNodeCallback?.Invoke(node);
            }
        }

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
                ToggleNodeExpand(node);
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

        _ = RunNodeSuggestionAsync(node);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateTreeSearch))]
    private void NavigateNextTreeMatch()
    {
        NavigateTreeMatch(1);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateTreeSearch))]
    private void NavigatePrevTreeMatch()
    {
        NavigateTreeMatch(-1);
    }

    [RelayCommand(CanExecute = nameof(HasTreeSearchQuery))]
    private void ClearTreeSearch()
    {
        TreeSearchText = string.Empty;
    }

    private void NavigateTreeMatch(int delta)
    {
        if (_treeSearchMatches.Count == 0)
        {
            return;
        }

        if (ActiveTreeSearchMatchIndex < 0 || ActiveTreeSearchMatchIndex >= _treeSearchMatches.Count)
        {
            ActiveTreeSearchMatchIndex = 0;
        }
        else
        {
            ActiveTreeSearchMatchIndex =
                (ActiveTreeSearchMatchIndex + delta + _treeSearchMatches.Count) % _treeSearchMatches.Count;
        }

        ActivateTreeMatchAtIndex(ActiveTreeSearchMatchIndex);
    }

    private void ActivateTreeMatchAtIndex(int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= _treeSearchMatches.Count)
        {
            return;
        }

        if (_activeTreeSearchNode is not null)
        {
            _activeTreeSearchNode.IsActiveSearchMatch = false;
        }

        var target = _treeSearchMatches[matchIndex];
        _activeTreeSearchNode = target;
        target.IsActiveSearchMatch = true;

        TreeSearchCountText = $"{matchIndex + 1}/{_treeSearchMatches.Count}{(HasMoreTreeSearchMatches ? "+" : string.Empty)}";

        var expandedAny = ExpandAncestors(target);
        if (expandedAny || !VisibleTreeNodes.Contains(target))
        {
            RebuildVisibleTreeNodes();
            UpdateTreeExpandState();
        }

        ScrollToTreeNodeCallback?.Invoke(target);
    }

    private async Task RunTreeSearchAsync(bool immediate)
    {
        CancelPendingTreeSearch();

        var query = TreeSearchText;
        if (string.IsNullOrEmpty(query) || TreeNodes.Count == 0)
        {
            ClearTreeSearchState(resetQuery: false);
            RebuildVisibleTreeNodes();
            UpdateTreeExpandState();
            OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
            return;
        }

        var cts = new CancellationTokenSource();
        _treeSearchCts = cts;
        var ct = cts.Token;

        try
        {
            if (!immediate)
            {
                await Task.Delay(TreeSearchDebounceMs, ct);
            }

            var rootsSnapshot = TreeNodes.ToList();
            var filterMode = FilterTreeBySearch;

            var (matches, filteredSet, hasMore) = await Task.Run(() =>
            {
                var foundMatches = new List<ExtractorNode>();
                var visibleSet = filterMode ? new HashSet<ExtractorNode>() : null;
                var more = false;
                var visited = 0;

                var stack = new Stack<ExtractorNode>();
                for (var i = rootsSnapshot.Count - 1; i >= 0; i--)
                {
                    stack.Push(rootsSnapshot[i]);
                }

                while (stack.Count > 0)
                {
                    if ((visited++ & 127) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var current = stack.Pop();
                    if (NodeMatchesQuery(current, query))
                    {
                        if (foundMatches.Count < MaxTreeSearchMatches)
                        {
                            foundMatches.Add(current);
                        }
                        else
                        {
                            more = true;
                        }

                        if (visibleSet is not null)
                        {
                            var ancestor = current;
                            while (ancestor is not null && visibleSet.Add(ancestor))
                            {
                                ancestor = ancestor.Parent;
                            }
                        }
                    }

                    var children = current.Children;
                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(children[i]);
                    }
                }

                return (foundMatches, visibleSet, more);
            }, ct);

            if (ct.IsCancellationRequested || !ReferenceEquals(_treeSearchCts, cts))
            {
                return;
            }

            ClearTreeSearchHighlights();
            _treeSearchMatches.Clear();
            _treeSearchMatches.AddRange(matches);

            _treeFilteredVisibleSet.Clear();
            if (filteredSet is not null)
            {
                foreach (var n in filteredSet)
                {
                    _treeFilteredVisibleSet.Add(n);
                }
            }

            foreach (var match in matches)
            {
                match.IsSearchMatch = true;
                _highlightedTreeNodes.Add(match);
            }

            HasMoreTreeSearchMatches = hasMore;
            TreeSearchMatchCount = matches.Count;

            if (matches.Count == 0)
            {
                ActiveTreeSearchMatchIndex = -1;
                _activeTreeSearchNode = null;
                TreeSearchCountText = "0/0";
                IsTreeSearchEmpty = true;
                RebuildVisibleTreeNodes();
                UpdateTreeExpandState();
                OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
                return;
            }

            IsTreeSearchEmpty = false;
            ActiveTreeSearchMatchIndex = 0;

            if (filterMode)
            {
                // En mode filtré, déplier les ancêtres de chaque branche retenue pour révéler les résultats
                foreach (var node in _treeFilteredVisibleSet)
                {
                    if (node.HasChildren && !ReferenceEquals(node, matches[0]))
                    {
                        var hasVisibleChild = false;
                        foreach (var child in node.Children)
                        {
                            if (_treeFilteredVisibleSet.Contains(child))
                            {
                                hasVisibleChild = true;
                                break;
                            }
                        }

                        if (hasVisibleChild)
                        {
                            node.IsExpanded = true;
                        }
                    }
                }
            }

            var firstMatch = matches[0];
            _activeTreeSearchNode = firstMatch;
            firstMatch.IsActiveSearchMatch = true;
            ExpandAncestors(firstMatch);

            TreeSearchCountText = $"1/{matches.Count}{(hasMore ? "+" : string.Empty)}";
            RebuildVisibleTreeNodes();
            UpdateTreeExpandState();
            OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));

            ScrollToTreeNodeCallback?.Invoke(firstMatch);
        }
        catch (OperationCanceledException)
        {
            // Recherche annulée ou remplacée par la frappe suivante.
        }
    }

    private static bool NodeMatchesQuery(ExtractorNode node, string query)
    {
        return (!string.IsNullOrEmpty(node.Label) && node.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(node.JsonPath) && node.JsonPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(node.HtmlPath) && node.HtmlPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(node.RawScalarValue) && node.RawScalarValue.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(node.AnchorName) && node.AnchorName.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(node.AttributeName) && node.AttributeName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ExpandAncestors(ExtractorNode node)
    {
        var changed = false;
        var parent = node.Parent;
        while (parent is not null)
        {
            if (!parent.IsExpanded)
            {
                parent.IsExpanded = true;
                changed = true;
            }
            parent = parent.Parent;
        }
        return changed;
    }

    private void ClearTreeSearchHighlights()
    {
        if (_activeTreeSearchNode is not null)
        {
            _activeTreeSearchNode.IsActiveSearchMatch = false;
            _activeTreeSearchNode = null;
        }

        foreach (var node in _highlightedTreeNodes)
        {
            node.IsSearchMatch = false;
            node.IsActiveSearchMatch = false;
        }
        _highlightedTreeNodes.Clear();
    }

    private void ClearTreeSearchState(bool resetQuery)
    {
        ClearTreeSearchHighlights();
        _treeSearchMatches.Clear();
        _treeFilteredVisibleSet.Clear();
        TreeSearchMatchCount = 0;
        ActiveTreeSearchMatchIndex = -1;
        HasMoreTreeSearchMatches = false;
        TreeSearchCountText = string.Empty;
        IsTreeSearchEmpty = false;
        if (resetQuery && !string.IsNullOrEmpty(TreeSearchText))
        {
            TreeSearchText = string.Empty;
        }
    }

    private void RebuildVisibleTreeNodes()
    {
        if (TreeNodes.Count == 0)
        {
            if (VisibleTreeNodes.Count > 0)
            {
                VisibleTreeNodes.Clear();
            }
            OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
            return;
        }

        var filterActive = IsTreeFilterActive;
        var flat = new List<ExtractorNode>(Math.Max(64, VisibleTreeNodes.Count));
        var stack = new Stack<ExtractorNode>();

        for (var i = TreeNodes.Count - 1; i >= 0; i--)
        {
            var root = TreeNodes[i];
            if (!filterActive || _treeFilteredVisibleSet.Contains(root))
            {
                stack.Push(root);
            }
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            flat.Add(current);

            if (current.IsExpanded && current.Children.Count > 0)
            {
                for (var i = current.Children.Count - 1; i >= 0; i--)
                {
                    var child = current.Children[i];
                    if (!filterActive || _treeFilteredVisibleSet.Contains(child))
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        VisibleTreeNodes.ReplaceAll(flat);
        OnPropertyChanged(nameof(ShowNoTreeSearchMatchesMessage));
    }

    private static List<ExtractorNode> CollectVisibleDescendants(ExtractorNode parent)
    {
        var result = new List<ExtractorNode>();
        var stack = new Stack<ExtractorNode>();

        for (var i = parent.Children.Count - 1; i >= 0; i--)
        {
            stack.Push(parent.Children[i]);
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            result.Add(current);
            if (current.IsExpanded && current.Children.Count > 0)
            {
                for (var i = current.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(current.Children[i]);
                }
            }
        }

        return result;
    }

    private int CountVisibleDescendantsInFlatList(int parentIndex, int parentDepth)
    {
        var count = 0;
        for (var i = parentIndex + 1; i < VisibleTreeNodes.Count; i++)
        {
            if (VisibleTreeNodes[i].Depth <= parentDepth)
            {
                break;
            }
            count++;
        }
        return count;
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
            CancelPendingSuggestion();
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

        _ = RunManualSuggestionAsync(_capturedStart, _capturedLength);
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

        _ = RunManualSuggestionAsync(_capturedStart, _capturedLength);
    }

    private async Task RunNodeSuggestionAsync(ExtractorNode node)
    {
        CancelPendingSuggestion();
        var cts = new CancellationTokenSource();
        _suggestionCts = cts;
        var ct = cts.Token;

        var sourceText = InputText ?? string.Empty;
        var extractAsList = ExtractAsList;

        try
        {
            var eval = await Task.Run(() =>
            {
                var suggestion = _suggester.SuggestForNode(sourceText, node, ct);
                return EvaluateSuggestionCore(suggestion, sourceText, extractAsList, ct);
            }, ct);

            if (ct.IsCancellationRequested || !ReferenceEquals(_suggestionCts, cts))
            {
                return;
            }

            ApplyEvaluationResult(eval);
        }
        catch (OperationCanceledException)
        {
            // Suggestion annulée par un nouveau clic ou chargement.
        }
    }

    private async Task RunManualSuggestionAsync(int start, int length)
    {
        CancelPendingSuggestion();
        var cts = new CancellationTokenSource();
        _suggestionCts = cts;
        var ct = cts.Token;

        var sourceText = InputText ?? string.Empty;
        var contextLength = ContextLength;
        var valueKind = ValueKind;
        var customPattern = CustomPattern;
        var useTrailingAnchor = UseTrailingAnchor;
        var extractAsList = ExtractAsList;

        try
        {
            var eval = await Task.Run(() =>
            {
                var suggestion = _suggester.SuggestManualRegex(
                    sourceText,
                    start,
                    length,
                    contextLength,
                    valueKind,
                    customPattern,
                    useTrailingAnchor,
                    ct);
                return EvaluateSuggestionCore(suggestion, sourceText, extractAsList, ct);
            }, ct);

            if (ct.IsCancellationRequested || !ReferenceEquals(_suggestionCts, cts))
            {
                return;
            }

            ApplyEvaluationResult(eval);
        }
        catch (OperationCanceledException)
        {
            // Suggestion annulée par une nouvelle sélection.
        }
    }

    private SuggestionEvaluationResult EvaluateSuggestionCore(
        ExtractionSuggestion suggestion,
        string sourceText,
        bool extractAsList,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var jsonPath = suggestion.JsonPath;
        if (extractAsList && !string.IsNullOrWhiteSpace(jsonPath))
        {
            jsonPath = ArrayIndexRegex.Replace(jsonPath, "[*]");
        }

        var htmlPath = suggestion.HtmlPath;
        var regex = suggestion.Regex;
        var hasJsonPath = !string.IsNullOrWhiteSpace(jsonPath);
        var hasHtmlPath = !string.IsNullOrWhiteSpace(htmlPath);
        var hasRegex = !string.IsNullOrWhiteSpace(regex);
        var warning = extractAsList ? null : suggestion.OccurrenceWarning;

        if (!hasJsonPath && !hasHtmlPath && !hasRegex)
        {
            return new SuggestionEvaluationResult(
                jsonPath,
                htmlPath,
                regex,
                suggestion.OccurrenceCount,
                warning,
                suggestion.InfoMessage,
                LiveTestStatus.Error,
                "Aucune correspondance",
                suggestion.InfoMessage ?? "Aucune expression proposée.",
                null);
        }

        var tempResponse = new ResponseModel { Body = sourceText };

        if (hasJsonPath)
        {
            if (extractAsList)
            {
                var listOutcome = _responseExtractor.ExtractList(tempResponse, new ChainExtraction
                {
                    VariableName = "preview",
                    Source = ChainExtractionSource.Body,
                    Kind = ExtractionKind.JsonPath,
                    Expression = jsonPath!,
                    IsList = true
                });

                ct.ThrowIfCancellationRequested();

                LiveTestStatus listStatus;
                string listBadge;
                string? listResult;
                if (listOutcome.IsSuccess)
                {
                    listStatus = LiveTestStatus.Success;
                    listBadge = listOutcome.Values.Count <= 1
                        ? $"{listOutcome.Values.Count} élément"
                        : $"{listOutcome.Values.Count} éléments";
                    var preview = string.Join(" | ", listOutcome.Values.Take(8));
                    listResult = listOutcome.Values.Count > 8
                        ? $"{preview} … (+{listOutcome.Values.Count - 8})"
                        : preview;
                }
                else
                {
                    listStatus = LiveTestStatus.Error;
                    listBadge = "Aucune correspondance";
                    listResult = listOutcome.ErrorMessage ?? "Échec d'extraction en liste JSONPath.";
                }

                string? regexListResult = null;
                if (hasRegex)
                {
                    var regexListOutcome = _responseExtractor.ExtractList(tempResponse, new ChainExtraction
                    {
                        VariableName = "preview",
                        Source = ChainExtractionSource.Body,
                        Kind = ExtractionKind.Regex,
                        Expression = regex!,
                        RegexGroup = 1,
                        IsList = true
                    });

                    regexListResult = regexListOutcome.IsSuccess
                        ? $"Test regex en liste : {regexListOutcome.Values.Count} élément(s) capturé(s)"
                        : $"Test regex : {regexListOutcome.ErrorMessage}";
                }

                return new SuggestionEvaluationResult(
                    jsonPath,
                    htmlPath,
                    regex,
                    suggestion.OccurrenceCount,
                    warning,
                    suggestion.InfoMessage,
                    listStatus,
                    listBadge,
                    listResult,
                    regexListResult);
            }

            var jsonOutcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
            {
                VariableName = "preview",
                Source = ChainExtractionSource.Body,
                Kind = ExtractionKind.JsonPath,
                Expression = jsonPath!
            });

            ct.ThrowIfCancellationRequested();

            LiveTestStatus status;
            string badge;
            string? resultText;
            if (jsonOutcome.IsSuccess)
            {
                status = LiveTestStatus.Success;
                badge = "1 correspondance";
                resultText = jsonOutcome.Value;
            }
            else
            {
                status = LiveTestStatus.Error;
                badge = "Aucune correspondance";
                resultText = jsonOutcome.ErrorMessage ?? "Échec d'extraction JSONPath.";
            }

            string? regexResult = null;
            if (hasRegex)
            {
                var regexOutcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
                {
                    VariableName = "preview",
                    Source = ChainExtractionSource.Body,
                    Kind = ExtractionKind.Regex,
                    Expression = regex!,
                    RegexGroup = 1
                });

                if (regexOutcome.IsSuccess)
                {
                    var (count, hasMore) = CountMatchesBounded(sourceText, regex!, ct);
                    var countLabel = hasMore ? $"{count}+" : count.ToString();
                    var snippet = TruncateInlineSnippet(regexOutcome.Value, MaxInlineRegexSnippetChars);
                    regexResult = count > 1
                        ? $"Test regex : {countLabel} correspondances (1re capturée : « {snippet} »)"
                        : $"Test regex : valeur capturée « {snippet} »";
                }
                else
                {
                    regexResult = $"Test regex : {regexOutcome.ErrorMessage}";
                }
            }

            return new SuggestionEvaluationResult(
                jsonPath,
                htmlPath,
                regex,
                suggestion.OccurrenceCount,
                warning,
                suggestion.InfoMessage,
                status,
                badge,
                resultText,
                regexResult);
        }

        // Mode Regex principal (XML / HTML / Texte)
        if (!hasRegex)
        {
            if (hasHtmlPath)
            {
                return new SuggestionEvaluationResult(
                    jsonPath,
                    htmlPath,
                    regex,
                    suggestion.OccurrenceCount,
                    warning,
                    suggestion.InfoMessage,
                    LiveTestStatus.Success,
                    "HTMLPath",
                    $"Chemin généré : {htmlPath}",
                    null);
            }

            return new SuggestionEvaluationResult(
                jsonPath,
                htmlPath,
                regex,
                suggestion.OccurrenceCount,
                warning,
                suggestion.InfoMessage,
                LiveTestStatus.None,
                string.Empty,
                null,
                null);
        }

        if (extractAsList)
        {
            var regexListOutcome = _responseExtractor.ExtractList(tempResponse, new ChainExtraction
            {
                VariableName = "preview",
                Source = ChainExtractionSource.Body,
                Kind = ExtractionKind.Regex,
                Expression = regex!,
                RegexGroup = 1,
                IsList = true
            });

            ct.ThrowIfCancellationRequested();

            if (regexListOutcome.IsSuccess)
            {
                var preview = string.Join(" | ", regexListOutcome.Values.Take(8));
                return new SuggestionEvaluationResult(
                    jsonPath,
                    htmlPath,
                    regex,
                    suggestion.OccurrenceCount,
                    warning,
                    suggestion.InfoMessage,
                    LiveTestStatus.Success,
                    regexListOutcome.Values.Count <= 1
                        ? $"{regexListOutcome.Values.Count} élément"
                        : $"{regexListOutcome.Values.Count} éléments",
                    regexListOutcome.Values.Count > 8
                        ? $"{preview} … (+{regexListOutcome.Values.Count - 8})"
                        : preview,
                    null);
            }

            return new SuggestionEvaluationResult(
                jsonPath,
                htmlPath,
                regex,
                suggestion.OccurrenceCount,
                warning,
                suggestion.InfoMessage,
                LiveTestStatus.Error,
                "Aucune correspondance",
                regexListOutcome.ErrorMessage ?? "Aucune correspondance trouvée.",
                null);
        }

        var outcome = _responseExtractor.Extract(tempResponse, new ChainExtraction
        {
            VariableName = "preview",
            Source = ChainExtractionSource.Body,
            Kind = ExtractionKind.Regex,
            Expression = regex!,
            RegexGroup = 1
        });

        ct.ThrowIfCancellationRequested();

        if (outcome.IsSuccess)
        {
            var (matchCount, hasMoreMatches) = CountMatchesBounded(sourceText, regex!, ct);
            if (matchCount > 1)
            {
                var countLabel = hasMoreMatches ? $"{matchCount}+" : matchCount.ToString();
                return new SuggestionEvaluationResult(
                    jsonPath,
                    htmlPath,
                    regex,
                    suggestion.OccurrenceCount,
                    warning,
                    suggestion.InfoMessage,
                    LiveTestStatus.Warning,
                    $"{countLabel} correspondances",
                    $"{countLabel} correspondances trouvées (1re capturée : {outcome.Value})",
                    null);
            }

            return new SuggestionEvaluationResult(
                jsonPath,
                htmlPath,
                regex,
                suggestion.OccurrenceCount,
                warning,
                suggestion.InfoMessage,
                LiveTestStatus.Success,
                "1 correspondance",
                outcome.Value,
                null);
        }

        return new SuggestionEvaluationResult(
            jsonPath,
            htmlPath,
            regex,
            suggestion.OccurrenceCount,
            warning,
            suggestion.InfoMessage,
            LiveTestStatus.Error,
            "Aucune correspondance",
            outcome.ErrorMessage ?? "Aucune correspondance trouvée.",
            null);
    }

    private void ApplyEvaluationResult(SuggestionEvaluationResult eval)
    {
        SuggestedJsonPath = eval.JsonPath;
        SuggestedHtmlPath = eval.HtmlPath;
        SuggestedRegex = eval.Regex;
        OccurrenceCount = eval.OccurrenceCount;
        OccurrenceWarningText = eval.OccurrenceWarning;
        InfoMessage = eval.InfoMessage;
        TestStatus = eval.TestStatus;
        TestStatusBadgeText = eval.TestStatusBadgeText;
        _fullTestResultText = eval.TestResultText;
        TestResultText = FormatResultPreview(eval.TestResultText, out var truncated);
        IsTestResultTruncated = truncated;
        RegexTestResultText = eval.RegexTestResultText;
    }

    private void ResetSuggestions()
    {
        SuggestedJsonPath = null;
        SuggestedHtmlPath = null;
        SuggestedRegex = null;
        OccurrenceCount = 0;
        OccurrenceWarningText = null;
        InfoMessage = null;
        _fullTestResultText = null;
        IsTestResultTruncated = false;
        TestResultText = null;
        TestStatus = LiveTestStatus.None;
        TestStatusBadgeText = string.Empty;
        RegexTestResultText = null;
        CopyFeedbackText = null;
    }

    private static string? FormatResultPreview(string? text, out bool isTruncated)
    {
        if (string.IsNullOrEmpty(text))
        {
            isTruncated = false;
            return text;
        }

        var cutIndex = text.Length;
        var lineCount = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (i >= MaxResultPreviewChars)
            {
                cutIndex = i;
                break;
            }

            if (text[i] == '\n')
            {
                lineCount++;
                if (lineCount > MaxResultPreviewLines)
                {
                    cutIndex = i;
                    break;
                }
            }
        }

        if (cutIndex >= text.Length)
        {
            isTruncated = false;
            return text;
        }

        isTruncated = true;
        var prefix = text[..cutIndex].TrimEnd('\r', '\n');
        var totalChars = text.Length.ToString("N0", FrenchCulture).Replace('\u202F', ' ').Replace('\u00A0', ' ');
        return $"{prefix}\n… [résultat tronqué : {totalChars} caractères au total]";
    }

    private static string TruncateInlineSnippet(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var singleLine = text.Replace("\r\n", " ", StringComparison.Ordinal)
                             .Replace('\n', ' ')
                             .Replace('\r', ' ');
        return singleLine.Length <= maxChars ? singleLine : $"{singleLine[..(maxChars - 1)]}…";
    }

    private void CancelPendingLoad()
    {
        if (_loadCts is not null)
        {
            _loadCts.Cancel();
            _loadCts.Dispose();
            _loadCts = null;
        }
    }

    private void CancelPendingTreeSearch()
    {
        if (_treeSearchCts is not null)
        {
            _treeSearchCts.Cancel();
            _treeSearchCts.Dispose();
            _treeSearchCts = null;
        }
    }

    private void CancelPendingSuggestion()
    {
        if (_suggestionCts is not null)
        {
            _suggestionCts.Cancel();
            _suggestionCts.Dispose();
            _suggestionCts = null;
        }
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

        foreach (var node in VisibleTreeNodes)
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

        if (!hasExpandable)
        {
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

    private static (int Count, bool HasMore) CountMatchesBounded(
        string input,
        string pattern,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern))
        {
            return (0, false);
        }

        try
        {
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            var count = 0;
            var match = regex.Match(input);
            while (match.Success)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
                if (count > MaxTrackedRegexOccurrences)
                {
                    return (MaxTrackedRegexOccurrences, true);
                }
                match = match.NextMatch();
            }

            return (count, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (0, false);
        }
    }
}
