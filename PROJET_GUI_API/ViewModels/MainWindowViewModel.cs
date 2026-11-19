using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(
        SingleRequestViewModel singleRequest,
        BatchViewModel batch,
        ChainViewModel chain,
        ExtractorViewModel extractor)
    {
        SingleRequest = singleRequest ?? throw new ArgumentNullException(nameof(singleRequest));
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        Chain = chain ?? throw new ArgumentNullException(nameof(chain));
        Extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));

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

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    private void OpenInExtractor(string body, string contentType)
    {
        Extractor.LoadContent(body, contentType);
        SelectedTabIndex = 3;
    }
}
