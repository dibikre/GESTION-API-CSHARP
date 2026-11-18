using ApiTester.ViewModels;

namespace ApiTester.Models;

public class BatchResultItem
{
    public int Index { get; set; }
    public string ParamsText { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public StatusCategory StatusCategory { get; set; } = StatusCategory.None;
    public long DurationMs { get; set; }
    public string SizeText { get; set; } = "0 B";
    public string? ErrorMessage { get; set; }
    public ResponseModel? Response { get; set; }
}
