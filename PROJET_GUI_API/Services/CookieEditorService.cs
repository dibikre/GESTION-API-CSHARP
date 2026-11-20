using System;

namespace ApiTester.Services;

public sealed class CookieEditorService : ICookieEditorService
{
    public event EventHandler<CookieEditorRequest>? OpenRequested;

    public void RequestOpen(CookieEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OpenRequested?.Invoke(this, request);
    }
}
