using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace ApiTester.Views;

public partial class BatchOrderPanel : UserControl
{
    public static readonly StyledProperty<ICommand?> MoveUpCommandProperty =
        AvaloniaProperty.Register<BatchOrderPanel, ICommand?>(nameof(MoveUpCommand));

    public static readonly StyledProperty<ICommand?> MoveDownCommandProperty =
        AvaloniaProperty.Register<BatchOrderPanel, ICommand?>(nameof(MoveDownCommand));

    public BatchOrderPanel()
    {
        InitializeComponent();
    }

    public ICommand? MoveUpCommand
    {
        get => GetValue(MoveUpCommandProperty);
        set => SetValue(MoveUpCommandProperty, value);
    }

    public ICommand? MoveDownCommand
    {
        get => GetValue(MoveDownCommandProperty);
        set => SetValue(MoveDownCommandProperty, value);
    }
}
