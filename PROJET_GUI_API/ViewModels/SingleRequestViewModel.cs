using System;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class SingleRequestViewModel : ViewModelBase
{
    private readonly IHttpService _httpService;

    public SingleRequestViewModel(IHttpService httpService)
    {
        _httpService = httpService ?? throw new ArgumentNullException(nameof(httpService));
    }

    public RequestViewModel Request { get; } = new();
    public ResponseViewModel Response { get; } = new();

    public event EventHandler<RequestCompletedEventArgs>? RequestCompleted;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken ct)
    {
        var rawUrl = Request.Url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            Response.SetError("Veuillez saisir une URL.");
            return;
        }

        if (!rawUrl.Contains("://", StringComparison.Ordinal))
        {
            rawUrl = (IsLocalHost(rawUrl) ? "http://" : "https://") + rawUrl;
            Request.Url = rawUrl;
        }
        else
        {
            Request.Url = rawUrl;
        }

        var requestModel = Request.ToModel();
        Response.IsBusy = true;

        try
        {
            var responseModel = await _httpService.SendAsync(requestModel, ct);

            if (ct.IsCancellationRequested)
            {
                Response.SetCancelled("Requête annulée", responseModel.DurationMs);
                return;
            }

            Response.Update(responseModel);

            if (string.IsNullOrEmpty(responseModel.ErrorMessage))
            {
                await OnRequestCompletedAsync(requestModel, responseModel);
            }
        }
        catch (OperationCanceledException)
        {
            Response.SetCancelled("Requête annulée");
        }
        finally
        {
            Response.IsBusy = false;
        }
    }

    protected virtual Task OnRequestCompletedAsync(RequestModel request, ResponseModel response)
    {
        RequestCompleted?.Invoke(this, new RequestCompletedEventArgs(request, response));
        return Task.CompletedTask;
    }

    private static bool IsLocalHost(string url)
    {
        return StartsWithHost(url, "localhost") || StartsWithHost(url, "127.0.0.1");
    }

    private static bool StartsWithHost(string url, string host)
    {
        if (!url.StartsWith(host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (url.Length == host.Length)
        {
            return true;
        }

        var nextChar = url[host.Length];
        return nextChar is ':' or '/' or '?';
    }
}

public sealed class RequestCompletedEventArgs : EventArgs
{
    public RequestCompletedEventArgs(RequestModel request, ResponseModel response)
    {
        Request = request;
        Response = response;
    }

    public RequestModel Request { get; }
    public ResponseModel Response { get; }
}
