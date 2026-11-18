namespace ApiTester.Models;

public sealed class BatchPreviewItem
{
    public int Index { get; init; }
    public string VariablesSummary { get; init; } = string.Empty;
    public HttpMethodType Method { get; init; }
    public string FinalUrl { get; init; } = string.Empty;
    public string HeadersText { get; init; } = string.Empty;
    public bool HasHeaders { get; init; }
    public string BodyText { get; init; } = string.Empty;
    public bool HasBody { get; init; }
    public bool IsInvalidJson { get; init; }
    public string? ErrorMessage { get; init; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
}
