using System.Collections.Generic;
using System.Windows.Input;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class BatchVariableEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable<BatchVariable>?> ItemsProperty =
        AvaloniaProperty.Register<BatchVariableEditor, IEnumerable<BatchVariable>?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> EnlargeListCommandProperty =
        AvaloniaProperty.Register<BatchVariableEditor, ICommand?>(nameof(EnlargeListCommand));

    public BatchVariableEditor()
    {
        InitializeComponent();
    }

    public IEnumerable<BatchVariable>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ICommand? EnlargeListCommand
    {
        get => GetValue(EnlargeListCommandProperty);
        set => SetValue(EnlargeListCommandProperty, value);
    }
}
