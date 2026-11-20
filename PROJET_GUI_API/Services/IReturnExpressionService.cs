using System.Collections.Generic;

namespace ApiTester.Services;

public enum ReturnAstKind
{
    ScalarVariable,
    ListVariable,
    AutoVariable,
    LiteralString,
    TemplateString,
    LiteralNumber,
    LiteralBool,
    LiteralNull,
    Array,
    Object
}

public enum ReturnRefRequirement
{
    ScalarOnly,
    ListOnly,
    ScalarOrList
}

public sealed record ReturnObjectEntry(string Key, ReturnAstNode Value);

public sealed record ReturnAstNode(
    ReturnAstKind Kind,
    string? TextValue = null,
    bool? BoolValue = null,
    IReadOnlyList<ReturnAstNode>? Elements = null,
    IReadOnlyList<ReturnObjectEntry>? Properties = null);

public sealed record ReturnVariableRef(string Name, ReturnRefRequirement Requirement);

public sealed record ReturnEvaluationResult(
    bool IsSuccess,
    string OutputText,
    bool IsJson,
    string? ErrorMessage = null);

public interface IReturnExpressionService
{
    bool IsReturnStatement(string? text);

    bool TryParse(string? rawExpression, out ReturnAstNode? ast, out string? errorMessage);

    IReadOnlyList<ReturnVariableRef> CollectVariableReferences(ReturnAstNode ast);

    IReadOnlyList<string> ValidateReferences(
        ReturnAstNode ast,
        IReadOnlySet<string> availableScalars,
        IReadOnlySet<string> availableLists);

    ReturnEvaluationResult Evaluate(
        ReturnAstNode ast,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lists,
        int globalIndex,
        string? guid = null,
        string? timestamp = null);
}
