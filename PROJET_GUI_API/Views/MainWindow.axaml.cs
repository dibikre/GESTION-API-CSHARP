using System;
using System.ComponentModel;
using ApiTester.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ApiTester.Views;

public partial class MainWindow : Window
{
    private OverlayEditorViewModel? _subscribedOverlay;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedOverlay is not null)
        {
            _subscribedOverlay.PropertyChanged -= OnOverlayPropertyChanged;
            _subscribedOverlay = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            _subscribedOverlay = vm.Overlay;
            _subscribedOverlay.PropertyChanged += OnOverlayPropertyChanged;
        }
    }

    private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_subscribedOverlay is null)
        {
            return;
        }

        if (e.PropertyName is nameof(OverlayEditorViewModel.IsOpen) or nameof(OverlayEditorViewModel.IsConditionsOpen))
        {
            UpdateOverlaySplitLayout();
        }

        if (e.PropertyName == nameof(OverlayEditorViewModel.IsOpen) &&
            _subscribedOverlay.IsOpen)
        {
            Dispatcher.UIThread.Post(() => OverlayEditorTextBox.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void UpdateOverlaySplitLayout()
    {
        if (_subscribedOverlay is { IsOpen: true, IsConditionsOpen: true })
        {
            OverlayBodyGrid.ColumnDefinitions = ColumnDefinitions.Parse("1.05*,Auto,0.95*");
            OverlayBodyGrid.ColumnDefinitions[0].MinWidth = 240;
            OverlayBodyGrid.ColumnDefinitions[2].MinWidth = 260;
        }
        else
        {
            OverlayBodyGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,0,0");
            OverlayBodyGrid.ColumnDefinitions[0].MinWidth = 0;
            OverlayBodyGrid.ColumnDefinitions[2].MinWidth = 0;
        }
    }

    private void OnOverlayScrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.Overlay.CloseCommand.CanExecute(null))
        {
            vm.Overlay.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCookieScrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.CookieEditor.CloseCommand.CanExecute(null))
        {
            vm.CookieEditor.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnOverlayCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}