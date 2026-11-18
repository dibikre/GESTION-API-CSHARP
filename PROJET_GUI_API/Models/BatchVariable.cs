using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public enum VariableMode
{
    Range,
    List
}

public partial class BatchVariable : ObservableObject
{
    private bool _isInitializing = true;

    public BatchVariable()
    {
        _isInitializing = false;
        RecalculateState();
    }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangeMode))]
    [NotifyPropertyChangedFor(nameof(IsListMode))]
    public partial VariableMode Mode { get; set; } = VariableMode.List;

    [ObservableProperty]
    public partial long RangeStart { get; set; } = 1;

    [ObservableProperty]
    public partial long RangeEnd { get; set; } = 10;

    [ObservableProperty]
    public partial long RangeStep { get; set; } = 1;

    [ObservableProperty]
    public partial int ZeroPad { get; set; } = 0;

    [ObservableProperty]
    public partial string ListText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocations))]
    public partial string UsedIn { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUsed { get; set; } = true;

    [ObservableProperty]
    public partial bool IsConfigured { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueCountText))]
    public partial long ValueCount { get; private set; }

    [ObservableProperty]
    public partial string ValuesPreview { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool IsRangeMode => Mode == VariableMode.Range;

    public bool IsListMode => Mode == VariableMode.List;

    public bool HasLocations => !string.IsNullOrWhiteSpace(UsedIn);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public string ValueCountText => !IsConfigured || HasError
        ? "0 valeur"
        : ValueCount <= 1
            ? $"{ValueCount} valeur"
            : $"{ValueCount} valeurs";

    partial void OnModeChanged(VariableMode value)
    {
        if (!_isInitializing)
        {
            IsConfigured = true;
            RecalculateState();
        }
    }

    partial void OnRangeStartChanged(long value)
    {
        if (!_isInitializing)
        {
            IsConfigured = true;
            RecalculateState();
        }
    }

    partial void OnRangeEndChanged(long value)
    {
        if (!_isInitializing)
        {
            IsConfigured = true;
            RecalculateState();
        }
    }

    partial void OnRangeStepChanged(long value)
    {
        if (!_isInitializing)
        {
            IsConfigured = true;
            RecalculateState();
        }
    }

    partial void OnZeroPadChanged(int value)
    {
        if (!_isInitializing)
        {
            IsConfigured = true;
            RecalculateState();
        }
    }

    partial void OnListTextChanged(string value)
    {
        if (!_isInitializing)
        {
            IsConfigured = true;
            RecalculateState();
        }
    }

    partial void OnIsConfiguredChanged(bool value)
    {
        if (!_isInitializing)
        {
            RecalculateState();
        }
    }

    partial void OnIsUsedChanged(bool value)
    {
        if (!_isInitializing)
        {
            RecalculateState();
        }
    }

    public IEnumerable<string> EnumerateValues()
    {
        if (!IsConfigured || HasError)
        {
            yield break;
        }

        if (Mode == VariableMode.List)
        {
            foreach (var item in ParseListValues(ListText))
            {
                yield return item;
            }
            yield break;
        }

        var current = RangeStart;
        var end = RangeEnd;
        var step = RangeStep;
        var pad = ZeroPad;

        while (true)
        {
            if (step > 0 && current > end)
            {
                yield break;
            }

            if (step < 0 && current < end)
            {
                yield break;
            }

            yield return FormatRangeValue(current, pad);

            if (step > 0 && current > long.MaxValue - step)
            {
                yield break;
            }

            if (step < 0 && current < long.MinValue - step)
            {
                yield break;
            }

            current += step;
        }
    }

    public void RecalculateState()
    {
        if (!IsConfigured)
        {
            ValueCount = 0;
            ValuesPreview = string.Empty;
            ErrorText = "Variable non configurée.";
            OnPropertyChanged(nameof(ValueCountText));
            return;
        }

        if (Mode == VariableMode.Range)
        {
            if (!TryComputeRangeCount(RangeStart, RangeEnd, RangeStep, ZeroPad, out var count, out var error))
            {
                ValueCount = 0;
                ValuesPreview = string.Empty;
                ErrorText = error;
                OnPropertyChanged(nameof(ValueCountText));
                return;
            }

            ValueCount = count;
            ErrorText = null;
            ValuesPreview = BuildPreviewFromRange(RangeStart, RangeEnd, RangeStep, ZeroPad, count);
            OnPropertyChanged(nameof(ValueCountText));
            return;
        }

        var items = ParseListValues(ListText);
        if (items.Count == 0)
        {
            ValueCount = 0;
            ValuesPreview = string.Empty;
            ErrorText = "La liste de valeurs est vide.";
            OnPropertyChanged(nameof(ValueCountText));
            return;
        }

        ValueCount = items.Count;
        ErrorText = null;
        ValuesPreview = items.Count <= 3
            ? string.Join(", ", items)
            : $"{string.Join(", ", items[0], items[1], items[2])} …";
        OnPropertyChanged(nameof(ValueCountText));
    }

    private static bool TryComputeRangeCount(
        long start,
        long end,
        long step,
        int zeroPad,
        out long count,
        out string? error)
    {
        count = 0;
        error = null;

        if (step == 0)
        {
            error = "Le pas ne peut pas être 0.";
            return false;
        }

        if (start <= end && step < 0)
        {
            error = "Le pas doit être positif lorsque Début <= Fin.";
            return false;
        }

        if (start > end && step > 0)
        {
            error = "Le pas doit être négatif lorsque Début > Fin.";
            return false;
        }

        if (zeroPad < 0 || zeroPad > 12)
        {
            error = "Le remplissage par zéros doit être compris entre 0 et 12.";
            return false;
        }

        var diff = (Int128)end - (Int128)start;
        var count128 = (diff / (Int128)step) + 1;
        count = count128 > long.MaxValue ? long.MaxValue : (long)count128;
        return true;
    }

    private static string BuildPreviewFromRange(long start, long end, long step, int zeroPad, long count)
    {
        var take = (int)Math.Min(3, count);
        var parts = new List<string>(take);
        var current = start;

        for (var i = 0; i < take; i++)
        {
            parts.Add(FormatRangeValue(current, zeroPad));
            if (i + 1 < take)
            {
                current += step;
            }
        }

        return count > 3
            ? $"{string.Join(", ", parts)} …"
            : string.Join(", ", parts);
    }

    private static string FormatRangeValue(long value, int zeroPad)
    {
        if (zeroPad <= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (value >= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture).PadLeft(zeroPad, '0');
        }

        var positive = (-(Int128)value).ToString(CultureInfo.InvariantCulture).PadLeft(zeroPad, '0');
        return $"-{positive}";
    }

    private static List<string> ParseListValues(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return [];
        }

        var lines = rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var values = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                values.Add(trimmed);
            }
        }

        return values;
    }
}
