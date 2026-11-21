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

public enum ChainCliLanguage
{
    Python,
    JavaScript,
    Php
}

public enum ChainStepKind
{
    HttpRequest,
    Return
}

public enum ExtractionConditionOperator
{
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Equals,
    NotEquals,
    RegexMatch,
    RegexNotMatch,
    RegexExtract,
    RegexExtractAll,
    RegexReplace,
    ReplaceText,
    Between,
    SplitPart,
    Trim,
    ToLower,
    ToUpper,
    NotEmpty,
    LengthMin,
    LengthMax,
    GreaterThan,
    LessThan
}

public enum ExtractionConditionMatchMode
{
    All,
    Any
}
