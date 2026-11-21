using System.Collections.Generic;
using ApiTester.Models;

namespace ApiTester.Services;

public interface IStepReturnEvaluator
{
    StepReturnParseResult Parse(string? returnExpression);

    StepReturnEvaluationResult Evaluate(
        string? returnExpression,
        IReadOnlyDictionary<string, string> scalarVariables,
        IReadOnlyDictionary<string, string>? activeListItemVariables,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? accumulatedLists,
        int globalIndex,
        string? requestGuid = null,
        long? requestTimestamp = null);
}
