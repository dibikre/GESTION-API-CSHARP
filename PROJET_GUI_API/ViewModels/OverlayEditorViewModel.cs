using System;
using System.Text.Json;
using System.Threading.Tasks;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.ViewModels;

public partial class OverlayEditorViewModel : ViewModelBase
{
    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        MaxDepth = 1024
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 1024
    };

    private readonly IClipboardService? _clipboardService;
    private Action<string>? _onSave;

    public OverlayEditorViewModel(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAndCloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyConditionsCommand))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FormatJsonCommand))]
    public partial bool IsValidJson { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStandardSaveButtons))]
    [NotifyPropertyChangedFor(nameof(ShowStandardCloseButton))]
    public partial bool IsReadOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConditionsSupport))]
    [NotifyPropertyChangedFor(nameof(ShowStandardSaveButtons))]
    [NotifyPropertyChangedFor(nameof(ShowStandardCloseButton))]
    [NotifyPropertyChangedFor(nameof(ShowConditionsApplyButtons))]
    [NotifyPropertyChangedFor(nameof(ShowConditionsCloseButton))]
    [NotifyPropertyChangedFor(nameof(ActiveConditionsCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveConditionsBadge))]
    [NotifyPropertyChangedFor(nameof(ConditionFilterSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasConditionFilterError))]
    [NotifyPropertyChangedFor(nameof(ConditionFilterError))]
    public partial ExtractionConditionsViewModel? ConditionsPanel { get; private set; }

    [ObservableProperty]
    public partial bool IsConditionsOpen { get; set; }

    public bool HasConditionsSupport => ConditionsPanel is not null;

    public bool ShowStandardSaveButtons => !HasConditionsSupport && !IsReadOnly;

    public bool ShowStandardCloseButton => !HasConditionsSupport && IsReadOnly;

    public bool ShowConditionsApplyButtons => HasConditionsSupport && (ConditionsPanel?.HasChanges ?? false);

    public bool ShowConditionsCloseButton => HasConditionsSupport && !(ConditionsPanel?.HasChanges ?? false);

    public int ActiveConditionsCount => ConditionsPanel?.ActiveConditionsCount ?? 0;

    public bool HasActiveConditionsBadge => ActiveConditionsCount > 0;

    public string ConditionFilterSummaryText => ConditionsPanel?.FilterSummaryText ?? string.Empty;

    public bool HasConditionFilterError => ConditionsPanel?.HasFilterError ?? false;

    public string ConditionFilterError => ConditionsPanel?.FilterError ?? string.Empty;

    partial void OnTextChanged(string value)
    {
        IsValidJson = CheckIsValidJson(value);
    }

    public void Open(OverlayEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        DetachConditionsPanel();

        IsReadOnly = request.IsReadOnly;
        _onSave = request.IsReadOnly ? null : request.OnSave;
        IsConditionsOpen = false;

        if (request.ExtractionConditions is not null)
        {
            var panel = new ExtractionConditionsViewModel(request.ExtractionConditions);
            panel.PreviewUpdated += OnConditionsPreviewUpdated;
            ConditionsPanel = panel;
            SyncFromConditionsPanel();
        }
        else
        {
            Title = request.Title ?? string.Empty;
            Text = request.InitialText ?? string.Empty;
            IsValidJson = CheckIsValidJson(Text);
        }

        IsOpen = true;
    }

    [RelayCommand]
    private void ToggleConditions()
    {
        if (!HasConditionsSupport)
        {
            return;
        }

        IsConditionsOpen = !IsConditionsOpen;
    }

    [RelayCommand(CanExecute = nameof(CanApplyConditions))]
    private void ApplyConditions()
    {
        if (ConditionsPanel is null || !ConditionsPanel.TryApply())
        {
            return;
        }

        DetachConditionsPanel();
        IsConditionsOpen = false;
        IsOpen = false;
        _onSave = null;
    }

    private bool CanApplyConditions() =>
        IsOpen && ConditionsPanel is not null && !ConditionsPanel.HasBlockingError;

    [RelayCommand(CanExecute = nameof(IsValidJson))]
    private void FormatJson()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(Text.TrimStart('\uFEFF'), JsonParseOptions);
            Text = JsonSerializer.Serialize(document.RootElement, IndentedJsonOptions);
        }
        catch (JsonException)
        {
            IsValidJson = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private void Close()
    {
        DetachConditionsPanel();
        IsConditionsOpen = false;
        IsOpen = false;
        _onSave = null;
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private void SaveAndClose()
    {
        if (HasConditionsSupport)
        {
            if (ConditionsPanel is { HasChanges: true })
            {
                ApplyConditions();
            }
            else
            {
                Close();
            }

            return;
        }

        if (IsReadOnly)
        {
            Close();
            return;
        }

        var callback = _onSave;
        var currentText = Text ?? string.Empty;
        IsOpen = false;
        _onSave = null;
        callback?.Invoke(currentText);
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (_clipboardService is null)
        {
            return;
        }

        await _clipboardService.SetTextAsync(Text ?? string.Empty);
    }

    private void OnConditionsPreviewUpdated()
    {
        SyncFromConditionsPanel();
        OnPropertyChanged(nameof(ShowConditionsApplyButtons));
        OnPropertyChanged(nameof(ShowConditionsCloseButton));
        OnPropertyChanged(nameof(ActiveConditionsCount));
        OnPropertyChanged(nameof(HasActiveConditionsBadge));
        OnPropertyChanged(nameof(ConditionFilterSummaryText));
        OnPropertyChanged(nameof(HasConditionFilterError));
        OnPropertyChanged(nameof(ConditionFilterError));
        ApplyConditionsCommand.NotifyCanExecuteChanged();
    }

    private void SyncFromConditionsPanel()
    {
        if (ConditionsPanel is null)
        {
            return;
        }

        Title = ConditionsPanel.ActiveConditionsCount > 0
            ? $"Liste [[{ConditionsPanel.VariableName}]] — {ConditionsPanel.FilteredCount}/{ConditionsPanel.RawCount} élément(s)"
            : $"Liste [[{ConditionsPanel.VariableName}]] — {ConditionsPanel.RawCount} élément(s)";
        Text = ConditionsPanel.FilteredText;
        IsValidJson = CheckIsValidJson(Text);
    }

    private void DetachConditionsPanel()
    {
        if (ConditionsPanel is not null)
        {
            ConditionsPanel.PreviewUpdated -= OnConditionsPreviewUpdated;
            ConditionsPanel = null;
        }
    }

    private static bool CheckIsValidJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('\uFEFF'), JsonParseOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
