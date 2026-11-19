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

    ExtractionOutcome Extract(ResponseModel response, ChainExtraction extraction);
}
