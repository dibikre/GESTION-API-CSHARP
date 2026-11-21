using System;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class ExtractionConditionsView : UserControl
{
    public static readonly StyledProperty<ExtractionConditionsViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ExtractionConditionsView, ExtractionConditionsViewModel?>(nameof(ViewModel));

    public ExtractionConditionsView()
    {
        InitializeComponent();
    }

    public ExtractionConditionsViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ViewModel = DataContext as ExtractionConditionsViewModel;
    }
}
