using System.Collections.Generic;
using System.Threading;
using ApiTester.Models;

namespace ApiTester.Services;

public interface IExtractionSuggester
{
    ResponseFormat DetectFormat(string? text, ResponseFormat requestedFormat);

    FormatAnalysisResult Analyze(
        string? text,
        ResponseFormat requestedFormat,
        CancellationToken cancellationToken = default);

    string? SuggestJsonPath(ExtractorNode node);

    ExtractionSuggestion SuggestForNode(
        string rawText,
        ExtractorNode node,
        CancellationToken cancellationToken = default);

    ExtractionSuggestion SuggestManualRegex(
        string rawText,
        int selectionStart,
        int selectionLength,
        int contextLength,
        ExtractorValueKind valueKind,
        string? customPattern,
        bool useTrailingAnchor,
        CancellationToken cancellationToken = default);

    IReadOnlyList<RealtimeExtractionSuggestion> SuggestRealtime(
        ResponseModel response,
        ChainExtractionSource source,
        ExtractionKind kind,
        string? query,
        string? headerName = null,
        string? variableName = null,
        bool isList = false,
        int maxResults = 25);

    IReadOnlyList<RealtimeExtractionSuggestion> SuggestHeaderNames(
        ResponseModel response,
        string? query,
        int maxResults = 25);
}
