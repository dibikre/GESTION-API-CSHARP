using System.Collections.Generic;
using ApiTester.Models;

namespace ApiTester.Services;

public enum VariableLocation
{
    Url,
    QueryParams,
    Headers,
    Body,
    FormFields
}

public sealed record ExtractedVariable(
    string Name,
    IReadOnlyList<VariableLocation> Locations,
    string LocationsDisplay,
    bool IsList = false);

public sealed class TemplateApplyResult
{
    public bool IsSuccess { get; init; }
    public RequestModel? Request { get; init; }
    public bool IsValidJson { get; init; } = true;
    public string? ErrorMessage { get; init; }
}

public interface ITemplateEngine
{
    bool IsPredefinedVariable(string name);

    bool ContainsAnyMarker(RequestModel template);

    IReadOnlyList<string> GetUnknownPredefinedMarkers(RequestModel template);

    IReadOnlyList<ExtractedVariable> ExtractVariables(RequestModel template);

    IReadOnlyList<ExtractedVariable> ExtractListVariables(RequestModel template);

    IReadOnlySet<string> GetBareScalarVariables(RequestModel template);

    RequestModel Apply(RequestModel template, IReadOnlyDictionary<string, string> values, int index);

    TemplateApplyResult ApplyDetailed(RequestModel template, IReadOnlyDictionary<string, string> values, int index);

    TemplateApplyResult ApplyDetailed(
        RequestModel template,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? listValues,
        int index);

    TemplateApplyResult ApplyDetailed(
        RequestModel template,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? listValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? allLists,
        int index);

    bool IsValidJson(string? json);
}
