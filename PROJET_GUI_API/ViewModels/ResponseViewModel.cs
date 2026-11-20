using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using ApiTester.Models;
using ApiTester.Services;
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
    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        MaxDepth = 1024
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 1024
    };

    private readonly IOverlayEditorService? _overlayEditor;
    private readonly IResponseRefiner _responseRefiner;
    private string _formattedFullBody = string.Empty;

    public ResponseViewModel(
        IOverlayEditorService? overlayEditor = null,
        IResponseRefiner? responseRefiner = null)
    {
        _overlayEditor = overlayEditor;
        _responseRefiner = responseRefiner ?? new ResponseRefiner();
    }

    public static IReadOnlyList<ResponseFilterMode> FilterModes { get; } = Enum.GetValues<ResponseFilterMode>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestExtractCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnlargeBodyCommand))]
    [NotifyPropertyChangedFor(nameof(IsFilterBannerVisible))]
    [NotifyPropertyChangedFor(nameof(HasRow1Banner))]
    public partial bool HasResponse { get; set; }

    public event EventHandler<ExtractRequestedEventArgs>? ExtractRequested;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterBannerVisible))]
    [NotifyPropertyChangedFor(nameof(HasRow1Banner))]
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
    [NotifyPropertyChangedFor(nameof(HasRow1Banner))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial StatusCategory StatusCategory { get; set; } = StatusCategory.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    [NotifyPropertyChangedFor(nameof(IsFilterBannerVisible))]
    [NotifyPropertyChangedFor(nameof(HasRow1Banner))]
    [NotifyPropertyChangedFor(nameof(HasFilterStatus))]
    [NotifyPropertyChangedFor(nameof(HasFilterError))]
    [NotifyCanExecuteChangedFor(nameof(ClearFilterCommand))]
    public partial string FilterExpression { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ResponseFilterMode FilterMode { get; set; } = ResponseFilterMode.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleRawFilterButtonText))]
    public partial bool ShowUnfilteredBody { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterStatus))]
    public partial string? FilterStatusText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterError))]
    public partial string? FilterErrorText { get; private set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsFilterActive => !string.IsNullOrWhiteSpace(FilterExpression);

    public bool IsFilterBannerVisible => HasResponse && StatusCode > 0 && IsFilterActive;

    public bool HasRow1Banner => HasError || IsFilterBannerVisible;

    public bool HasFilterStatus => IsFilterActive && !string.IsNullOrWhiteSpace(FilterStatusText);

    public bool HasFilterError => IsFilterActive && !string.IsNullOrWhiteSpace(FilterErrorText);

    public string ToggleRawFilterButtonText => ShowUnfilteredBody ? "Voir affiné" : "Voir brut";

    partial void OnFilterExpressionChanged(string value)
    {
        if (ShowUnfilteredBody)
        {
            ShowUnfilteredBody = false;
        }

        RefreshDisplayedBody();
    }

    partial void OnFilterModeChanged(ResponseFilterMode value)
    {
        if (ShowUnfilteredBody)
        {
            ShowUnfilteredBody = false;
        }

        RefreshDisplayedBody();
    }

    partial void OnShowUnfilteredBodyChanged(bool value)
    {
        RefreshDisplayedBody();
    }

    [RelayCommand(CanExecute = nameof(IsFilterActive))]
    private void ClearFilter()
    {
        FilterExpression = string.Empty;
        ShowUnfilteredBody = false;
    }

    [RelayCommand]
    private void ToggleUnfilteredBody()
    {
        ShowUnfilteredBody = !ShowUnfilteredBody;
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        ShowUnfilteredBody = false;
        RefreshDisplayedBody();
    }

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

    [RelayCommand(CanExecute = nameof(HasResponse))]
    private void EnlargeBody()
    {
        if (!HasResponse)
        {
            return;
        }

        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            "Corps de la réponse",
            Body,
            IsReadOnly: true,
            OnSave: null));
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
        _formattedFullBody = FormatBody(response.Body, response.ContentType);
        ErrorMessage = response.ErrorMessage;
        StatusCategory = DetermineStatusCategory(response);
        RefreshDisplayedBody();
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
        _formattedFullBody = string.Empty;
        ErrorMessage = errorMessage;
        StatusCategory = StatusCategory.NetworkError;
        RefreshDisplayedBody();
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
        _formattedFullBody = string.Empty;
        ErrorMessage = null;
        StatusCategory = StatusCategory.None;
        RefreshDisplayedBody();
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
        _formattedFullBody = string.Empty;
        ErrorMessage = null;
        FilterStatusText = null;
        FilterErrorText = null;
        StatusCategory = StatusCategory.None;
    }

    private void RefreshDisplayedBody()
    {
        if (!HasResponse || StatusCode <= 0 || !IsFilterActive)
        {
            FilterStatusText = null;
            FilterErrorText = null;
            Body = _formattedFullBody;
            return;
        }

        var outcome = _responseRefiner.Refine(
            RawBody,
            _formattedFullBody,
            ContentType,
            FilterExpression,
            FilterMode);

        if (outcome.IsSuccess)
        {
            FilterStatusText = outcome.SummaryText;
            FilterErrorText = null;
            Body = ShowUnfilteredBody ? _formattedFullBody : outcome.RefinedBody;
        }
        else
        {
            FilterStatusText = null;
            FilterErrorText = outcome.ErrorMessage;
            Body = ShowUnfilteredBody ? _formattedFullBody : string.Empty;
        }
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
            using var document = JsonDocument.Parse(rawBody.TrimStart('\uFEFF'), JsonParseOptions);
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
