namespace ApiTester.Models;

public enum SaveMode
{
    None,
    PerRequest,
    Grouped,
    Both
}

public enum GroupedFormat
{
    Json,
    JsonLines
}

public sealed class BatchSaveOptions
{
    public SaveMode SaveMode { get; set; } = SaveMode.None;
    public string OutputFolder { get; set; } = string.Empty;
    public GroupedFormat GroupedFormat { get; set; } = GroupedFormat.JsonLines;
    public bool CreateTimestampSubfolder { get; set; } = true;
    public bool IncludeSummaryCsv { get; set; } = true;
}
