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

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasInfoBanner => !string.IsNullOrWhiteSpace(InfoBannerText);
    public bool HasEntries => AllEntries.Count > 0;
    public bool HasFilteredEntries => FilteredEntries.Count > 0;
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

        ApplyFilter();
        SelectedPreviewItem = FilteredEntries.FirstOrDefault() ?? AllEntries.FirstOrDefault();
        NotifyAllStateChanged();
        IsOpen = true;
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var entry in FilteredEntries)
        {
            entry.IsSelected = true;
        }

        NotifySelectionStateChanged();
    }

    [RelayCommand]
    private void DeselectAllVisible()
    {
        foreach (var entry in FilteredEntries)
        {
            entry.IsSelected = false;
        }

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
        SelectedPreviewItem = null;
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HarEntryItem.IsSelected))
        {
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
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(FilterCountText));
        OnPropertyChanged(nameof(HasFilterCount));
        NotifySelectionStateChanged();
    }
}
