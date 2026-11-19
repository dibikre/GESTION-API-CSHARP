using System;

namespace ApiTester.Services;

public sealed class OverlayEditorService : IOverlayEditorService
{
    public event EventHandler<OverlayEditorRequest>? OpenRequested;

    public void RequestOpen(OverlayEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OpenRequested?.Invoke(this, request);
    }
}
