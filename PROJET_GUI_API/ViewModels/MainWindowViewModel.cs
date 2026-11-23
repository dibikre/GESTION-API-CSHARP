using System;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.Services;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IHarParserService _harParserService;

    public MainWindowViewModel(
        SingleRequestViewModel singleRequest,
        BatchViewModel batch,
        ChainViewModel chain,
        ExtractorViewModel extractor,
        IOverlayEditorService overlayEditorService,
        ICookieEditorService cookieEditorService,
        IClipboardService clipboardService,
        IHarParserService? harParserService = null)
    {
        SingleRequest = singleRequest ?? throw new ArgumentNullException(nameof(singleRequest));
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        Chain = chain ?? throw new ArgumentNullException(nameof(chain));
        Extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        ArgumentNullException.ThrowIfNull(overlayEditorService);
        ArgumentNullException.ThrowIfNull(cookieEditorService);
        ArgumentNullException.ThrowIfNull(clipboardService);
        _harParserService = harParserService ?? new HarParserService();

        Overlay = new OverlayEditorViewModel(clipboardService);
        overlayEditorService.OpenRequested += (_, request) => Overlay.Open(request);

        CookieEditor = new CookieEditorViewModel(clipboardService);
        cookieEditorService.OpenRequested += (_, request) => CookieEditor.Open(request);

        HarImport = new HarImportViewModel();

        Batch.OpenInSingleRequestCallback = requestModel =>
        {
            SingleRequest.Request.LoadFrom(requestModel);
            SelectedTabIndex = 0;
        };

        SingleRequest.ExtractToLabCallback = OpenInExtractor;
        Batch.ExtractToLabCallback = OpenInExtractor;
        Chain.ExtractToLabCallback = OpenInExtractor;
        Chain.ImportHarCallback = () => _ = BrowseAndImportHarAsync();
        Extractor.SwitchToChainCallback = () => SelectedTabIndex = 2;
    }

    public SingleRequestViewModel SingleRequest { get; }
    public BatchViewModel Batch { get; }
    public ChainViewModel Chain { get; }
    public ExtractorViewModel Extractor { get; }
    public OverlayEditorViewModel Overlay { get; }
    public CookieEditorViewModel CookieEditor { get; }
    public HarImportViewModel HarImport { get; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    public async Task BrowseAndImportHarAsync()
    {
        var file = await _harParserService.PickHarFileAsync();
        if (file is null)
        {
            return;
        }

        await ImportHarStorageFileAsync(file);
    }

    public async Task ImportHarStorageFileAsync(IStorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = await _harParserService.ParseFileAsync(file);
        OpenHarImportResult(result);
    }

    public void ShowHarImportError(string fileName, string errorMessage)
    {
        OpenHarImportResult(HarParseResult.Failure(fileName, errorMessage));
    }

    private void OpenHarImportResult(HarParseResult result)
    {
        HarImport.Open(result, (selectedEntries, replaceExisting) =>
        {
            var added = Chain.ImportHarEntries(selectedEntries, replaceExisting);
            if (added > 0)
            {
                SelectedTabIndex = 2;
            }
        });
    }

    [RelayCommand]
    private void CloseActiveOverlay()
    {
        if (HarImport.IsOpen)
        {
            if (HarImport.CloseCommand.CanExecute(null))
            {
                HarImport.CloseCommand.Execute(null);
            }

            return;
        }

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
