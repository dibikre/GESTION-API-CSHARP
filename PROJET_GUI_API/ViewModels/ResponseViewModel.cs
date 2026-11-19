using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using ApiTester.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public enum StatusCategory
{
    None,
    Success,
    Redirect,
    ClientError,
    ServerError,
    NetworkError
}

public partial class ResponseViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestExtractCommand))]
    public partial bool HasResponse { get; set; }

    public event EventHandler<ExtractRequestedEventArgs>? ExtractRequested;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int StatusCode { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long DurationMs { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string ContentType { get; set; } = string.Empty;

    public ObservableCollection<KeyValueItem> Headers { get; } = [];

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RawBody { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial StatusCategory StatusCategory { get; set; } = StatusCategory.None;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [RelayCommand(CanExecute = nameof(HasResponse))]
    private void RequestExtract()
    {
        if (!HasResponse)
        {
            return;
        }

        var content = string.IsNullOrEmpty(RawBody) ? Body : RawBody;
        ExtractRequested?.Invoke(this, new ExtractRequestedEventArgs(content, ContentType));
    }

    public void Update(ResponseModel response)
    {
        ArgumentNullException.ThrowIfNull(response);

        HasResponse = true;
        StatusCode = response.StatusCode;
        StatusText = response.ReasonPhrase;
        DurationMs = response.DurationMs;
        SizeText = FormatSize(response.SizeBytes);
        ContentType = response.ContentType;

        Headers.Clear();
        foreach (var header in response.Headers)
        {
            Headers.Add(new KeyValueItem(header.Key, header.Value, header.IsEnabled));
        }

        RawBody = response.Body;
        Body = FormatBody(response.Body, response.ContentType);
        ErrorMessage = response.ErrorMessage;
        StatusCategory = DetermineStatusCategory(response);
    }

    public void SetError(string errorMessage)
    {
        HasResponse = true;
        StatusCode = 0;
        StatusText = string.Empty;
        DurationMs = 0;
        SizeText = "0 B";
        ContentType = string.Empty;
        Headers.Clear();
        RawBody = string.Empty;
        Body = string.Empty;
        ErrorMessage = errorMessage;
        StatusCategory = StatusCategory.NetworkError;
    }

    public void SetCancelled(string message = "Requête annulée", long durationMs = 0)
    {
        HasResponse = true;
        StatusCode = 0;
        StatusText = message;
        DurationMs = durationMs;
        SizeText = "0 B";
        ContentType = string.Empty;
        Headers.Clear();
        RawBody = string.Empty;
        Body = string.Empty;
        ErrorMessage = null;
        StatusCategory = StatusCategory.None;
    }

    public void Clear()
    {
        HasResponse = false;
        IsBusy = false;
        StatusCode = 0;
        StatusText = string.Empty;
        DurationMs = 0;
        SizeText = "0 B";
        ContentType = string.Empty;
        Headers.Clear();
        Body = string.Empty;
        RawBody = string.Empty;
        ErrorMessage = null;
        StatusCategory = StatusCategory.None;
    }

    private static StatusCategory DetermineStatusCategory(ResponseModel response)
    {
        if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
        {
            return StatusCategory.NetworkError;
        }

        return response.StatusCode switch
        {
            >= 200 and < 300 => StatusCategory.Success,
            >= 300 and < 400 => StatusCategory.Redirect,
            >= 400 and < 500 => StatusCategory.ClientError,
            >= 500 and < 600 => StatusCategory.ServerError,
            _ => StatusCategory.None
        };
    }

    private static string FormatBody(string rawBody, string contentType)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return rawBody ?? string.Empty;
        }

        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return rawBody;
        }

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            return JsonSerializer.Serialize(document.RootElement, IndentedJsonOptions);
        }
        catch (JsonException)
        {
            return rawBody;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        if (bytes < kb)
        {
            return $"{bytes} B";
        }

        if (bytes < mb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / kb:0.##} KB");
        }

        if (bytes < gb)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / mb:0.##} MB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes / gb:0.##} GB");
    }
}

public sealed class ExtractRequestedEventArgs : EventArgs
{
    public ExtractRequestedEventArgs(string body, string contentType)
    {
        Body = body ?? string.Empty;
        ContentType = contentType ?? string.Empty;
    }

    public string Body { get; }
    public string ContentType { get; }
}
