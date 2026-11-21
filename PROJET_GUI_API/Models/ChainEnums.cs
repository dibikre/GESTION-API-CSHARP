namespace ApiTester.Models;

public enum ExtractionKind
{
    JsonPath,
    Regex,
    FullResponse
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
    NotStartsWith,
    EndsWith,
    NotEndsWith,
    Equals,
    NotEquals,
    InList,
    NotInList,
    RegexMatch,
    RegexNotMatch,
    RegexExtract,
    RegexExtractAll,
    RegexReplace,
    ReplaceText,
    PrependText,
    AppendText,
    SetValue,
    Between,
    SplitPart,
    Trim,
    ToLower,
    ToUpper,
    IsEmpty,
    NotEmpty,
    LengthEquals,
    LengthMin,
    LengthMax,
    NumberEquals,
    NumberNotEquals,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual
}

public enum ExtractionConditionalAction
{
    Keep,
    Exclude,
    SetValue,
    PrependText,
    AppendText,
    ReplaceText,
    RegexExtract,
    RegexExtractAll,
    RegexReplace,
    Between,
    SplitPart,
    Trim,
    ToLower,
    ToUpper
}

public enum ExtractionConditionMatchMode
{
    All,
    Any
}

public enum ChainResultSortMode
{
    ChronologicalAsc,
    ChronologicalDesc,
    SizeDesc,
    SizeAsc
}
