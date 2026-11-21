using System.Collections.Generic;

namespace ApiTester.Models;

public abstract record StepReturnNode;

public sealed record IdentifierReturnNode(
    string Name,
    bool ForceScalar,
    bool ForceList) : StepReturnNode;

public sealed record StringLiteralReturnNode(
    string Value,
    bool HasTemplateMarkers) : StepReturnNode;

public sealed record NumberReturnNode(string RawText) : StepReturnNode;

public sealed record BooleanReturnNode(bool Value) : StepReturnNode;

public sealed record NullReturnNode : StepReturnNode;

public sealed record ArrayReturnNode(IReadOnlyList<StepReturnNode> Items) : StepReturnNode;

public sealed record ObjectReturnEntry(string Key, StepReturnNode Value);

public sealed record ObjectReturnNode(IReadOnlyList<ObjectReturnEntry> Entries) : StepReturnNode;

public sealed record ConcatReturnNode(IReadOnlyList<StepReturnNode> Parts) : StepReturnNode;

public sealed record StepReturnDependencies(
    IReadOnlyList<string> RequiredScalars,
    IReadOnlyList<string> RequiredLists,
    IReadOnlyList<string> AmbiguousIdentifiers);

public sealed record StepReturnParseResult(
    bool IsSuccess,
    StepReturnNode? Root,
    string? ErrorMessage,
    StepReturnDependencies Dependencies);

public sealed record StepReturnEvaluationResult(
    bool IsSuccess,
    string FormattedValue,
    bool IsJson,
    string? ErrorMessage);
