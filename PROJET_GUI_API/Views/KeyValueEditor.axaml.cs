using System.Collections.Generic;
using System.Windows.Input;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class KeyValueEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable<KeyValueItem>?> ItemsProperty =
        AvaloniaProperty.Register<KeyValueEditor, IEnumerable<KeyValueItem>?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<KeyValueEditor, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<KeyValueEditor, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<string> AddButtonTextProperty =
        AvaloniaProperty.Register<KeyValueEditor, string>(nameof(AddButtonText), "Ajouter");

    public static readonly StyledProperty<string> KeyPlaceholderProperty =
        AvaloniaProperty.Register<KeyValueEditor, string>(nameof(KeyPlaceholder), "Clé");

    public static readonly StyledProperty<string> ValuePlaceholderProperty =
        AvaloniaProperty.Register<KeyValueEditor, string>(nameof(ValuePlaceholder), "Valeur");

    public KeyValueEditor()
    {
        InitializeComponent();
    }

    public IEnumerable<KeyValueItem>? Items
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

    public string AddButtonText
    {
        get => GetValue(AddButtonTextProperty);
        set => SetValue(AddButtonTextProperty, value);
    }

    public string KeyPlaceholder
    {
        get => GetValue(KeyPlaceholderProperty);
        set => SetValue(KeyPlaceholderProperty, value);
    }

    public string ValuePlaceholder
    {
        get => GetValue(ValuePlaceholderProperty);
        set => SetValue(ValuePlaceholderProperty, value);
    }
}
