using System.Collections.Generic;
using System.Windows.Input;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class VaryingParamEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable<VaryingParam>?> ItemsProperty =
        AvaloniaProperty.Register<VaryingParamEditor, IEnumerable<VaryingParam>?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<VaryingParamEditor, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<VaryingParamEditor, ICommand?>(nameof(RemoveCommand));

    public VaryingParamEditor()
    {
        InitializeComponent();
    }

    public IEnumerable<VaryingParam>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }
}
