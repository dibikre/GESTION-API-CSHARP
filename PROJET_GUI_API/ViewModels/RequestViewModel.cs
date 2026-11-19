using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class RequestViewModel : ViewModelBase
{
    private readonly IOverlayEditorService? _overlayEditor;

    public RequestViewModel(IOverlayEditorService? overlayEditor = null)
    {
        _overlayEditor = overlayEditor;
    }

    public static IReadOnlyList<HttpMethodType> Methods { get; } = Enum.GetValues<HttpMethodType>();
    public static IReadOnlyList<BodyType> BodyTypes { get; } = Enum.GetValues<BodyType>();
    public static IReadOnlyList<AuthType> AuthTypes { get; } = Enum.GetValues<AuthType>();

    [ObservableProperty]
    public partial HttpMethodType Method { get; set; } = HttpMethodType.Get;

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJsonBody))]
    [NotifyPropertyChangedFor(nameof(IsTextBody))]
    [NotifyPropertyChangedFor(nameof(IsFormBody))]
    [NotifyPropertyChangedFor(nameof(CanEnlargeBody))]
    public partial BodyType BodyType { get; set; } = BodyType.None;

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int TimeoutSeconds { get; set; } = 30;

    public ObservableCollection<KeyValueItem> QueryParams { get; } = [];
    public ObservableCollection<KeyValueItem> Headers { get; } = [];
    public ObservableCollection<KeyValueItem> FormFields { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBearerAuth))]
    [NotifyPropertyChangedFor(nameof(IsBasicAuth))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyAuth))]
    public partial AuthType AuthType { get; set; } = AuthType.None;

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeyName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeyValue { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ApiKeyInHeader { get; set; } = true;

    public bool IsBearerAuth => AuthType == AuthType.Bearer;
    public bool IsBasicAuth => AuthType == AuthType.Basic;
    public bool IsApiKeyAuth => AuthType == AuthType.ApiKey;

    public bool IsJsonBody => BodyType == BodyType.Json;
    public bool IsTextBody => BodyType == BodyType.Text;
    public bool IsFormBody => BodyType == BodyType.FormUrlEncoded;
    public bool CanEnlargeBody => BodyType is BodyType.Json or BodyType.Text;

    [RelayCommand]
    private void EnlargeBody()
    {
        _overlayEditor?.RequestOpen(new OverlayEditorRequest(
            "Corps de la requête",
            Body,
            IsReadOnly: false,
            OnSave: text => Body = text));
    }

    [RelayCommand]
    private void AddQueryParam()
    {
        QueryParams.Add(new KeyValueItem());
    }

    [RelayCommand]
    private void RemoveQueryParam(KeyValueItem? item)
    {
        if (item is not null)
        {
            QueryParams.Remove(item);
        }
    }

    [RelayCommand]
    private void AddHeader()
    {
        Headers.Add(new KeyValueItem());
    }

    [RelayCommand]
    private void RemoveHeader(KeyValueItem? item)
    {
        if (item is not null)
        {
            Headers.Remove(item);
        }
    }

    [RelayCommand]
    private void AddFormField()
    {
        FormFields.Add(new KeyValueItem());
    }

    [RelayCommand]
    private void RemoveFormField(KeyValueItem? item)
    {
        if (item is not null)
        {
            FormFields.Remove(item);
        }
    }

    public RequestModel ToModel()
    {
        return new RequestModel
        {
            Method = Method,
            Url = Url,
            QueryParams = QueryParams.Select(x => new KeyValueItem(x.Key, x.Value, x.IsEnabled)).ToList(),
            Headers = Headers.Select(x => new KeyValueItem(x.Key, x.Value, x.IsEnabled)).ToList(),
            BodyType = BodyType,
            Body = Body,
            FormFields = FormFields.Select(x => new KeyValueItem(x.Key, x.Value, x.IsEnabled)).ToList(),
            Auth = new AuthConfig
            {
                Type = AuthType,
                Token = Token,
                Username = Username,
                Password = Password,
                ApiKeyName = ApiKeyName,
                ApiKeyValue = ApiKeyValue,
                ApiKeyInHeader = ApiKeyInHeader
            },
            TimeoutSeconds = TimeoutSeconds
        };
    }

    public void LoadFrom(RequestModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Method = model.Method;
        Url = model.Url;
        BodyType = model.BodyType;
        Body = model.Body;
        TimeoutSeconds = model.TimeoutSeconds;

        ReplaceItems(QueryParams, model.QueryParams);
        ReplaceItems(Headers, model.Headers);
        ReplaceItems(FormFields, model.FormFields);

        AuthType = model.Auth.Type;
        Token = model.Auth.Token;
        Username = model.Auth.Username;
        Password = model.Auth.Password;
        ApiKeyName = model.Auth.ApiKeyName;
        ApiKeyValue = model.Auth.ApiKeyValue;
        ApiKeyInHeader = model.Auth.ApiKeyInHeader;
    }

    private static void ReplaceItems(ObservableCollection<KeyValueItem> target, IEnumerable<KeyValueItem> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(new KeyValueItem(item.Key, item.Value, item.IsEnabled));
        }
    }
}
