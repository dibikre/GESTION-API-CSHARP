using System;
using System.ComponentModel;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ApiTester.Views;

public partial class CookieEditorView : UserControl
{
    public static readonly StyledProperty<CookieEditorViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<CookieEditorView, CookieEditorViewModel?>(nameof(ViewModel));

    private CookieEditorViewModel? _subscribedViewModel;

    public CookieEditorView()
    {
        InitializeComponent();
    }

    public CookieEditorViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        ViewModel = DataContext as CookieEditorViewModel;
        if (ViewModel is not null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CookieEditorViewModel.IsTextMode) &&
            _subscribedViewModel is { IsOpen: true, IsTextMode: true })
        {
            Dispatcher.UIThread.Post(() => CookieTextBox.Focus(), DispatcherPriority.Loaded);
        }
    }
}
