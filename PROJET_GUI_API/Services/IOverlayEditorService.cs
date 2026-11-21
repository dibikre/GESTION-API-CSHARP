using System;
using System.Collections.Generic;
using ApiTester.Models;

namespace ApiTester.Services;

public sealed record ExtractionConditionsOverlayRequest(
    string VariableName,
    IReadOnlyList<string> RawItems,
    IReadOnlyList<ChainExtractionCondition> InitialConditions,
    ExtractionConditionMatchMode InitialMatchMode,
    bool InitialDeduplicate,
    Action<IReadOnlyList<ChainExtractionCondition>, ExtractionConditionMatchMode, bool> OnApply);

public sealed record OverlayEditorRequest(
    string Title,
    string InitialText,
    bool IsReadOnly,
    Action<string>? OnSave,
    ExtractionConditionsOverlayRequest? ExtractionConditions = null);

public interface IOverlayEditorService
{
    event EventHandler<OverlayEditorRequest>? OpenRequested;

    void RequestOpen(OverlayEditorRequest request);
}
