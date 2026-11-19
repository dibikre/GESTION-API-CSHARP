using System;

namespace ApiTester.Services;

public sealed record OverlayEditorRequest(
    string Title,
    string InitialText,
    bool IsReadOnly,
    Action<string>? OnSave);

public interface IOverlayEditorService
{
    event EventHandler<OverlayEditorRequest>? OpenRequested;

    void RequestOpen(OverlayEditorRequest request);
}
