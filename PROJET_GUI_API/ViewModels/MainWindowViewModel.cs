using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(
        SingleRequestViewModel singleRequest,
        BatchViewModel batch,
        ChainViewModel chain)
    {
        SingleRequest = singleRequest ?? throw new ArgumentNullException(nameof(singleRequest));
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
        Chain = chain ?? throw new ArgumentNullException(nameof(chain));

        Batch.OpenInSingleRequestCallback = requestModel =>
        {
            SingleRequest.Request.LoadFrom(requestModel);
            SelectedTabIndex = 0;
        };
    }

    public SingleRequestViewModel SingleRequest { get; }
    public BatchViewModel Batch { get; }
    public ChainViewModel Chain { get; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }
}
