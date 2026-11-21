using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using ApiTester.Models;
using ApiTester.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ApiTester.Views;

public partial class ChainExtractionEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable<ChainExtraction>?> ItemsProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, IEnumerable<ChainExtraction>?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<ICommand?> TestCommandProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ICommand?>(nameof(TestCommand));

    public static readonly StyledProperty<ICommand?> InspectListCommandProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ICommand?>(nameof(InspectListCommand));

    public static readonly StyledProperty<bool> HasLastResponseProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, bool>(nameof(HasLastResponse));

    public static readonly StyledProperty<string> LastResponseSummaryProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, string>(nameof(LastResponseSummary), string.Empty);

    public static readonly StyledProperty<ResponseModel?> LastResponseProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ResponseModel?>(nameof(LastResponse));

    public static readonly StyledProperty<IExtractionSuggester?> ExtractionSuggesterProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, IExtractionSuggester?>(nameof(ExtractionSuggester));

    public static readonly StyledProperty<Func<ChainExtraction, IReadOnlyList<RealtimeExtractionSuggestion>>?> SourceVariablesProviderProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, Func<ChainExtraction, IReadOnlyList<RealtimeExtractionSuggestion>>?>(nameof(SourceVariablesProvider));

    public static readonly StyledProperty<Func<ChainExtraction, ResponseModel?>?> SuggestionResponseResolverProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, Func<ChainExtraction, ResponseModel?>?>(nameof(SuggestionResponseResolver));

    private readonly ObservableCollection<RealtimeExtractionSuggestion> _suggestions = [];
    private TextBox? _activeTextBox;
    private TextBox? _suppressNextGotFocusForBox;
    private ChainExtraction? _activeExtraction;
    private bool _isAttachedToVisualTree;
    private bool _isPointerInsidePopup;
    private bool _isApplyingSuggestion;
    private bool _suppressSuggestionsUpdate;

    public ChainExtractionEditor()
    {
        InitializeComponent();
        SuggestionsListBox.ItemsSource = _suggestions;
    }

    public IEnumerable<ChainExtraction>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public ICommand? TestCommand
    {
        get => GetValue(TestCommandProperty);
        set => SetValue(TestCommandProperty, value);
    }

    public ICommand? InspectListCommand
    {
        get => GetValue(InspectListCommandProperty);
        set => SetValue(InspectListCommandProperty, value);
    }

    public bool HasLastResponse
    {
        get => GetValue(HasLastResponseProperty);
        set => SetValue(HasLastResponseProperty, value);
    }

    public string LastResponseSummary
    {
        get => GetValue(LastResponseSummaryProperty);
        set => SetValue(LastResponseSummaryProperty, value);
    }

    public ResponseModel? LastResponse
    {
        get => GetValue(LastResponseProperty);
        set => SetValue(LastResponseProperty, value);
    }

    public IExtractionSuggester? ExtractionSuggester
    {
        get => GetValue(ExtractionSuggesterProperty);
        set => SetValue(ExtractionSuggesterProperty, value);
    }

    public Func<ChainExtraction, IReadOnlyList<RealtimeExtractionSuggestion>>? SourceVariablesProvider
    {
        get => GetValue(SourceVariablesProviderProperty);
        set => SetValue(SourceVariablesProviderProperty, value);
    }

    public Func<ChainExtraction, ResponseModel?>? SuggestionResponseResolver
    {
        get => GetValue(SuggestionResponseResolverProperty);
        set => SetValue(SuggestionResponseResolverProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LastResponseProperty || change.Property == HasLastResponseProperty)
        {
            var allowWithoutLastResponse = _activeExtraction?.IsVariableSource == true ||
                (_activeTextBox is not null && IsSourceVariableBox(_activeTextBox));

            if (!allowWithoutLastResponse && (LastResponse is null || !HasLastResponse))
            {
                CloseSuggestionsPopup();
            }
            else if (SuggestionsPopup.IsOpen && _activeTextBox is not null)
            {
                RefreshSuggestionsFor(_activeTextBox, openIfAny: true);
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttachedToVisualTree = false;
        _isApplyingSuggestion = false;
        _suppressNextGotFocusForBox = null;
        CloseSuggestionsPopup();
        SuggestionsPopup.PlacementTarget = null;
        SetActiveExtraction(null);
        _activeTextBox = null;
    }

    private void OnSuggestionTextBoxLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.RemoveHandler(InputElement.KeyDownEvent, OnSuggestionInputTunnelKeyDown);
            tb.AddHandler(InputElement.KeyDownEvent, OnSuggestionInputTunnelKeyDown, RoutingStrategies.Tunnel);
        }
    }

    private void OnSuggestionTextBoxUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.RemoveHandler(InputElement.KeyDownEvent, OnSuggestionInputTunnelKeyDown);
            if (ReferenceEquals(_suppressNextGotFocusForBox, tb))
            {
                _suppressNextGotFocusForBox = null;
            }
            if (ReferenceEquals(SuggestionsPopup.PlacementTarget, tb))
            {
                CloseSuggestionsPopup();
                SuggestionsPopup.PlacementTarget = null;
            }
            if (ReferenceEquals(_activeTextBox, tb))
            {
                CloseSuggestionsPopup();
                SetActiveExtraction(null);
                _activeTextBox = null;
            }
        }
    }

    private void OnSuggestionTextBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ChainExtraction extraction)
        {
            return;
        }

        _activeTextBox = tb;
        SetActiveExtraction(extraction);

        var suppressOpen = _suppressSuggestionsUpdate || ReferenceEquals(_suppressNextGotFocusForBox, tb);
        if (ReferenceEquals(_suppressNextGotFocusForBox, tb))
        {
            _suppressNextGotFocusForBox = null;
        }

        if (!suppressOpen && CanSuggestFor(tb, extraction))
        {
            RefreshSuggestionsFor(tb, openIfAny: true);
        }
    }

    private void OnSuggestionTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isPointerInsidePopup &&
                !_isApplyingSuggestion &&
                (!tb.IsFocused || !ReferenceEquals(_activeTextBox, tb)))
            {
                if (ReferenceEquals(_activeTextBox, tb))
                {
                    CloseSuggestionsPopup();
                }
            }
        }, DispatcherPriority.Background);
    }

    private void OnSuggestionTextBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressSuggestionsUpdate || sender is not TextBox tb || !tb.IsFocused)
        {
            return;
        }

        if (tb.DataContext is not ChainExtraction extraction)
        {
            return;
        }

        _activeTextBox = tb;
        SetActiveExtraction(extraction);

        if (!CanSuggestFor(tb, extraction))
        {
            CloseSuggestionsPopup();
            return;
        }

        RefreshSuggestionsFor(tb, openIfAny: true);
    }

    private void OnToggleSuggestionsButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Parent: Grid grid, DataContext: ChainExtraction extraction })
        {
            return;
        }

        var tb = grid.Children.OfType<TextBox>().FirstOrDefault();
        if (tb is null || !CanSuggestFor(tb, extraction))
        {
            return;
        }

        if (SuggestionsPopup.IsOpen && ReferenceEquals(_activeTextBox, tb))
        {
            _suppressNextGotFocusForBox = tb;
            CloseSuggestionsPopup();
            tb.Focus();
            ClearSuppressNextGotFocusAsync(tb);
            return;
        }

        _activeTextBox = tb;
        SetActiveExtraction(extraction);
        _suppressNextGotFocusForBox = tb;
        tb.Focus();
        if (ReferenceEquals(_suppressNextGotFocusForBox, tb))
        {
            _suppressNextGotFocusForBox = null;
        }
        RefreshSuggestionsFor(tb, openIfAny: true);
    }

    private void OnSuggestionInputTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ChainExtraction extraction)
        {
            return;
        }

        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (CanSuggestFor(tb, extraction))
            {
                _activeTextBox = tb;
                SetActiveExtraction(extraction);
                RefreshSuggestionsFor(tb, openIfAny: true);
                e.Handled = true;
            }
            return;
        }

        if (!SuggestionsPopup.IsOpen)
        {
            if (e.Key == Key.Down && CanSuggestFor(tb, extraction))
            {
                _activeTextBox = tb;
                SetActiveExtraction(extraction);
                RefreshSuggestionsFor(tb, openIfAny: true);
                if (SuggestionsPopup.IsOpen && _suggestions.Count > 0)
                {
                    SuggestionsListBox.SelectedIndex = 0;
                    if (SuggestionsListBox.SelectedItem is not null)
                    {
                        SuggestionsListBox.ScrollIntoView(SuggestionsListBox.SelectedItem);
                    }
                    e.Handled = true;
                }
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                CloseSuggestionsPopup();
                e.Handled = true;
                break;

            case Key.Down:
                if (_suggestions.Count > 0)
                {
                    var next = SuggestionsListBox.SelectedIndex < 0
                        ? 0
                        : Math.Min(SuggestionsListBox.SelectedIndex + 1, _suggestions.Count - 1);
                    SuggestionsListBox.SelectedIndex = next;
                    if (SuggestionsListBox.SelectedItem is not null)
                    {
                        SuggestionsListBox.ScrollIntoView(SuggestionsListBox.SelectedItem);
                    }
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (_suggestions.Count > 0)
                {
                    var prev = SuggestionsListBox.SelectedIndex <= 0
                        ? 0
                        : SuggestionsListBox.SelectedIndex - 1;
                    SuggestionsListBox.SelectedIndex = prev;
                    if (SuggestionsListBox.SelectedItem is not null)
                    {
                        SuggestionsListBox.ScrollIntoView(SuggestionsListBox.SelectedItem);
                    }
                }
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Tab:
                if (SuggestionsListBox.SelectedItem is RealtimeExtractionSuggestion selected)
                {
                    e.Handled = true;
                    ApplySuggestion(selected);
                }
                else if (e.Key == Key.Tab)
                {
                    CloseSuggestionsPopup();
                }
                break;
        }
    }

    private void OnSuggestionItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: RealtimeExtractionSuggestion suggestion } control)
        {
            return;
        }

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        _isApplyingSuggestion = true;

        // Ne jamais fermer/rouvrir PopupRoot de manière synchrone pendant PointerPressed :
        // laisser le routage du pointeur se terminer avant d'appliquer la suggestion.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ApplySuggestion(suggestion);
            }
            finally
            {
                _isApplyingSuggestion = false;
            }
        }, DispatcherPriority.Normal);
    }

    private void OnSuggestionsBorderPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerInsidePopup = true;
    }

    private void OnSuggestionsBorderPointerExited(object? sender, PointerEventArgs e)
    {
        _isPointerInsidePopup = false;
    }

    private void OnActiveExtractionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSuggestionsUpdate || _activeTextBox is null || _activeExtraction is null)
        {
            return;
        }

        if (e.PropertyName is nameof(ChainExtraction.Kind)
            or nameof(ChainExtraction.Source)
            or nameof(ChainExtraction.IsList)
            or nameof(ChainExtraction.SourceVariableName))
        {
            if (SuggestionsPopup.IsOpen && CanSuggestFor(_activeTextBox, _activeExtraction))
            {
                RefreshSuggestionsFor(_activeTextBox, openIfAny: true);
            }
            else if (!CanSuggestFor(_activeTextBox, _activeExtraction))
            {
                CloseSuggestionsPopup();
            }
        }
    }

    private void SetActiveExtraction(ChainExtraction? extraction)
    {
        if (ReferenceEquals(_activeExtraction, extraction))
        {
            return;
        }

        if (_activeExtraction is not null)
        {
            _activeExtraction.PropertyChanged -= OnActiveExtractionPropertyChanged;
        }

        _activeExtraction = extraction;

        if (_activeExtraction is not null)
        {
            _activeExtraction.PropertyChanged += OnActiveExtractionPropertyChanged;
        }
    }

    private ResponseModel? ResolveEffectiveResponse(ChainExtraction extraction)
    {
        if (SuggestionResponseResolver is not null)
        {
            return SuggestionResponseResolver(extraction);
        }

        return extraction.IsVariableSource ? null : LastResponse;
    }

    private bool CanSuggestFor(TextBox tb, ChainExtraction extraction)
    {
        if (IsSourceVariableBox(tb))
        {
            return extraction.IsVariableSource;
        }

        if (IsHeaderNameBox(tb))
        {
            return HasLastResponse && LastResponse is not null && extraction.IsHeaderSource;
        }

        if (!extraction.RequiresExpression)
        {
            return false;
        }

        if (extraction.IsVariableSource)
        {
            return ResolveEffectiveResponse(extraction) is not null;
        }

        return HasLastResponse && LastResponse is not null;
    }

    private void RefreshSuggestionsFor(TextBox tb, bool openIfAny)
    {
        if (!_isAttachedToVisualTree ||
            tb.DataContext is not ChainExtraction extraction ||
            !tb.IsVisible ||
            !tb.IsEnabled ||
            TopLevel.GetTopLevel(tb) is null)
        {
            CloseSuggestionsPopup();
            return;
        }

        var isSourceVariable = IsSourceVariableBox(tb);
        var isHeaderName = IsHeaderNameBox(tb);
        var query = tb.Text;

        IReadOnlyList<RealtimeExtractionSuggestion> items;
        if (isSourceVariable)
        {
            items = FilterSourceVariableSuggestions(
                SourceVariablesProvider?.Invoke(extraction) ?? [],
                query);
        }
        else
        {
            var effectiveResponse = extraction.IsVariableSource
                ? ResolveEffectiveResponse(extraction)
                : (HasLastResponse ? LastResponse : null);

            if (effectiveResponse is null)
            {
                CloseSuggestionsPopup();
                return;
            }

            var suggester = ExtractionSuggester ?? new ExtractionSuggester();
            items = isHeaderName
                ? suggester.SuggestHeaderNames(effectiveResponse, query, maxResults: 25)
                : suggester.SuggestRealtime(
                    effectiveResponse,
                    extraction.Source,
                    extraction.Kind,
                    query,
                    extraction.HeaderName,
                    extraction.VariableName,
                    extraction.IsList,
                    maxResults: 25);
        }

        _suggestions.Clear();
        foreach (var item in items)
        {
            _suggestions.Add(item);
        }

        if (_suggestions.Count == 0 || !openIfAny)
        {
            CloseSuggestionsPopup();
            return;
        }

        SuggestionsHeaderText.Text = isSourceVariable
            ? $"Variables disponibles ({_suggestions.Count})"
            : isHeaderName
                ? $"En-têtes de la réponse ({_suggestions.Count})"
                : extraction.Kind == ExtractionKind.JsonPath
                    ? $"Suggestions JSONPath ({_suggestions.Count})"
                    : $"Suggestions Regex ({_suggestions.Count})";

        var targetWidth = tb.Bounds.Width > 0 ? Math.Clamp(tb.Bounds.Width, 420, 680) : 480;
        SuggestionsBorder.Width = targetWidth;
        if (!ReferenceEquals(SuggestionsPopup.PlacementTarget, tb))
        {
            SuggestionsPopup.PlacementTarget = tb;
        }
        SuggestionsListBox.SelectedIndex = -1;
        if (!SuggestionsPopup.IsOpen)
        {
            SuggestionsPopup.IsOpen = true;
        }
    }

    private static IReadOnlyList<RealtimeExtractionSuggestion> FilterSourceVariableSuggestions(
        IReadOnlyList<RealtimeExtractionSuggestion> candidates,
        string? query)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var norm = (query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(norm))
        {
            return candidates.Take(25).ToList();
        }

        var exactOrPrefix = new List<RealtimeExtractionSuggestion>();
        var contains = new List<RealtimeExtractionSuggestion>();
        foreach (var candidate in candidates)
        {
            if (candidate.Expression.StartsWith(norm, StringComparison.OrdinalIgnoreCase))
            {
                exactOrPrefix.Add(candidate);
            }
            else if (candidate.Expression.Contains(norm, StringComparison.OrdinalIgnoreCase) ||
                     candidate.PreviewValue.Contains(norm, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(candidate);
            }
        }

        return exactOrPrefix.Concat(contains).Take(25).ToList();
    }

    private void ApplySuggestion(RealtimeExtractionSuggestion suggestion)
    {
        if (_activeExtraction is null)
        {
            CloseSuggestionsPopup();
            return;
        }

        var extraction = _activeExtraction;
        var targetBox = _activeTextBox;
        var isSourceVariable = targetBox is not null && IsSourceVariableBox(targetBox);
        var isHeaderName = targetBox is not null && IsHeaderNameBox(targetBox);

        _suppressSuggestionsUpdate = true;
        _suppressNextGotFocusForBox = targetBox;
        try
        {
            CloseSuggestionsPopup();

            if (isSourceVariable)
            {
                extraction.SourceVariableName = suggestion.Expression;
            }
            else if (isHeaderName)
            {
                extraction.HeaderName = suggestion.Expression;
            }
            else
            {
                extraction.Expression = suggestion.Expression;
                if (!string.IsNullOrWhiteSpace(suggestion.TargetHeaderName) &&
                    string.IsNullOrWhiteSpace(extraction.HeaderName))
                {
                    extraction.HeaderName = suggestion.TargetHeaderName;
                }

                if (suggestion.SuggestsList &&
                    extraction.Kind == ExtractionKind.JsonPath &&
                    extraction.Expression.Contains("[*]", StringComparison.Ordinal) &&
                    !extraction.IsList)
                {
                    extraction.IsList = true;
                }
            }

            if (!isSourceVariable &&
                string.IsNullOrWhiteSpace(extraction.VariableName) &&
                !string.IsNullOrWhiteSpace(suggestion.SuggestedVariableName))
            {
                extraction.VariableName = EnsureUniqueVariableName(suggestion.SuggestedVariableName, extraction);
            }

            if (targetBox is not null &&
                _isAttachedToVisualTree &&
                targetBox.IsVisible &&
                targetBox.IsEnabled &&
                TopLevel.GetTopLevel(targetBox) is not null)
            {
                targetBox.Focus();
                var textLength = targetBox.Text?.Length ?? 0;
                targetBox.CaretIndex = textLength;
            }
        }
        finally
        {
            _suppressSuggestionsUpdate = false;
            ClearSuppressNextGotFocusAsync(targetBox);
        }

        if (!isSourceVariable ||
            extraction.Kind == ExtractionKind.FullResponse ||
            !string.IsNullOrWhiteSpace(extraction.Expression))
        {
            if (TestCommand?.CanExecute(extraction) == true)
            {
                TestCommand.Execute(extraction);
            }
        }
    }

    private void ClearSuppressNextGotFocusAsync(TextBox? targetBox)
    {
        if (targetBox is null)
        {
            _suppressNextGotFocusForBox = null;
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_suppressNextGotFocusForBox, targetBox))
            {
                _suppressNextGotFocusForBox = null;
            }
        }, DispatcherPriority.Background);
    }

    private string EnsureUniqueVariableName(string baseName, ChainExtraction current)
    {
        if (Items is null)
        {
            return baseName;
        }

        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (!ReferenceEquals(item, current) && !string.IsNullOrWhiteSpace(item.VariableName))
            {
                existingNames.Add(item.VariableName.Trim());
            }
        }

        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return baseName;
    }

    private void CloseSuggestionsPopup()
    {
        _isPointerInsidePopup = false;
        SuggestionsListBox.SelectedIndex = -1;
        if (SuggestionsPopup.IsOpen)
        {
            SuggestionsPopup.IsOpen = false;
        }
    }

    private static bool IsHeaderNameBox(TextBox tb) =>
        string.Equals(tb.Tag as string, "HeaderName", StringComparison.Ordinal);

    private static bool IsSourceVariableBox(TextBox tb) =>
        string.Equals(tb.Tag as string, "SourceVariable", StringComparison.Ordinal);
}
