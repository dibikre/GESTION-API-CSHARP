using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class KeyValueItem : ObservableObject
{
    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public KeyValueItem()
    {
    }

    public KeyValueItem(string key, string value, bool isEnabled = true)
    {
        Key = key;
        Value = value;
        IsEnabled = isEnabled;
    }
}
