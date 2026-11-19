namespace ApiTester.Models;

public enum ExtractionKind
{
    JsonPath,
    Regex
}

public enum ChainExtractionSource
{
    Body,
    Header
}

public enum ChainStepStatus
{
    Success,
    Failed,
    Skipped
}

public enum ChainRunStatus
{
    Success,
    Failed,
    Cancelled
}
