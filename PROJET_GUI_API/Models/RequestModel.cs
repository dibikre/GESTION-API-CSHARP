using System.Collections.Generic;

namespace ApiTester.Models;

public class RequestModel
{
    public HttpMethodType Method { get; set; } = HttpMethodType.Get;
    public string Url { get; set; } = string.Empty;
    public List<KeyValueItem> QueryParams { get; set; } = [];
    public List<KeyValueItem> Headers { get; set; } = [];
    public BodyType BodyType { get; set; } = BodyType.None;
    public string Body { get; set; } = string.Empty;
    public List<KeyValueItem> FormFields { get; set; } = [];
    public AuthConfig Auth { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
}
