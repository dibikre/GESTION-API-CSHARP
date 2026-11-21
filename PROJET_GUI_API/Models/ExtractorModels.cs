using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public enum ResponseFormat
{
    Auto,
    Json,
    Xml,
    Html,
    Text
}

public enum ExtractorValueKind
{
    Auto,
    Digits,
    NonWhitespace,
    UntilEndOfLine,
    Custom
}

public enum ExtractorNodeKind
{
    JsonObject,
    JsonArray,
    JsonValue,
    XmlElement,
    XmlAttribute
}

public enum LiveTestStatus
{
    None,
    Success,
    Warning,
    Error
}

public sealed partial class ExtractorNode : ObservableObject
{
    public string Label { get; init; } = string.Empty;

    public string KindTag { get; init; } = string.Empty;

    public ExtractorNodeKind NodeKind { get; init; }

    public string? JsonPath { get; init; }

    public string? HtmlPath { get; init; }

    public string? CssSelector { get; init; }

    public string? AnchorName { get; init; }

    public string? ElementTagName { get; init; }

    public string? AttributeName { get; init; }

    public string? IdAttribute { get; init; }

    public string? NameAttribute { get; init; }

    public string? PropertyAttribute { get; init; }

    public string? RelAttribute { get; init; }

    public JsonValueKind? JsonKind { get; init; }

    public string? RawScalarValue { get; init; }

    public bool IsLeaf { get; init; }

    public bool IsSelectable { get; init; }

    public ObservableCollection<ExtractorNode> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed record FormatAnalysisResult(
    ResponseFormat DetectedFormat,
    bool FallbackToText,
    string? StatusMessage,
    ObservableCollection<ExtractorNode> RootNodes);

public sealed record ExtractionSuggestion(
    string? JsonPath,
    string? Regex,
    int OccurrenceCount,
    string? OccurrenceWarning,
    string? InfoMessage,
    string? HtmlPath = null);

public sealed record RealtimeExtractionSuggestion(
    string Expression,
    string SuggestedVariableName,
    string PreviewValue,
    string BadgeText,
    string DetailText,
    bool SuggestsList,
    int MatchCount = 1,
    string? TargetHeaderName = null)
{
    public bool HasPreviewValue => !string.IsNullOrWhiteSpace(PreviewValue);

    public bool HasDetailText => !string.IsNullOrWhiteSpace(DetailText);
}

