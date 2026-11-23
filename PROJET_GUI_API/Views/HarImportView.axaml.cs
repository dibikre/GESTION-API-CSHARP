using System;
using System.ComponentModel;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ApiTester.Views;

public partial class HarImportView : UserControl
{
    public static readonly StyledProperty<HarImportViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<HarImportView, HarImportViewModel?>(nameof(ViewModel));

    private HarImportViewModel? _subscribedViewModel;

    public HarImportView()
    {
        InitializeComponent();
    }

    public HarImportViewModel? ViewModel
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

        ViewModel = DataContext as HarImportViewModel;
        if (ViewModel is not null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HarImportViewModel.IsOpen) &&
            _subscribedViewModel is { IsOpen: true, HasEntries: true })
        {
            Dispatcher.UIThread.Post(() => HarSearchTextBox.Focus(), DispatcherPriority.Loaded);
        }
    }
}
