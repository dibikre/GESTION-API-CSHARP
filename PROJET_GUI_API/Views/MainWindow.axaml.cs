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
        if (e.PropertyName == nameof(OverlayEditorViewModel.IsOpen) &&
            _subscribedOverlay is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => OverlayEditorTextBox.Focus(), DispatcherPriority.Loaded);
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

    private void OnOverlayCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}