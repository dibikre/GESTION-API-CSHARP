using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;

namespace ApiTester.Services;

public interface IBatchResultWriter
{
    int SavedCount { get; }
    int ErrorCount { get; }
    string OutputPath { get; }

    Task BeginAsync(BatchSaveOptions options, int totalRequests, CancellationToken ct = default);

    Task WriteAsync(BatchResultItem item, CancellationToken ct = default);

    Task CompleteAsync();
}
