using System;
using System.ComponentModel;
using System.Linq;
using ApiTester.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;

namespace ApiTester.Views;

public partial class MainWindow : Window
{
    private OverlayEditorViewModel? _subscribedOverlay;

    public MainWindow()
    {
        InitializeComponent();
        OverlayEditorTextBox.PropertyChanged += OnOverlayEditorTextBoxPropertyChanged;

        RootDropGrid.AddHandler(DragDrop.DragEnterEvent, OnRootDragEnter, RoutingStrategies.Tunnel);
        RootDropGrid.AddHandler(DragDrop.DragOverEvent, OnRootDragOver, RoutingStrategies.Tunnel);
        RootDropGrid.AddHandler(DragDrop.DragLeaveEvent, OnRootDragLeave, RoutingStrategies.Tunnel);
        RootDropGrid.AddHandler(DragDrop.DropEvent, OnRootDrop, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedOverlay is not null)
        {
            _subscribedOverlay.PropertyChanged -= OnOverlayPropertyChanged;
            _subscribedOverlay = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            _subscribedOverlay = vm.Overlay;
            _subscribedOverlay.PropertyChanged += OnOverlayPropertyChanged;
        }
    }

    private void OnRootDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDropVisual(e);
    }

    private void OnRootDragOver(object? sender, DragEventArgs e)
    {
        UpdateDropVisual(e);
    }

    private void OnRootDragLeave(object? sender, DragEventArgs e)
    {
        var pos = e.GetPosition(RootDropGrid);
        if (pos.X <= 2 ||
            pos.Y <= 2 ||
            pos.X >= RootDropGrid.Bounds.Width - 2 ||
            pos.Y >= RootDropGrid.Bounds.Height - 2)
        {
            HarDropHintOverlay.IsVisible = false;
        }
    }

    private void UpdateDropVisual(DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            HarDropHintOverlay.IsVisible = true;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            HarDropHintOverlay.IsVisible = false;
        }
    }

    private async void OnRootDrop(object? sender, DragEventArgs e)
    {
        HarDropHintOverlay.IsVisible = false;

        if (DataContext is not MainWindowViewModel vm ||
            !e.DataTransfer.Contains(DataFormat.File))
        {
            return;
        }

        e.Handled = true;

        try
        {
            var items = e.DataTransfer.TryGetFiles();
            if (items is null || items.Length == 0)
            {
                return;
            }

            var files = items.OfType<IStorageFile>().ToList();
            if (files.Count == 0)
            {
                vm.ShowHarImportError("Dossier", "Dépose un fichier .har (les dossiers ne sont pas pris en charge).");
                return;
            }

            var harFile = files.FirstOrDefault(f => f.Name.EndsWith(".har", StringComparison.OrdinalIgnoreCase))
                          ?? files.FirstOrDefault(f => f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

            if (harFile is null)
            {
                var firstFile = files[0];
                vm.ShowHarImportError(
                    firstFile.Name,
                    $"Le fichier « {firstFile.Name} » n'est pas un fichier .har (HTTP Archive).");
                return;
            }

            await vm.ImportHarStorageFileAsync(harFile);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erreur lors du glisser-déposer du fichier HAR ({ErrorType}).", ex.GetType().Name);
            vm.ShowHarImportError("archive.har", $"Impossible de charger le fichier déposé : {ex.Message}");
        }
    }

    private void OnOverlayEditorTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_subscribedOverlay is not { IsOpen: true, HasConditionsSupport: true })
        {
            return;
        }

        if (e.Property == TextBox.SelectionStartProperty ||
            e.Property == TextBox.SelectionEndProperty ||
            e.Property == TextBox.TextProperty)
        {
            _subscribedOverlay.UpdateConditionsSelection(
                OverlayEditorTextBox.Text,
                OverlayEditorTextBox.SelectionStart,
                OverlayEditorTextBox.SelectionEnd);
        }
    }

    private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_subscribedOverlay is null)
        {
            return;
        }

        if (e.PropertyName is nameof(OverlayEditorViewModel.IsOpen) or nameof(OverlayEditorViewModel.IsConditionsOpen))
        {
            UpdateOverlaySplitLayout();
        }

        if (e.PropertyName == nameof(OverlayEditorViewModel.IsOpen) &&
            _subscribedOverlay.IsOpen)
        {
            Dispatcher.UIThread.Post(() => OverlayEditorTextBox.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void UpdateOverlaySplitLayout()
    {
        if (_subscribedOverlay is { IsOpen: true, IsConditionsOpen: true })
        {
            OverlayBodyGrid.ColumnDefinitions = ColumnDefinitions.Parse("1.05*,Auto,0.95*");
            OverlayBodyGrid.ColumnDefinitions[0].MinWidth = 240;
            OverlayBodyGrid.ColumnDefinitions[2].MinWidth = 260;
        }
        else
        {
            OverlayBodyGrid.ColumnDefinitions = ColumnDefinitions.Parse("*,0,0");
            OverlayBodyGrid.ColumnDefinitions[0].MinWidth = 0;
            OverlayBodyGrid.ColumnDefinitions[2].MinWidth = 0;
        }
    }

    private void OnOverlayScrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.Overlay.CloseCommand.CanExecute(null))
        {
            vm.Overlay.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCookieScrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.CookieEditor.CloseCommand.CanExecute(null))
        {
            vm.CookieEditor.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnHarImportScrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.HarImport.CloseCommand.CanExecute(null))
        {
            vm.HarImport.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnOverlayCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}