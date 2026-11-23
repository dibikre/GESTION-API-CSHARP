using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ApiTester.Views;

public partial class ExtractorView : UserControl
{
    private const int AutoSuggestMaxSelectionLength = 4096;
    private const int MaxCodeSearchMatches = 5_000;
    private const int CodeSearchDebounceMs = 140;
    private const int ImmediateSearchThresholdChars = 30_000;

    public static readonly StyledProperty<ExtractorViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ExtractorView, ExtractorViewModel?>(nameof(ViewModel));

    private readonly List<(int Start, int Length)> _searchMatches = [];
    private CancellationTokenSource? _codeSearchCts;
    private bool _hasMoreCodeSearchMatches;
    private TextBox? _lastSelectionBox;
    private TextPresenter? _manualTextPresenter;
    private ScrollViewer? _manualScrollViewer;
    private int _cachedStart;
    private int _cachedEnd;
    private string? _cachedSelectedText;
    private int _lastAutoAppliedStart = -1;
    private int _lastAutoAppliedLength = -1;
    private int _activeSearchMatchIndex = -1;
    private bool _suppressSelectionCapture;

    public ExtractorView()
    {
        InitializeComponent();

        ManualSelectionTextBox.TemplateApplied += OnManualTextBoxTemplateApplied;
        ManualSelectionTextBox.PropertyChanged += OnTextBoxPropertyChanged;
        ManualSelectionTextBox.PointerReleased += OnManualTextBoxPointerReleased;
        ManualSelectionTextBox.KeyUp += OnManualTextBoxKeyUp;
        ManualSelectionTextBox.SizeChanged += (_, _) => CodeSearchOverlay.InvalidateVisual();

        InputResponseTextBox.PropertyChanged += OnTextBoxPropertyChanged;
        InputResponseTextBox.PointerReleased += OnInputTextBoxPointerReleased;

        UseSelectionButton.AddHandler(InputElement.PointerPressedEvent, OnUseSelectionButtonPointerPressed, RoutingStrategies.Tunnel);
        UseSelectionButton.Click += OnUseSelectionButtonClick;

        TreeSearchTextBox.KeyDown += OnTreeSearchTextBoxKeyDown;

        CodeSearchTextBox.TextChanged += (_, _) => _ = UpdateCodeSearchAsync(scrollToMatch: true);
        CodeSearchTextBox.KeyDown += OnCodeSearchTextBoxKeyDown;
        CodeSearchPrevButton.Click += (_, _) => NavigateCodeSearch(-1);
        CodeSearchNextButton.Click += (_, _) => NavigateCodeSearch(1);

        AddHandler(InputElement.KeyDownEvent, OnExtractorTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    public ExtractorViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private ExtractorViewModel? _subscribedViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.ScrollToTreeNodeCallback = null;
            _subscribedViewModel = null;
        }

        ViewModel = DataContext as ExtractorViewModel;
        if (ViewModel is not null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedViewModel.ScrollToTreeNodeCallback = ScrollTreeToNode;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ScrollToTreeNodeCallback = ScrollTreeToNode;
        }

        if (_subscribedViewModel?.IsManualMode == true)
        {
            Dispatcher.UIThread.Post(EnsureManualTemplateParts, DispatcherPriority.Loaded);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CancelPendingCodeSearch();

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ScrollToTreeNodeCallback = null;
        }

        if (_manualScrollViewer is not null)
        {
            _manualScrollViewer.ScrollChanged -= OnManualScrollChanged;
            _manualScrollViewer = null;
        }
        _manualTextPresenter = null;
        CodeSearchOverlay.Attach(null, null);
    }

