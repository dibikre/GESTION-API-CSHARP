using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ApiTester.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class HarImportViewModel : ViewModelBase
{
    private Action<IReadOnlyList<HarEntryItem>, bool>? _onImport;
    private bool _isBulkUpdatingSelection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportSelectedCommand))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HideStaticAssets { get; set; }

    [ObservableProperty]
    public partial bool ReplaceExistingSteps { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfoBanner))]
    public partial string InfoBannerText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewItem))]
    public partial HarEntryItem? SelectedPreviewItem { get; set; }

    public ObservableCollection<HarEntryItem> AllEntries { get; } = [];
    public ObservableCollection<HarEntryItem> FilteredEntries { get; } = [];
    public ObservableCollection<HarFormatShortcutItem> FormatShortcuts { get; } = [];

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasInfoBanner => !string.IsNullOrWhiteSpace(InfoBannerText);
    public bool HasEntries => AllEntries.Count > 0;
    public bool HasFilteredEntries => FilteredEntries.Count > 0;
    public bool HasFormatShortcuts => FormatShortcuts.Count > 0;
    public bool HasPreviewItem => SelectedPreviewItem is not null;

    public int TotalCount => AllEntries.Count;
    public int FilteredCount => FilteredEntries.Count;
    public int SelectedCount => AllEntries.Count(e => e.IsSelected);

    public string SelectionCountText => TotalCount == 0
        ? "0 requête"
        : $"{SelectedCount} / {TotalCount} {(SelectedCount <= 1 ? "sélectionnée" : "sélectionnées")}";

    public string FilterCountText => FilteredCount == TotalCount
        ? string.Empty
        : $"{FilteredCount} affichée(s)";

    public bool HasFilterCount => !string.IsNullOrEmpty(FilterCountText);

    public string ImportButtonText => SelectedCount > 0
        ? $"Ajouter à la chaîne ({SelectedCount})"
        : "Ajouter à la chaîne";

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnHideStaticAssetsChanged(bool value)
    {
        ApplyFilter();
    }

    public void Open(HarParseResult result, Action<IReadOnlyList<HarEntryItem>, bool> onImport)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onImport);

        ClearEntries();
        _onImport = onImport;
        FileName = string.IsNullOrWhiteSpace(result.FileName) ? "archive.har" : result.FileName;
        SearchText = string.Empty;
        HideStaticAssets = false;
        ReplaceExistingSteps = false;

        if (!result.IsSuccess)
        {
            ErrorMessage = result.ErrorMessage ?? "Impossible de lire le fichier HAR.";
            InfoBannerText = string.Empty;
            SelectedPreviewItem = null;
            NotifyAllStateChanged();
            IsOpen = true;
            return;
        }

        ErrorMessage = null;
        foreach (var entry in result.Entries)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
            AllEntries.Add(entry);
        }

        var infoParts = new List<string>();
        if (result.IgnoredNonHttpCount > 0)
        {
            infoParts.Add($"{result.IgnoredNonHttpCount} requête(s) non-HTTP ignorée(s)");
        }

        if (result.IgnoredInvalidCount > 0)
        {
            infoParts.Add($"{result.IgnoredInvalidCount} entrée(s) invalide(s) ignorée(s)");
        }

        InfoBannerText = infoParts.Count > 0 ? string.Join(" · ", infoParts) : string.Empty;

        BuildFormatShortcuts();
        ApplyFilter();
        SelectedPreviewItem = FilteredEntries.FirstOrDefault() ?? AllEntries.FirstOrDefault();
        NotifyAllStateChanged();
        IsOpen = true;
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        _isBulkUpdatingSelection = true;
        try
        {
            foreach (var entry in FilteredEntries)
            {
                entry.IsSelected = true;
            }
        }
        finally
        {
            _isBulkUpdatingSelection = false;
        }

        RefreshFormatShortcutCounts();
        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private void DeselectAllVisible()
    {
        _isBulkUpdatingSelection = true;
        try
        {
            foreach (var entry in FilteredEntries)
            {
                entry.IsSelected = false;
            }
        }
        finally
        {
            _isBulkUpdatingSelection = false;
        }

        RefreshFormatShortcutCounts();
        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private void ToggleFormatShortcut(HarFormatShortcutItem? shortcut)
    {
        if (shortcut is null)
        {
            return;
        }

        var matching = AllEntries.Where(shortcut.Matches).ToList();
        if (matching.Count == 0)
        {
            return;
        }

        var targetSelected = !matching.Any(e => e.IsSelected);
        _isBulkUpdatingSelection = true;
        try
        {
            foreach (var entry in matching)
            {
                entry.IsSelected = targetSelected;
            }
        }
        finally
        {
            _isBulkUpdatingSelection = false;
        }

        RefreshFormatShortcutCounts();
        NotifySelectionStateChanged();
    }

    private bool CanImport() => IsOpen && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void ImportSelected()
    {
        var selected = AllEntries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var callback = _onImport;
        var replaceExisting = ReplaceExistingSteps;

        IsOpen = false;
        _onImport = null;
        ClearEntries();
        NotifyAllStateChanged();

        callback?.Invoke(selected, replaceExisting);
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private void Close()
    {
        IsOpen = false;
        _onImport = null;
        ClearEntries();
        NotifyAllStateChanged();
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        var hasQuery = !string.IsNullOrEmpty(query);

        FilteredEntries.Clear();
        foreach (var entry in AllEntries)
        {
            if (HideStaticAssets && entry.IsStaticAsset)
            {
                continue;
            }

            if (hasQuery &&
                !entry.FullUrl.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !entry.OriginalMethod.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredEntries.Add(entry);
        }

        if (SelectedPreviewItem is null || !FilteredEntries.Contains(SelectedPreviewItem))
        {
            SelectedPreviewItem = FilteredEntries.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasFilteredEntries));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(FilterCountText));
        OnPropertyChanged(nameof(HasFilterCount));
    }

    private void ClearEntries()
    {
        foreach (var entry in AllEntries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }

        AllEntries.Clear();
        FilteredEntries.Clear();
        FormatShortcuts.Clear();
        SelectedPreviewItem = null;
    }

    private void BuildFormatShortcuts()
    {
        FormatShortcuts.Clear();
        if (AllEntries.Count == 0)
        {
            return;
        }

        var staticEntries = AllEntries.Where(e => e.IsStaticAsset).ToList();
        if (staticEntries.Count > 0)
        {
            FormatShortcuts.Add(new HarFormatShortcutItem(
                key: "__STATIC__",
                label: "Assets statiques",
                sortOrder: 0,
                totalCount: staticEntries.Count,
                checkedCount: staticEntries.Count(e => e.IsSelected),
                predicate: static e => e.IsStaticAsset));
        }

        var imageEntries = AllEntries.Where(e => IsImageFormatTag(e.ResponseFormatTag)).ToList();
        var distinctImageTags = imageEntries
            .Select(e => e.ResponseFormatTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (distinctImageTags >= 2)
        {
            FormatShortcuts.Add(new HarFormatShortcutItem(
                key: "__IMAGES__",
                label: "Images",
                sortOrder: 25,
                totalCount: imageEntries.Count,
                checkedCount: imageEntries.Count(e => e.IsSelected),
                predicate: static e => IsImageFormatTag(e.ResponseFormatTag)));
        }

        var groupedFormats = AllEntries
            .Where(e => e.HasResponseFormatTag)
            .GroupBy(e => e.ResponseFormatTag, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var tag = group.Key.ToUpperInvariant();
                var (label, order) = GetFormatDisplayInfo(tag);
                var items = group.ToList();
                return new HarFormatShortcutItem(
                    key: tag,
                    label: label,
                    sortOrder: order,
                    totalCount: items.Count,
                    checkedCount: items.Count(e => e.IsSelected),
                    predicate: e => string.Equals(e.ResponseFormatTag, tag, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.TotalCount)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase);

        foreach (var shortcut in groupedFormats)
        {
            FormatShortcuts.Add(shortcut);
        }
    }

    private static bool IsImageFormatTag(string tag) =>
        tag.Equals("JPG", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("PNG", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("GIF", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("SVG", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("WEBP", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("ICO", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("IMG", StringComparison.OrdinalIgnoreCase);

    private static (string Label, int SortOrder) GetFormatDisplayInfo(string tag) => tag switch
    {
        "JS" => ("JS", 10),
        "CSS" => ("CSS", 20),
        "JPG" => ("JPG", 30),
        "PNG" => ("PNG", 31),
        "SVG" => ("SVG", 32),
        "WEBP" => ("WEBP", 33),
        "GIF" => ("GIF", 34),
        "ICO" => ("ICO", 35),
        "IMG" => ("IMG", 36),
        "FONT" => ("Polices", 40),
        "MEDIA" => ("Médias", 50),
        "MAP" => ("MAP", 60),
        "WASM" => ("WASM", 65),
        "HTML" => ("HTML", 70),
        "XML" => ("XML", 80),
        "TEXT" => ("TEXT", 85),
        "PDF" => ("PDF", 90),
        "SSE" => ("SSE", 95),
        "JSON" => ("JSON", 100),
        _ => (tag, 110)
    };

    private void RefreshFormatShortcutCounts()
    {
        foreach (var shortcut in FormatShortcuts)
        {
            shortcut.CheckedCount = AllEntries.Count(e => shortcut.Matches(e) && e.IsSelected);
        }
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isBulkUpdatingSelection)
        {
            return;
        }

        if (e.PropertyName == nameof(HarEntryItem.IsSelected))
        {
            RefreshFormatShortcutCounts();
            NotifySelectionStateChanged();
        }
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionCountText));
        OnPropertyChanged(nameof(ImportButtonText));
        ImportSelectedCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAllStateChanged()
    {
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasFilteredEntries));
        OnPropertyChanged(nameof(HasFormatShortcuts));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(FilterCountText));
        OnPropertyChanged(nameof(HasFilterCount));
        NotifySelectionStateChanged();
    }
}
