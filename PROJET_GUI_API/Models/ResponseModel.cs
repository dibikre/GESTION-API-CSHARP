using System.Collections.Generic;

namespace ApiTester.Models;

public class ResponseModel
{
    public int StatusCode { get; set; }
    public string ReasonPhrase { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public long DurationMs { get; set; }
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public List<KeyValueItem> Headers { get; set; } = [];
    public string Body { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
