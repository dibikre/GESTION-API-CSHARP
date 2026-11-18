using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public enum BatchAxisKind
{
    UrlParam,
    Variable
}

public partial class BatchAxis : ObservableObject
{
    private bool _isRange;
    private long _rangeStart;
    private long _rangeEnd;
    private long _rangeStep = 1;
    private int _zeroPad;
    private IReadOnlyList<string> _listValues = [];

    [ObservableProperty]
    public partial BatchAxisKind Kind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveUpAutomationName))]
    [NotifyPropertyChangedFor(nameof(MoveDownAutomationName))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveUpAutomationName))]
    [NotifyPropertyChangedFor(nameof(MoveDownAutomationName))]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueCountText))]
    public partial long ValueCount { get; private set; }

    [ObservableProperty]
    public partial string ValuesPreview { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int Rank { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoleText))]
    public partial string RoleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOuterRole { get; set; }

    [ObservableProperty]
    public partial bool IsInnerRole { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    internal VaryingParam? SourceParam { get; set; }

    internal BatchVariable? SourceVariable { get; set; }

    public bool HasRoleText => !string.IsNullOrWhiteSpace(RoleText);

    public string ValueCountText => ValueCount <= 1
        ? $"{ValueCount} valeur"
        : $"{ValueCount} valeurs";

    public string MoveUpAutomationName => $"Monter l'axe {DisplayName}";

    public string MoveDownAutomationName => $"Descendre l'axe {DisplayName}";

    public static BatchAxis CreateRange(
        BatchAxisKind kind,
        string name,
        long start,
        long end,
        long step,
        int zeroPad = 0)
    {
        var axis = new BatchAxis
        {
            Kind = kind,
            Name = name,
            DisplayName = name
        };
        axis.ConfigureRange(start, end, step, zeroPad);
        return axis;
    }

    public static BatchAxis CreateList(
        BatchAxisKind kind,
        string name,
        IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var axis = new BatchAxis
        {
            Kind = kind,
            Name = name,
            DisplayName = name
        };
        axis.ConfigureList(values);
        return axis;
    }

    public void ConfigureRange(long start, long end, long step, int zeroPad = 0)
    {
        if (!TryComputeRangeCount(start, end, step, zeroPad, out var count, out var error))
        {
            throw new ArgumentException(error ?? "Plage numérique invalide.");
        }

        _isRange = true;
        _rangeStart = start;
        _rangeEnd = end;
        _rangeStep = step;
        _zeroPad = zeroPad;
        _listValues = [];

        ValueCount = count;
        ValuesPreview = BuildPreview();
        OnPropertyChanged(nameof(ValueCountText));
    }

    public void ConfigureList(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("La liste de valeurs ne peut pas être vide.", nameof(values));
        }

        _isRange = false;
        _rangeStart = 0;
        _rangeEnd = 0;
        _rangeStep = 1;
        _zeroPad = 0;
        _listValues = values is string[] arr ? arr : [.. values];

        ValueCount = _listValues.Count;
        ValuesPreview = BuildPreview();
        OnPropertyChanged(nameof(ValueCountText));
    }

    public BatchAxis CloneWithSources(VaryingParam? sourceParam, BatchVariable? sourceVariable)
    {
        var clone = new BatchAxis
        {
            Kind = Kind,
            Name = Name,
            DisplayName = DisplayName,
            Rank = Rank,
            RoleText = RoleText,
            IsOuterRole = IsOuterRole,
            IsInnerRole = IsInnerRole,
            CanMoveUp = CanMoveUp,
            CanMoveDown = CanMoveDown,
            SourceParam = sourceParam,
            SourceVariable = sourceVariable
        };

        if (_isRange)
        {
            clone.ConfigureRange(_rangeStart, _rangeEnd, _rangeStep, _zeroPad);
        }
        else
        {
            clone.ConfigureList(_listValues);
        }

        return clone;
    }

    public string GetValue(long index)
    {
        if (index < 0 || index >= ValueCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"L'index {index} est hors limites pour l'axe « {Name} » ({ValueCount} valeurs).");
        }

        if (!_isRange)
        {
            return _listValues[(int)index];
        }

        var value = (Int128)_rangeStart + ((Int128)index * (Int128)_rangeStep);
        return FormatRangeValue(value, _zeroPad);
    }

    public static bool TryComputeRangeCount(
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

    public static List<string> ParseListValues(string? rawText)
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

    private string BuildPreview()
    {
        var take = (int)Math.Min(3, ValueCount);
        var parts = new List<string>(take);

        for (var i = 0; i < take; i++)
        {
            parts.Add(GetValue(i));
        }

        return ValueCount > 3
            ? $"{string.Join(", ", parts)} …"
            : string.Join(", ", parts);
    }

    private static string FormatRangeValue(Int128 value, int zeroPad)
    {
        if (zeroPad <= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (value >= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture).PadLeft(zeroPad, '0');
        }

        var positive = (-value).ToString(CultureInfo.InvariantCulture).PadLeft(zeroPad, '0');
        return $"-{positive}";
    }
}

public sealed class BatchOrderPreviewCell
{
    public required string DisplayValue { get; init; }
    public required string FullValue { get; init; }
    public required bool HasChanged { get; init; }
}

public sealed class BatchOrderPreviewRow
{
    public required int Index { get; init; }
    public required IReadOnlyList<BatchOrderPreviewCell> Cells { get; init; }
}
