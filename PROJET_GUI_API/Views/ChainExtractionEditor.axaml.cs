using System.Collections.Generic;
using System.Windows.Input;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class ChainExtractionEditor : UserControl
{
    public static readonly StyledProperty<IEnumerable<ChainExtraction>?> ItemsProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, IEnumerable<ChainExtraction>?>(nameof(Items));

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<ICommand?> TestCommandProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, ICommand?>(nameof(TestCommand));

    public static readonly StyledProperty<bool> HasLastResponseProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, bool>(nameof(HasLastResponse));

    public static readonly StyledProperty<string> LastResponseSummaryProperty =
        AvaloniaProperty.Register<ChainExtractionEditor, string>(nameof(LastResponseSummary), string.Empty);

    public ChainExtractionEditor()
    {
        InitializeComponent();
    }

    public IEnumerable<ChainExtraction>? Items
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

    public ICommand? TestCommand
    {
        get => GetValue(TestCommandProperty);
        set => SetValue(TestCommandProperty, value);
    }

    public bool HasLastResponse
    {
        get => GetValue(HasLastResponseProperty);
        set => SetValue(HasLastResponseProperty, value);
    }

    public string LastResponseSummary
    {
        get => GetValue(LastResponseSummaryProperty);
        set => SetValue(LastResponseSummaryProperty, value);
    }
}
