using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ApiTester.Services;
using ApiTester.ViewModels;
using ApiTester.Views;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<IBatchRunner, BatchRunner>();
        services.AddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddSingleton<IBatchResultWriter, BatchResultWriter>();
        services.AddSingleton<SingleRequestViewModel>();
        services.AddSingleton<BatchViewModel>();
        services.AddSingleton<MainWindowViewModel>();
    }
}