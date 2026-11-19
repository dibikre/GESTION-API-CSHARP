using ApiTester.Models;

namespace ApiTester.Services;

public interface IExtractionSuggester
{
    ResponseFormat DetectFormat(string? text, ResponseFormat requestedFormat);

    FormatAnalysisResult Analyze(string? text, ResponseFormat requestedFormat);

    string? SuggestJsonPath(ExtractorNode node);

    ExtractionSuggestion SuggestForNode(string rawText, ExtractorNode node);

    ExtractionSuggestion SuggestManualRegex(
        string rawText,
        int selectionStart,
        int selectionLength,
        int contextLength,
        ExtractorValueKind valueKind,
        string? customPattern,
        bool useTrailingAnchor);
}
