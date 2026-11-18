using System.Collections.Generic;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace ApiTester.Views;

public partial class BatchPreviewPanel : UserControl
{
    public static readonly StyledProperty<IEnumerable<BatchPreviewItem>?> ItemsProperty =
        AvaloniaProperty.Register<BatchPreviewPanel, IEnumerable<BatchPreviewItem>?>(nameof(Items));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<BatchPreviewPanel, bool>(
            nameof(IsExpanded),
            defaultBindingMode: BindingMode.TwoWay);

    public BatchPreviewPanel()
    {
        InitializeComponent();
    }

    public IEnumerable<BatchPreviewItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }
}
