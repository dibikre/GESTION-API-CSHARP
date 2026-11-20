using System;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(
        SingleRequestViewModel singleRequest,
        BatchViewModel batch,
        ChainViewModel chain,
        ExtractorViewModel extractor,
        IOverlayEditorService overlayEditorService,
        ICookieEditorService cookieEditorService,
        IClipboardService clipboardService)
    {
        SingleRequest = singleRequest ?? throw new ArgumentNullException(nameof(singleRequest));
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        Chain = chain ?? throw new ArgumentNullException(nameof(chain));
        Extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        ArgumentNullException.ThrowIfNull(overlayEditorService);
        ArgumentNullException.ThrowIfNull(cookieEditorService);
        ArgumentNullException.ThrowIfNull(clipboardService);

        Overlay = new OverlayEditorViewModel(clipboardService);
        overlayEditorService.OpenRequested += (_, request) => Overlay.Open(request);

        CookieEditor = new CookieEditorViewModel(clipboardService);
        cookieEditorService.OpenRequested += (_, request) => CookieEditor.Open(request);

        Batch.OpenInSingleRequestCallback = requestModel =>
        {
            SingleRequest.Request.LoadFrom(requestModel);
            SelectedTabIndex = 0;
        };

        SingleRequest.ExtractToLabCallback = OpenInExtractor;
        Batch.ExtractToLabCallback = OpenInExtractor;
        Chain.ExtractToLabCallback = OpenInExtractor;
        Extractor.SwitchToChainCallback = () => SelectedTabIndex = 2;
    }

    public SingleRequestViewModel SingleRequest { get; }
    public BatchViewModel Batch { get; }
    public ChainViewModel Chain { get; }
    public ExtractorViewModel Extractor { get; }
    public OverlayEditorViewModel Overlay { get; }
    public CookieEditorViewModel CookieEditor { get; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [RelayCommand]
    private void CloseActiveOverlay()
    {
        if (CookieEditor.IsOpen)
        {
            if (CookieEditor.CloseCommand.CanExecute(null))
            {
                CookieEditor.CloseCommand.Execute(null);
            }

            return;
        }

        if (Overlay.IsOpen && Overlay.CloseCommand.CanExecute(null))
        {
            Overlay.CloseCommand.Execute(null);
        }
    }

    private void OpenInExtractor(string body, string contentType)
    {
        Extractor.LoadContent(body, contentType);
        SelectedTabIndex = 3;
    }
}
