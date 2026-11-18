using System.Threading;
using System.Threading.Tasks;

namespace ApiTester.Services;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(string? startFolder, CancellationToken ct = default);

    Task<bool> OpenFolderAsync(string folderPath, CancellationToken ct = default);
}
