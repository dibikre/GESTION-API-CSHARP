using System;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class ExtractorView : UserControl
{
    public static readonly StyledProperty<ExtractorViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ExtractorView, ExtractorViewModel?>(nameof(ViewModel));

    public ExtractorView()
    {
        InitializeComponent();
    }

    public ExtractorViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ViewModel = DataContext as ExtractorViewModel;
    }
}
