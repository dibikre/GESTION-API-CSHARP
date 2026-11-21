using System;
using ApiTester.Models;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ApiTester.Views;

public partial class ChainView : UserControl
{
    public static readonly StyledProperty<ChainViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ChainView, ChainViewModel?>(nameof(ViewModel));

    public ChainView()
    {
        InitializeComponent();
    }

    public ChainViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ViewModel = DataContext as ChainViewModel;
    }

    private void OnCopyResultAsCurlMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ChainStepResult result } &&
            ViewModel?.CopyResultAsCurlCommand.CanExecute(result) == true)
        {
            ViewModel.CopyResultAsCurlCommand.Execute(result);
        }
    }
}
