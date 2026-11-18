using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public enum BatchMode
{
    Combinations,
    Parallel
}

public partial class VaryingParam : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsVarying { get; set; }

    [ObservableProperty]
    public partial long Start { get; set; } = 1;

    [ObservableProperty]
    public partial long End { get; set; } = 10;

    [ObservableProperty]
    public partial long Step { get; set; } = 1;

    public VaryingParam()
    {
    }

    public VaryingParam(string name, string value, bool isEnabled = true)
    {
        Name = name;
        Value = value;
        IsEnabled = isEnabled;
    }
}
