using System.Threading.Tasks;

namespace ApiTester.Services;

public interface IClipboardService
{
    Task<bool> SetTextAsync(string text);
}
