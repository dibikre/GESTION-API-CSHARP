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

    private readonly ObservableCollection<RealtimeExtractionSuggestion> _suggestions = [];
    private TextBox? _activeTextBox;
    private ChainExtraction? _activeExtraction;
    private bool _isPointerInsidePopup;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LastResponseProperty || change.Property == HasLastResponseProperty)
        {
            if (LastResponse is null || !HasLastResponse)
            {
                CloseSuggestionsPopup();
            }
            else if (SuggestionsPopup.IsOpen && _activeTextBox is not null)
            {
                RefreshSuggestionsFor(_activeTextBox, openIfAny: true);
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CloseSuggestionsPopup();
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

        if (!_suppressSuggestionsUpdate && CanSuggestFor(tb, extraction))
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
            if (!_isPointerInsidePopup && (!tb.IsFocused || !ReferenceEquals(_activeTextBox, tb)))
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
            CloseSuggestionsPopup();
            tb.Focus();
            return;
        }

        _activeTextBox = tb;
        SetActiveExtraction(extraction);
        tb.Focus();
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
                    ApplySuggestion(selected);
                    e.Handled = true;
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
        if (sender is Control { DataContext: RealtimeExtractionSuggestion suggestion })
        {
            ApplySuggestion(suggestion);
            e.Handled = true;
        }
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
            or nameof(ChainExtraction.IsList))
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

    private bool CanSuggestFor(TextBox tb, ChainExtraction extraction)
    {
        if (!HasLastResponse || LastResponse is null)
        {
            return false;
        }

        if (IsHeaderNameBox(tb))
        {
            return extraction.IsHeaderSource;
        }

        return extraction.RequiresExpression;
    }

    private void RefreshSuggestionsFor(TextBox tb, bool openIfAny)
    {
        if (tb.DataContext is not ChainExtraction extraction || LastResponse is null)
        {
            CloseSuggestionsPopup();
            return;
        }

        var suggester = ExtractionSuggester ?? new ExtractionSuggester();
        var isHeaderName = IsHeaderNameBox(tb);
        var query = tb.Text;

        IReadOnlyList<RealtimeExtractionSuggestion> items = isHeaderName
            ? suggester.SuggestHeaderNames(LastResponse, query, maxResults: 25)
            : suggester.SuggestRealtime(
                LastResponse,
                extraction.Source,
                extraction.Kind,
                query,
                extraction.HeaderName,
                extraction.VariableName,
                extraction.IsList,
                maxResults: 25);

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

        SuggestionsHeaderText.Text = isHeaderName
            ? $"En-têtes de la réponse ({_suggestions.Count})"
            : extraction.Kind == ExtractionKind.JsonPath
                ? $"Suggestions JSONPath ({_suggestions.Count})"
                : $"Suggestions Regex ({_suggestions.Count})";

        var targetWidth = tb.Bounds.Width > 0 ? Math.Clamp(tb.Bounds.Width, 420, 680) : 480;
        SuggestionsBorder.Width = targetWidth;
        SuggestionsPopup.PlacementTarget = tb;
        SuggestionsListBox.SelectedIndex = -1;
        SuggestionsPopup.IsOpen = true;
    }

    private void ApplySuggestion(RealtimeExtractionSuggestion suggestion)
    {
        if (_activeExtraction is null)
        {
            return;
        }

        var extraction = _activeExtraction;
        var targetBox = _activeTextBox;
        var isHeaderName = targetBox is not null && IsHeaderNameBox(targetBox);

        _suppressSuggestionsUpdate = true;
        try
        {
            if (isHeaderName)
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

            if (string.IsNullOrWhiteSpace(extraction.VariableName) &&
                !string.IsNullOrWhiteSpace(suggestion.SuggestedVariableName))
            {
                extraction.VariableName = EnsureUniqueVariableName(suggestion.SuggestedVariableName, extraction);
            }
        }
        finally
        {
            _suppressSuggestionsUpdate = false;
        }

        CloseSuggestionsPopup();

        if (targetBox is not null)
        {
            targetBox.Focus();
            targetBox.CaretIndex = targetBox.Text?.Length ?? 0;
        }

        if (TestCommand?.CanExecute(extraction) == true)
        {
            TestCommand.Execute(extraction);
        }
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
        SuggestionsPopup.IsOpen = false;
    }

    private static bool IsHeaderNameBox(TextBox tb) =>
        string.Equals(tb.Tag as string, "HeaderName", StringComparison.Ordinal);
}
