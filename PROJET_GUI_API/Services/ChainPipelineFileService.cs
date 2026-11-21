using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Serilog;

namespace ApiTester.Services;

public sealed class ChainPipelineFileService : IChainPipelineFileService
{
    private const string PipelineExtension = "apit";
    private const string DefaultFileName = "pipeline.apit";
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<ChainPipelineSaveResult> SavePipelineAsync(
        ChainPipelineDocument document,
        string? suggestedStartFolder = null,
        string? suggestedFileName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (ct.IsCancellationRequested)
        {
            return ChainPipelineSaveResult.Cancelled();
        }

        IStorageFile? targetFile;
        try
        {
            var topLevel = GetMainTopLevel();
            var storageProvider = topLevel?.StorageProvider;
            if (storageProvider is null || !storageProvider.CanSave)
            {
                Log.Warning("Le fournisseur de stockage Avalonia ne permet pas l'enregistrement de fichier.");
                return ChainPipelineSaveResult.Failure(
                    "Impossible d'ouvrir l'explorateur de fichiers pour enregistrer le pipeline.");
            }

            var suggestedStartLocation = await ResolveStartFolderAsync(storageProvider, suggestedStartFolder);
            var safeSuggestedName = NormalizeSuggestedFileName(suggestedFileName);

            var options = new FilePickerSaveOptions
            {
                Title = "Enregistrer le pipeline (.apit)",
                SuggestedStartLocation = suggestedStartLocation,
                SuggestedFileName = safeSuggestedName,
                DefaultExtension = PipelineExtension,
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("Pipeline ApiTester (*.apit)")
                    {
                        Patterns = ["*.apit"]
                    }
                ]
            };

            targetFile = await storageProvider.SaveFilePickerAsync(options);
        }
        catch (OperationCanceledException)
        {
            return ChainPipelineSaveResult.Cancelled();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Échec lors de l'ouverture de l'explorateur de fichiers pour enregistrer le pipeline ({ErrorType}).",
                ex.GetType().Name);
            return ChainPipelineSaveResult.Failure(
                $"Impossible d'ouvrir l'explorateur de fichiers : {ex.Message}");
        }

        if (ct.IsCancellationRequested || targetFile is null)
        {
            return ChainPipelineSaveResult.Cancelled();
        }

        string jsonContent;
        try
        {
            jsonContent = ChainPipelineSerializer.Serialize(document);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Échec de la sérialisation du pipeline en JSON ({ErrorType}).", ex.GetType().Name);
            return ChainPipelineSaveResult.Failure(
                $"Impossible de préparer le contenu du pipeline : {ex.Message}");
        }

        var localPath = targetFile.TryGetLocalPath()
            ?? (targetFile.Path is { IsAbsoluteUri: true, IsFile: true } uri ? uri.LocalPath : null);
        var displayName = !string.IsNullOrWhiteSpace(localPath)
            ? Path.GetFileName(localPath)
            : targetFile.Name;

        try
        {
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                var targetDir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                await using var fileStream = new FileStream(
                    localPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 8192,
                    useAsync: true);
                await using var writer = new StreamWriter(fileStream, Utf8WithoutBom, bufferSize: 8192, leaveOpen: false);
                await writer.WriteAsync(jsonContent.AsMemory(), ct);
                await writer.FlushAsync(ct);
            }
            else
            {
                await using var stream = await targetFile.OpenWriteAsync();
                if (stream.CanSeek)
                {
                    stream.SetLength(0);
                }

                await using var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 8192, leaveOpen: false);
                await writer.WriteAsync(jsonContent.AsMemory(), ct);
                await writer.FlushAsync(ct);
            }

            Log.Information("Pipeline enregistré dans {FileName} ({StepCount} étapes).",
                displayName, document.Steps.Count);

            return ChainPipelineSaveResult.Success(localPath, displayName);
        }
        catch (OperationCanceledException)
        {
            return ChainPipelineSaveResult.Cancelled();
        }
        catch (Exception ex) when (IsFileSystemWriteException(ex))
        {
            Log.Error(ex, "Échec de l'écriture du pipeline dans {FileName} ({ErrorType}).",
                displayName, ex.GetType().Name);
            return ChainPipelineSaveResult.Failure(
                $"Impossible d'enregistrer le fichier « {displayName} » : {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erreur inattendue lors de l'enregistrement du pipeline dans {FileName} ({ErrorType}).",
                displayName, ex.GetType().Name);
            return ChainPipelineSaveResult.Failure(
                $"Erreur lors de l'enregistrement du pipeline : {ex.Message}");
        }
    }

    private static async Task<IStorageFolder?> ResolveStartFolderAsync(
        IStorageProvider storageProvider,
        string? suggestedStartFolder)
    {
        var candidates = new[]
        {
            suggestedStartFolder,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            try
            {
                var folder = await storageProvider.TryGetFolderFromPathAsync(path);
                if (folder is not null)
                {
                    return folder;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Impossible de résoudre le dossier initial {FolderPath} ({ErrorType}).",
                    path, ex.GetType().Name);
            }
        }

        return null;
    }

    private static string NormalizeSuggestedFileName(string? suggestedFileName)
    {
        if (string.IsNullOrWhiteSpace(suggestedFileName))
        {
            return DefaultFileName;
        }

        var trimmed = suggestedFileName.Trim();
        return trimmed.EndsWith($".{PipelineExtension}", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}.{PipelineExtension}";
    }

    private static bool IsFileSystemWriteException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or PathTooLongException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException;

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
