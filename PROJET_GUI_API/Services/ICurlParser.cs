using ApiTester.Models;

namespace ApiTester.Services;

public interface ICurlParser
{
    CurlParseResult Parse(string input);
}
