using System.Collections.Generic;

namespace ApiTester.Models;

public class CurlParseResult
{
    public RequestModel Request { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}