    private void ScrollTreeToNode(ExtractorNode node)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel?.IsTreeMode == true)
            {
                TreeListBox.ScrollIntoView(node);
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtractorViewModel.SelectionPreviewText) &&
            string.IsNullOrEmpty(_subscribedViewModel?.SelectionPreviewText))
        {
            ClearCachedSelection();
        }
        else if (e.PropertyName == nameof(ExtractorViewModel.IsManualMode))
        {
            if (_subscribedViewModel?.IsManualMode == true)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    EnsureManualTemplateParts();
                    if (!string.IsNullOrEmpty(CodeSearchTextBox.Text))
                    {
                        _ = UpdateCodeSearchAsync(scrollToMatch: false);
                    }
                    else
                    {
                        CodeSearchOverlay.InvalidateVisual();
                    }
                }, DispatcherPriority.Loaded);
            }
            else
            {
                CancelPendingCodeSearch();
                _searchMatches.Clear();
                _hasMoreCodeSearchMatches = false;
                _activeSearchMatchIndex = -1;
                CodeSearchOverlay.SetSearchState(_searchMatches, -1);
            }
        }
    }

    private void OnManualTextBoxTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_manualScrollViewer is not null)
        {
            _manualScrollViewer.ScrollChanged -= OnManualScrollChanged;
        }

        _manualTextPresenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        _manualScrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer")
            ?? _manualTextPresenter?.FindAncestorOfType<ScrollViewer>();

        if (_manualScrollViewer is not null)
        {
            _manualScrollViewer.ScrollChanged += OnManualScrollChanged;
        }

        CodeSearchOverlay.Attach(_manualTextPresenter, _manualScrollViewer);
    }

    private void EnsureManualTemplateParts()
    {
        if (_manualTextPresenter is not null && _manualScrollViewer is not null)
        {
            return;
        }

        _manualTextPresenter ??= ManualSelectionTextBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        var scrollViewer = _manualScrollViewer
            ?? _manualTextPresenter?.FindAncestorOfType<ScrollViewer>()
            ?? ManualSelectionTextBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        if (!ReferenceEquals(_manualScrollViewer, scrollViewer))
        {
            if (_manualScrollViewer is not null)
            {
                _manualScrollViewer.ScrollChanged -= OnManualScrollChanged;
            }

            _manualScrollViewer = scrollViewer;
            if (_manualScrollViewer is not null)
            {
                _manualScrollViewer.ScrollChanged += OnManualScrollChanged;
            }
        }

        CodeSearchOverlay.Attach(_manualTextPresenter, _manualScrollViewer);
    }

    private void OnManualScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        CodeSearchOverlay.InvalidateVisual();
    }

    private void OnTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        if (e.Property == TextBox.TextProperty)
        {
            if (ReferenceEquals(_lastSelectionBox, tb) || ReferenceEquals(tb, InputResponseTextBox))
            {
                ClearCachedSelection();
            }

            if (ReferenceEquals(tb, ManualSelectionTextBox) && ViewModel?.IsManualMode == true)
            {
                _ = UpdateCodeSearchAsync(scrollToMatch: false);
            }

            return;
        }

        if (_suppressSelectionCapture)
        {
            return;
        }

        if (e.Property == TextBox.SelectionStartProperty ||
            e.Property == TextBox.SelectionEndProperty)
        {
            CaptureSelectionFromTextBox(tb, allowClearWhenFocused: true);
        }
    }

    private void OnExtractorTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.IsTreeMode)
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                TreeSearchTextBox.Focus();
                TreeSearchTextBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F3 && ViewModel.CanNavigateTreeSearch)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    ViewModel.NavigatePrevTreeMatchCommand.Execute(null);
                }
                else
                {
                    ViewModel.NavigateNextTreeMatchCommand.Execute(null);
                }
                e.Handled = true;
            }

            return;
        }

        if (!ViewModel.IsManualMode)
        {
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CodeSearchTextBox.Focus();
            CodeSearchTextBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && _searchMatches.Count > 0)
        {
            NavigateCodeSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
    }

    private void OnTreeSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter && ViewModel.CanNavigateTreeSearch)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                ViewModel.NavigatePrevTreeMatchCommand.Execute(null);
            }
            else
            {
                ViewModel.NavigateNextTreeMatchCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ViewModel.HasTreeSearchQuery)
        {
            ViewModel.ClearTreeSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCodeSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _searchMatches.Count > 0)
        {
            NavigateCodeSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(CodeSearchTextBox.Text))
        {
            CodeSearchTextBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private async Task UpdateCodeSearchAsync(bool scrollToMatch)
    {
        CancelPendingCodeSearch();

        var query = CodeSearchTextBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(query) || ViewModel?.IsManualMode != true)
        {
            _searchMatches.Clear();
            _hasMoreCodeSearchMatches = false;
            _activeSearchMatchIndex = -1;
            CodeSearchCountText.IsVisible = false;
            CodeSearchCountText.Classes.Remove("empty");
            CodeSearchPrevButton.IsEnabled = false;
            CodeSearchNextButton.IsEnabled = false;
            CodeSearchOverlay.SetSearchState(_searchMatches, -1);
            return;
        }

        var text = ManualSelectionTextBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            ApplyCodeSearchResults([], hasMore: false, scrollToMatch: false);
            return;
        }

        if (text.Length <= ImmediateSearchThresholdChars)
        {
            var (syncMatches, syncMore) = FindCodeMatches(text, query, MaxCodeSearchMatches, CancellationToken.None);
            ApplyCodeSearchResults(syncMatches, syncMore, scrollToMatch);
            return;
        }

        var cts = new CancellationTokenSource();
        _codeSearchCts = cts;
        var ct = cts.Token;

        try
        {
            await Task.Delay(CodeSearchDebounceMs, ct);
            var (matches, hasMore) = await Task.Run(
                () => FindCodeMatches(text, query, MaxCodeSearchMatches, ct),
                ct);

            if (ct.IsCancellationRequested || !ReferenceEquals(_codeSearchCts, cts))
            {
                return;
            }

            if (ViewModel?.IsManualMode != true ||
                !string.Equals(CodeSearchTextBox.Text ?? string.Empty, query, StringComparison.Ordinal))
            {
                return;
            }

            ApplyCodeSearchResults(matches, hasMore, scrollToMatch);
        }
        catch (OperationCanceledException)
        {
            // Recherche remplacée ou annulée.
        }
    }

    private static (List<(int Start, int Length)> Matches, bool HasMore) FindCodeMatches(
        string text,
        string query,
        int maxMatches,
        CancellationToken ct)
    {
        var matches = new List<(int Start, int Length)>();
        var hasMore = false;
        var pos = 0;

        while (pos <= text.Length - query.Length)
        {
            if ((matches.Count & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var found = text.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            if (matches.Count >= maxMatches)
            {
                hasMore = true;
                break;
            }

            matches.Add((found, query.Length));
            pos = found + query.Length;
        }

        return (matches, hasMore);
    }

    private void ApplyCodeSearchResults(List<(int Start, int Length)> matches, bool hasMore, bool scrollToMatch)
    {
        _searchMatches.Clear();
        _searchMatches.AddRange(matches);
        _hasMoreCodeSearchMatches = hasMore;

        if (_searchMatches.Count == 0)
        {
            _activeSearchMatchIndex = -1;
            CodeSearchCountText.Text = "0/0";
            CodeSearchCountText.IsVisible = true;
            if (!CodeSearchCountText.Classes.Contains("empty"))
            {
                CodeSearchCountText.Classes.Add("empty");
            }
            CodeSearchPrevButton.IsEnabled = false;
            CodeSearchNextButton.IsEnabled = false;
            CodeSearchOverlay.SetSearchState(_searchMatches, -1);
            return;
        }

        var anchor = Math.Max(0, Math.Min(ManualSelectionTextBox.SelectionStart, ManualSelectionTextBox.SelectionEnd));
        var nextIndex = 0;
        for (var i = 0; i < _searchMatches.Count; i++)
        {
            if (_searchMatches[i].Start >= anchor)
            {
                nextIndex = i;
                break;
            }
        }

        _activeSearchMatchIndex = nextIndex;
        CodeSearchCountText.Text = FormatCodeSearchCount(_activeSearchMatchIndex + 1, _searchMatches.Count, _hasMoreCodeSearchMatches);
        CodeSearchCountText.IsVisible = true;
        CodeSearchCountText.Classes.Remove("empty");
        CodeSearchPrevButton.IsEnabled = true;
        CodeSearchNextButton.IsEnabled = true;

        EnsureManualTemplateParts();
        CodeSearchOverlay.SetSearchState(_searchMatches, _activeSearchMatchIndex);

        if (scrollToMatch)
        {
            ScrollToSearchMatch(_activeSearchMatchIndex, selectMatch: false);
        }
    }

    private void NavigateCodeSearch(int delta)
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        if (_activeSearchMatchIndex < 0 || _activeSearchMatchIndex >= _searchMatches.Count)
        {
            _activeSearchMatchIndex = 0;
        }
        else
        {
            _activeSearchMatchIndex = (_activeSearchMatchIndex + delta + _searchMatches.Count) % _searchMatches.Count;
        }

        CodeSearchCountText.Text = FormatCodeSearchCount(_activeSearchMatchIndex + 1, _searchMatches.Count, _hasMoreCodeSearchMatches);
        EnsureManualTemplateParts();
        CodeSearchOverlay.SetSearchState(_searchMatches, _activeSearchMatchIndex);
        ScrollToSearchMatch(_activeSearchMatchIndex, selectMatch: true);
    }

    private static string FormatCodeSearchCount(int current, int total, bool hasMore) =>
        hasMore ? $"{current}/{total}+" : $"{current}/{total}";

    private void CancelPendingCodeSearch()
    {
        if (_codeSearchCts is not null)
        {
            _codeSearchCts.Cancel();
            _codeSearchCts.Dispose();
            _codeSearchCts = null;
        }
    }

    private void ScrollToSearchMatch(int matchIndex, bool selectMatch)
    {
        if (matchIndex < 0 || matchIndex >= _searchMatches.Count)
        {
            return;
        }

        var (start, length) = _searchMatches[matchIndex];
        var text = ManualSelectionTextBox.Text ?? string.Empty;
        if (start < 0 || start + length > text.Length)
        {
            return;
        }

        if (selectMatch)
        {
            _suppressSelectionCapture = true;
            try
            {
                ManualSelectionTextBox.SelectionStart = start;
                ManualSelectionTextBox.SelectionEnd = start + length;
                ManualSelectionTextBox.CaretIndex = start + length;

                _cachedStart = start;
                _cachedEnd = start + length;
                _cachedSelectedText = text.Substring(start, length);
                _lastSelectionBox = ManualSelectionTextBox;
                ViewModel?.UpdateTextSelection(_cachedStart, _cachedEnd, _cachedSelectedText);
            }
            finally
            {
                _suppressSelectionCapture = false;
            }
        }

        EnsureManualTemplateParts();
        var presenter = _manualTextPresenter;
        var scrollViewer = _manualScrollViewer;
        if (presenter?.TextLayout is null || scrollViewer is null)
        {
            Dispatcher.UIThread.Post(() => ScrollPresenterToRange(start, length), DispatcherPriority.Loaded);
            return;
        }

        ScrollPresenterToRange(start, length);
    }

    private void ScrollPresenterToRange(int start, int length)
    {
        EnsureManualTemplateParts();
        var presenter = _manualTextPresenter;
        var scrollViewer = _manualScrollViewer;
        var textLayout = presenter?.TextLayout;
        var presenterText = presenter?.Text;
        if (textLayout is null || scrollViewer is null || string.IsNullOrEmpty(presenterText) || start + length > presenterText.Length)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_manualTextPresenter?.TextLayout is { } layout &&
                    _manualScrollViewer is { } sv &&
                    _manualTextPresenter.Text is { Length: > 0 } pt &&
                    start + length <= pt.Length)
                {
                    var deferredRect = layout.HitTestTextRange(start, Math.Min(length, 128)).FirstOrDefault();
                    if (deferredRect.Width <= 0 && deferredRect.Height <= 0)
                    {
                        deferredRect = layout.HitTestTextPosition(start);
                    }
                    if (deferredRect.Width > 0 || deferredRect.Height > 0)
                    {
                        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
                        var targetY = Math.Clamp(deferredRect.Center.Y - sv.Viewport.Height * 0.4, 0, maxY);
                        sv.Offset = new Vector(sv.Offset.X, targetY);
                    }
                    CodeSearchOverlay.InvalidateVisual();
                }
            }, DispatcherPriority.Render);
            return;
        }

        var rect = textLayout.HitTestTextRange(start, Math.Min(length, 128)).FirstOrDefault();
        if (rect.Width <= 0 && rect.Height <= 0)
        {
            rect = textLayout.HitTestTextPosition(start);
        }

        if (rect.Width > 0 || rect.Height > 0)
        {
            var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var targetY = Math.Clamp(rect.Center.Y - scrollViewer.Viewport.Height * 0.4, 0, maxY);
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetY);
        }

        CodeSearchOverlay.InvalidateVisual();
    }

    private void OnManualTextBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (CaptureSelectionFromTextBox(ManualSelectionTextBox, allowClearWhenFocused: true))
        {
            AutoApplyIfSmallSelection();
        }
    }

    private void OnManualTextBoxKeyUp(object? sender, KeyEventArgs e)
    {
        if (CaptureSelectionFromTextBox(ManualSelectionTextBox, allowClearWhenFocused: true))
        {
            AutoApplyIfSmallSelection();
        }
    }

    private void OnInputTextBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CaptureSelectionFromTextBox(InputResponseTextBox, allowClearWhenFocused: true);
    }

    private void OnUseSelectionButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        SnapshotCurrentLiveSelection();
    }

    private void OnUseSelectionButtonClick(object? sender, RoutedEventArgs e)
    {
        SnapshotCurrentLiveSelection();
        ViewModel?.UseSelectionFromView(_cachedStart, _cachedEnd, _cachedSelectedText);
    }

    private void SnapshotCurrentLiveSelection()
    {
        if (CaptureSelectionFromTextBox(ManualSelectionTextBox, allowClearWhenFocused: false))
        {
            return;
        }

        CaptureSelectionFromTextBox(InputResponseTextBox, allowClearWhenFocused: false);
    }

    private bool CaptureSelectionFromTextBox(TextBox tb, bool allowClearWhenFocused)
    {
        var text = tb.Text ?? string.Empty;
        var s = Math.Clamp(Math.Min(tb.SelectionStart, tb.SelectionEnd), 0, text.Length);
        var e = Math.Clamp(Math.Max(tb.SelectionStart, tb.SelectionEnd), 0, text.Length);

        if (e > s)
        {
            _cachedStart = s;
            _cachedEnd = e;
            _cachedSelectedText = text[s..e];
            _lastSelectionBox = tb;
            ViewModel?.UpdateTextSelection(s, e, _cachedSelectedText);
            return true;
        }

        if (allowClearWhenFocused && tb.IsFocused && ReferenceEquals(_lastSelectionBox, tb))
        {
            ClearCachedSelection();
            ViewModel?.UpdateTextSelection(0, 0, null);
        }

        return false;
    }

    private void AutoApplyIfSmallSelection()
    {
        if (ViewModel is null || !ViewModel.IsManualMode || string.IsNullOrEmpty(_cachedSelectedText))
        {
            return;
        }

        var len = _cachedEnd - _cachedStart;
        if (len <= 0 || len > AutoSuggestMaxSelectionLength)
        {
            return;
        }

        if (_cachedStart == _lastAutoAppliedStart && len == _lastAutoAppliedLength)
        {
            return;
        }

        _lastAutoAppliedStart = _cachedStart;
        _lastAutoAppliedLength = len;
        ViewModel.UseSelectionFromView(_cachedStart, _cachedEnd, _cachedSelectedText);
    }

    private void ClearCachedSelection()
    {
        _cachedStart = 0;
        _cachedEnd = 0;
        _cachedSelectedText = null;
        _lastAutoAppliedStart = -1;
        _lastAutoAppliedLength = -1;
    }
}
