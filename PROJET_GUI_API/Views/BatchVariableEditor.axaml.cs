using System.Collections.Generic;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class BatchVariableEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable<BatchVariable>?> ItemsProperty =
        AvaloniaProperty.Register<BatchVariableEditor, IEnumerable<BatchVariable>?>(nameof(Items));

    public BatchVariableEditor()
    {
        InitializeComponent();
    }

    public IEnumerable<BatchVariable>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }
}
