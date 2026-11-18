using System.Threading;
using System.Threading.Tasks;
using ApiTester.Models;

namespace ApiTester.Services;

public interface IHttpService
{
    Task<ResponseModel> SendAsync(RequestModel request, CancellationToken ct = default);
}
