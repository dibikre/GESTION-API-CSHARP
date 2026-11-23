using System;
using System.Linq;
using ApiTester.Models;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ApiTester.Views;

public partial class ChainView : UserControl
{
    public static readonly StyledProperty<ChainViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<ChainView, ChainViewModel?>(nameof(ViewModel));

    public ChainView()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnChainTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    public ChainViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public void FocusStepsList()
    {
        if (ViewModel is { HasSteps: true })
        {
            StepsListBox.Focus();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ViewModel = DataContext as ChainViewModel;
    }

    private void OnChainTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsRunning || !ViewModel.HasSteps)
        {
            return;
        }

        var sourceVisual = e.Source as Visual;
        var focusedVisual = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;

        if (IsTextInputElement(sourceVisual) || IsTextInputElement(focusedVisual))
        {
            return;
        }

        if (IsDescendantOrSelf(sourceVisual, ResultsListBox) ||
            IsDescendantOrSelf(focusedVisual, ResultsListBox))
        {
            return;
        }

        if (e.Key == Key.A &&
            (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            StepsListBox.SelectAll();
            if (!IsDescendantOrSelf(focusedVisual, StepsListBox))
            {
                StepsListBox.Focus();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Meta) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            var inStepsPanel = IsDescendantOrSelf(sourceVisual, StepsPanel) ||
                               IsDescendantOrSelf(focusedVisual, StepsPanel);
            if (!inStepsPanel)
            {
                return;
            }

            var selectedSteps = StepsListBox.SelectedItems?
                .OfType<ChainStep>()
                .ToList() ?? [];

            if (selectedSteps.Count == 0 && ViewModel.SelectedStep is { } fallbackStep)
            {
                selectedSteps.Add(fallbackStep);
            }

            if (selectedSteps.Count > 0)
            {
                ViewModel.RemoveSteps(selectedSteps);
                if (ViewModel.HasSteps)
                {
                    StepsListBox.Focus();
                }

                e.Handled = true;
            }
        }
    }

    private static bool IsTextInputElement(Visual? visual)
    {
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (current is TextBox or SelectableTextBlock or NumericUpDown)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOrSelf(Visual? visual, Visual? ancestor)
    {
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
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
