using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;

namespace ApiTester.Services;

public interface IChainPipelineFileService
{
    Task<ChainPipelineSaveResult> SavePipelineAsync(
        ChainPipelineDocument document,
        string? suggestedStartFolder = null,
        string? suggestedFileName = null,
        CancellationToken ct = default);
}
