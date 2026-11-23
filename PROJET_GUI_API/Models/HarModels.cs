using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class HarEntryItem : ObservableObject
{
    public int Index { get; init; }
    public string OriginalMethod { get; init; } = "GET";
    public HttpMethodType Method { get; init; } = HttpMethodType.Get;
    public string FullUrl { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string StepName { get; init; } = string.Empty;
    public int? ResponseStatus { get; init; }
    public string ResourceType { get; init; } = string.Empty;
    public bool IsStaticAsset { get; init; }
    public IReadOnlyList<KeyValueItem> QueryParams { get; init; } = [];
    public IReadOnlyList<KeyValueItem> Headers { get; init; } = [];
    public BodyType BodyType { get; init; } = BodyType.None;
    public string Body { get; init; } = string.Empty;
    public IReadOnlyList<KeyValueItem> FormFields { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    public string IndexLabel => $"#{Index}";
    public bool HasResponseStatus => ResponseStatus is > 0;
    public string StatusText => ResponseStatus is > 0 ? ResponseStatus.Value.ToString() : string.Empty;
    public bool IsSuccessStatus => ResponseStatus is >= 200 and < 300;
    public bool IsRedirectStatus => ResponseStatus is >= 300 and < 400;
    public bool IsErrorStatus => ResponseStatus is >= 400;
    public bool HasResourceType => !string.IsNullOrWhiteSpace(ResourceType);
    public bool HasQueryParams => QueryParams.Count > 0;
    public bool HasHeaders => Headers.Count > 0;
    public bool IsFormBody => BodyType == BodyType.FormUrlEncoded && FormFields.Count > 0;
    public bool HasTextBody => BodyType is BodyType.Json or BodyType.Text && !string.IsNullOrEmpty(Body);
    public bool HasBody => IsFormBody || HasTextBody;
    public bool HasWarnings => Warnings.Count > 0;

    public string QueryParamsHeaderText => $"Paramètres d'URL ({QueryParams.Count})";
    public string HeadersHeaderText => $"En-têtes ({Headers.Count})";

    public string BodyTypeLabel => BodyType switch
    {
        BodyType.Json => "JSON",
        BodyType.Text => "Texte",
        BodyType.FormUrlEncoded => "Formulaire",
        _ => "Aucun"
    };

    public string BodyHeaderText => BodyType == BodyType.None
        ? "Corps (aucun)"
        : $"Corps ({BodyTypeLabel})";

    public string DetailsSummaryText
    {
        get
        {
            var parts = new List<string>(3);
            if (QueryParams.Count > 0)
            {
                parts.Add($"{QueryParams.Count} param.");
            }

            if (Headers.Count > 0)
            {
                parts.Add($"{Headers.Count} {(Headers.Count <= 1 ? "en-tête" : "en-têtes")}");
            }

            if (BodyType != BodyType.None)
            {
                parts.Add(BodyTypeLabel);
            }

            return parts.Count == 0 ? "Requête simple" : string.Join(" · ", parts);
        }
    }

    public RequestModel ToRequestModel()
    {
        var model = new RequestModel
        {
            Method = Method,
            Url = BaseUrl,
            BodyType = BodyType,
            Body = Body
        };

        foreach (var param in QueryParams)
        {
            model.QueryParams.Add(new KeyValueItem(param.Key, param.Value, param.IsEnabled));
        }

        foreach (var header in Headers)
        {
            model.Headers.Add(new KeyValueItem(header.Key, header.Value, header.IsEnabled));
        }

        foreach (var field in FormFields)
        {
            model.FormFields.Add(new KeyValueItem(field.Key, field.Value, field.IsEnabled));
        }

        return model;
    }
}

public sealed class HarParseResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public string FileName { get; init; } = string.Empty;
    public IReadOnlyList<HarEntryItem> Entries { get; init; } = [];
    public int IgnoredNonHttpCount { get; init; }
    public int IgnoredInvalidCount { get; init; }

    public static HarParseResult Success(
        string fileName,
        IReadOnlyList<HarEntryItem> entries,
        int ignoredNonHttpCount,
        int ignoredInvalidCount) => new()
    {
        IsSuccess = true,
        FileName = fileName,
        Entries = entries,
        IgnoredNonHttpCount = ignoredNonHttpCount,
        IgnoredInvalidCount = ignoredInvalidCount
    };

    public static HarParseResult Failure(string fileName, string errorMessage) => new()
    {
        IsSuccess = false,
        FileName = fileName,
        ErrorMessage = errorMessage
    };
}
