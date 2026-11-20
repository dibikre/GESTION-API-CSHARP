using System;

namespace ApiTester.Services;

public sealed record CookieEditorRequest(
    string InitialCookieHeader,
    Action<string>? OnSave);

public interface ICookieEditorService
{
    event EventHandler<CookieEditorRequest>? OpenRequested;

    void RequestOpen(CookieEditorRequest request);
}
