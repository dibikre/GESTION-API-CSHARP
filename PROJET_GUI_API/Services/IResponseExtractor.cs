using System.Collections.Generic;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed record ExtractionOutcome(
    bool IsSuccess,
    string Value,
    string? ErrorMessage)
{
    public static ExtractionOutcome Success(string value) => new(true, value, null);

    public static ExtractionOutcome Failure(string errorMessage) => new(false, string.Empty, errorMessage);
}

public sealed record ListExtractionOutcome(
    bool IsSuccess,
    IReadOnlyList<string> Values,
    string? ErrorMessage)
{
    public static ListExtractionOutcome Success(IReadOnlyList<string> values) => new(true, values, null);

    public static ListExtractionOutcome Failure(string errorMessage) => new(false, [], errorMessage);
}

public interface IResponseExtractor
{
    bool TryValidateVariableName(string? variableName, out string? errorMessage);

    /// <summary>
    /// Extrait une valeur depuis le corps (JSONPath simplifié incluant l'indexation directe sur tableau racine [index], ou Regex) ou un en-tête.
    /// </summary>
    ExtractionOutcome Extract(ResponseModel response, ChainExtraction extraction);

    /// <summary>
    /// Extrait toutes les valeurs correspondantes dans une liste (JSONPath avec [*] / [] ou tableau ciblé, ou Regex global).
    /// </summary>
    ListExtractionOutcome ExtractList(ResponseModel response, ChainExtraction extraction);

    /// <summary>
    /// Extrait la liste brute sans appliquer les conditions de filtrage ou de transformation.
    /// </summary>
    ListExtractionOutcome ExtractRawList(ResponseModel response, ChainExtraction extraction);
}
