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

public interface IResponseExtractor
{
    bool TryValidateVariableName(string? variableName, out string? errorMessage);

    /// <summary>
    /// Extrait une valeur depuis le corps (JSONPath simplifié incluant l'indexation directe sur tableau racine [index], ou Regex) ou un en-tête.
    /// </summary>
    ExtractionOutcome Extract(ResponseModel response, ChainExtraction extraction);
}
