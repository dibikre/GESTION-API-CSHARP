using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Serilog;

namespace ApiTester.Services;

public sealed class FolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string? startFolder, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            var topLevel = GetMainTopLevel();
            var storageProvider = topLevel?.StorageProvider;
            if (storageProvider is null || !storageProvider.CanPickFolder)
            {
                Log.Warning("Le fournisseur de stockage Avalonia ne permet pas la sélection de dossier.");
                return null;
            }

            IStorageFolder? suggestedStartLocation = null;
            if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
            {
                suggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(startFolder);
            }

            var options = new FolderPickerOpenOptions
            {
                Title = "Choisir le dossier d'enregistrement",
                AllowMultiple = false,
                SuggestedStartLocation = suggestedStartLocation
            };

            var folders = await storageProvider.OpenFolderPickerAsync(options);
            if (ct.IsCancellationRequested || folders.Count == 0)
            {
                return null;
            }

            return folders[0].TryGetLocalPath()
                ?? (folders[0].Path is { IsAbsoluteUri: true } uri ? uri.LocalPath : null);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Échec lors de la sélection du dossier ({ErrorType}).", ex.GetType().Name);
            return null;
        }
    }

    public async Task<bool> OpenFolderAsync(string folderPath, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var topLevel = GetMainTopLevel();
            var launcher = topLevel?.Launcher;
            if (launcher is not null)
            {
                var launched = await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folderPath));
                if (launched)
                {
                    return true;
                }
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });

            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Impossible d'ouvrir le dossier dans l'explorateur ({ErrorType}).", ex.GetType().Name);
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
