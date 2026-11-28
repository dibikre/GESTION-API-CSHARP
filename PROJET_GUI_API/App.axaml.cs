using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ApiTester.Services;
using ApiTester.ViewModels;
using ApiTester.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace ApiTester;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public IServiceProvider? Services => _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "Exception non gérée sur le thread UI ({ErrorType}).",
                eventArgs.Exception.GetType().Name);

            SurfaceUnexpectedUiException(eventArgs.Exception);

            if (eventArgs.Exception is not OutOfMemoryException)
            {
                eventArgs.Handled = true;
            }
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => _serviceProvider?.Dispose();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        });

        services.AddSingleton<IHttpService, HttpService>();
        services.AddSingleton<ICurlParser, CurlParser>();
        services.AddSingleton<ITemplateEngine, TemplateEngine>();
        services.AddSingleton<IRateLimitDetector, RateLimitDetector>();
        services.AddSingleton<IResponseExtractor, ResponseExtractor>();
        services.AddSingleton<IResponseRefiner, ResponseRefiner>();
        services.AddSingleton<IExtractionSuggester, ExtractionSuggester>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IOverlayEditorService, OverlayEditorService>();
        services.AddSingleton<ICookieEditorService, CookieEditorService>();
        services.AddSingleton<IBatchRunner, BatchRunner>();
        services.AddSingleton<IChainRunner, ChainRunner>();
        services.AddSingleton<IStepReturnEvaluator, StepReturnEvaluator>();
        services.AddSingleton<IChainCliGenerator, ChainCliGenerator>();
        services.AddSingleton<IChainPipelineFileService, ChainPipelineFileService>();
        services.AddSingleton<IHarParserService, HarParserService>();
        services.AddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddSingleton<IBatchResultWriter, BatchResultWriter>();
        services.AddSingleton<SingleRequestViewModel>();
        services.AddSingleton<BatchViewModel>();
        services.AddSingleton<ChainViewModel>();
        services.AddSingleton<ExtractorViewModel>();
        services.AddSingleton<MainWindowViewModel>();
    }

    private void SurfaceUnexpectedUiException(Exception exception)
    {
        try
        {
            var mainVm = _serviceProvider?.GetService<MainWindowViewModel>();
            if (mainVm is null)
            {
                return;
            }

            var message = $"Erreur inattendue : {exception.Message}";
            switch (mainVm.SelectedTabIndex)
            {
                case 0:
                    mainVm.SingleRequest.Response.IsBusy = false;
                    mainVm.SingleRequest.Response.SetError(message);
                    break;
                case 1:
                    mainVm.Batch.ReportUnexpectedError(message);
                    break;
                case 2:
                    mainVm.Chain.ReportUnexpectedError(message);
                    break;
                default:
                    if (mainVm.Batch.IsRunning)
                    {
                        mainVm.Batch.ReportUnexpectedError(message);
                    }
                    else if (mainVm.Chain.IsRunning)
                    {
                        mainVm.Chain.ReportUnexpectedError(message);
                    }
                    break;
            }
        }
        catch (Exception notifyEx)
        {
            Log.Error(notifyEx, "Impossible d'afficher l'exception UI dans l'interface.");
        }
    }
}