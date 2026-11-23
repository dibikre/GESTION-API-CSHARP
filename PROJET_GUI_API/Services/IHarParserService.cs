using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;
using Avalonia.Platform.Storage;

namespace ApiTester.Services;

public interface IHarParserService
{
    Task<IStorageFile?> PickHarFileAsync(CancellationToken ct = default);

    Task<HarParseResult> ParseFileAsync(IStorageFile file, CancellationToken ct = default);

    HarParseResult ParseContent(string jsonContent, string fileName);
}
