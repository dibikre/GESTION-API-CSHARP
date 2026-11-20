using ApiTester.Models;

namespace ApiTester.Services;

public sealed record ResponseRefinementResult(
    bool IsSuccess,
    string RefinedBody,
    string SummaryText,
    string? ErrorMessage)
{
    public static ResponseRefinementResult Success(string refinedBody, string summaryText) =>
        new(true, refinedBody, summaryText, null);

    public static ResponseRefinementResult Failure(string errorMessage) =>
        new(false, string.Empty, string.Empty, errorMessage);
}

public interface IResponseRefiner
{
    ResponseRefinementResult Refine(
        string rawBody,
        string formattedBody,
        string contentType,
        string expression,
        ResponseFilterMode mode);
}
