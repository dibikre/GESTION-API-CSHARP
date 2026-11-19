using System;
using System.Threading.Tasks;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class OverlayEditorViewModel : ViewModelBase
{
    private readonly IClipboardService? _clipboardService;
    private Action<string>? _onSave;

    public OverlayEditorViewModel(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAndCloseCommand))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    public void Open(OverlayEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Title = request.Title ?? string.Empty;
        Text = request.InitialText ?? string.Empty;
        IsReadOnly = request.IsReadOnly;
        _onSave = request.IsReadOnly ? null : request.OnSave;
        IsOpen = true;
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private void Close()
    {
        IsOpen = false;
        _onSave = null;
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private void SaveAndClose()
    {
        if (IsReadOnly)
        {
            Close();
            return;
        }

        var callback = _onSave;
        var currentText = Text ?? string.Empty;
        IsOpen = false;
        _onSave = null;
        callback?.Invoke(currentText);
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_clipboardService is null)
        {
            return;
        }

        await _clipboardService.SetTextAsync(Text ?? string.Empty);
    }
}
