using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ApiTester.Models;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class CookieEditorViewModel : ViewModelBase
{
    private readonly IClipboardService? _clipboardService;
    private Action<string>? _onSave;

    public CookieEditorViewModel(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService;
        Cookies.CollectionChanged += OnCookiesCollectionChanged;
    }

    public ObservableCollection<CookiePairItem> Cookies { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAndCloseCommand))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTableMode))]
    [NotifyPropertyChangedFor(nameof(CookieCountText))]
    public partial bool IsTextMode { get; set; }

    public bool IsTableMode => !IsTextMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CookieCountText))]
    public partial string TextValue { get; set; } = string.Empty;

    public string CookieCountText
    {
        get
        {
            if (IsTextMode)
            {
                var parsed = CookiePairItem.Parse(TextValue);
                return FormatCookieCount(parsed.Count, parsed.Count);
            }

            var total = Cookies.Count(x => !string.IsNullOrWhiteSpace(x.Name));
            var enabled = Cookies.Count(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Name));
            return FormatCookieCount(enabled, total);
        }
    }

    public void Open(CookieEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReplaceCookies(CookiePairItem.Parse(request.InitialCookieHeader));
        TextValue = request.InitialCookieHeader ?? string.Empty;
        IsTextMode = false;
        _onSave = request.OnSave;
        IsOpen = true;
        OnPropertyChanged(nameof(CookieCountText));
    }

    [RelayCommand]
    private void SwitchToTable()
    {
        if (!IsTextMode)
        {
            return;
        }

        ReplaceCookies(CookiePairItem.Parse(TextValue));
        IsTextMode = false;
        OnPropertyChanged(nameof(CookieCountText));
    }

    [RelayCommand]
    private void SwitchToText()
    {
        if (IsTextMode)
        {
            return;
        }

        TextValue = CookiePairItem.Serialize(Cookies);
        IsTextMode = true;
        OnPropertyChanged(nameof(CookieCountText));
    }

    [RelayCommand]
    private void AddCookie()
    {
        if (IsTextMode)
        {
            SwitchToTable();
        }

        Cookies.Add(new CookiePairItem());
        OnPropertyChanged(nameof(CookieCountText));
    }

    [RelayCommand]
    private void RemoveCookie(CookiePairItem? item)
    {
        if (item is not null)
        {
            Cookies.Remove(item);
        }
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_clipboardService is null)
        {
            return;
        }

        await _clipboardService.SetTextAsync(BuildCurrentHeaderString());
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private void Close()
    {
        IsOpen = false;
        _onSave = null;
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private void SaveAndClose()
    {
        var callback = _onSave;
        var updatedHeader = BuildCurrentHeaderString();
        IsOpen = false;
        _onSave = null;
        callback?.Invoke(updatedHeader);
    }

    private string BuildCurrentHeaderString()
    {
        return IsTextMode
            ? CookiePairItem.NormalizeText(TextValue)
            : CookiePairItem.Serialize(Cookies);
    }

    private void ReplaceCookies(IEnumerable<CookiePairItem> items)
    {
        foreach (var existing in Cookies)
        {
            existing.PropertyChanged -= OnCookieItemPropertyChanged;
        }

        Cookies.Clear();
        foreach (var item in items)
        {
            Cookies.Add(item);
        }
    }

    private void OnCookiesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CookiePairItem oldItem in e.OldItems)
            {
                oldItem.PropertyChanged -= OnCookieItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (CookiePairItem newItem in e.NewItems)
            {
                newItem.PropertyChanged += OnCookieItemPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(CookieCountText));
    }

    private void OnCookieItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CookieCountText));
    }

    private static string FormatCookieCount(int enabled, int total)
    {
        if (total == 0)
        {
            return "0 cookie";
        }

        if (enabled == total)
        {
            return total == 1 ? "1 cookie" : $"{total} cookies";
        }

        return total == 1
            ? $"{enabled}/1 cookie actif"
            : $"{enabled}/{total} cookies actifs";
    }
}
