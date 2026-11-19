using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Serilog;

namespace ApiTester.Services;

public sealed class ClipboardService : IClipboardService
{
    public async Task<bool> SetTextAsync(string text)
    {
        try
        {
            var topLevel = GetMainTopLevel();
            var clipboard = topLevel?.Clipboard;
            if (clipboard is null)
            {
                return false;
            }

            await clipboard.SetTextAsync(text ?? string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossible de copier le texte dans le presse-papiers ({ErrorType}).", ex.GetType().Name);
            return false;
        }
    }

    private static TopLevel? GetMainTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } mainWindow)
        {
            return TopLevel.GetTopLevel(mainWindow);
        }

        return null;
    }
}
