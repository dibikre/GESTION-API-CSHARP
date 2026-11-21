using System;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ApiTester.Views;

public partial class ExtractorView : UserControl
{
    private const int AutoSuggestMaxSelectionLength = 4096;

    public static readonly StyledProperty<ExtractorViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ExtractorView, ExtractorViewModel?>(nameof(ViewModel));

    private TextBox? _lastSelectionBox;
    private int _cachedStart;
    private int _cachedEnd;
    private string? _cachedSelectedText;
    private int _lastAutoAppliedStart = -1;
    private int _lastAutoAppliedLength = -1;

    public ExtractorView()
    {
        InitializeComponent();

        ManualSelectionTextBox.PropertyChanged += OnTextBoxPropertyChanged;
        ManualSelectionTextBox.PointerReleased += OnManualTextBoxPointerReleased;
        ManualSelectionTextBox.KeyUp += OnManualTextBoxKeyUp;

        InputResponseTextBox.PropertyChanged += OnTextBoxPropertyChanged;
        InputResponseTextBox.PointerReleased += OnInputTextBoxPointerReleased;

        UseSelectionButton.AddHandler(InputElement.PointerPressedEvent, OnUseSelectionButtonPointerPressed, RoutingStrategies.Tunnel);
        UseSelectionButton.Click += OnUseSelectionButtonClick;
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
            _subscribedViewModel = null;
        }

        ViewModel = DataContext as ExtractorViewModel;
        if (ViewModel is not null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtractorViewModel.SelectionPreviewText) &&
            string.IsNullOrEmpty(_subscribedViewModel?.SelectionPreviewText))
        {
            ClearCachedSelection();
        }
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

            return;
        }

        if (e.Property == TextBox.SelectionStartProperty ||
            e.Property == TextBox.SelectionEndProperty)
        {
            CaptureSelectionFromTextBox(tb, allowClearWhenFocused: true);
        }
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
