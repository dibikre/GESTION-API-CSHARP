using System;
using System.Collections.Generic;
using System.Numerics;
using ApiTester.Services;
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
    private BigInteger _rangeStart;
    private BigInteger _rangeEnd;
    private BigInteger _rangeStep = BigInteger.One;
    private NumberBase _rangeBase = NumberBase.Decimal;
    private int _rangeWidth;
    private bool _rangeUppercaseHex = true;
    private bool _rangeUsePrefix;
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
        BigInteger start,
        BigInteger end,
        BigInteger step,
        NumberBase numberBase = NumberBase.Decimal,
        int width = 0,
        bool uppercaseHex = true,
        bool usePrefix = false)
    {
        var axis = new BatchAxis
        {
            Kind = kind,
            Name = name,
            DisplayName = name
        };
        axis.ConfigureRange(start, end, step, numberBase, width, uppercaseHex, usePrefix);
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

    public void ConfigureRange(
        BigInteger start,
        BigInteger end,
        BigInteger step,
        NumberBase numberBase = NumberBase.Decimal,
        int width = 0,
        bool uppercaseHex = true,
        bool usePrefix = false)
    {
        if (width < 0 || width > BatchVariable.MaxWidth)
        {
            throw new ArgumentException($"La largeur doit être comprise entre 0 et {BatchVariable.MaxWidth}.", nameof(width));
        }

        if (!NumberCodec.TryComputeRangeCount(start, end, step, out var count, out _, out var error))
        {
            throw new ArgumentException(error ?? "Plage numérique invalide.");
        }

        _isRange = true;
        _rangeStart = start;
        _rangeEnd = end;
        _rangeStep = step;
        _rangeBase = numberBase;
        _rangeWidth = width;
        _rangeUppercaseHex = uppercaseHex;
        _rangeUsePrefix = usePrefix;
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
        _rangeStart = BigInteger.Zero;
        _rangeEnd = BigInteger.Zero;
        _rangeStep = BigInteger.One;
        _rangeBase = NumberBase.Decimal;
        _rangeWidth = 0;
        _rangeUppercaseHex = true;
        _rangeUsePrefix = false;
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
            clone.ConfigureRange(_rangeStart, _rangeEnd, _rangeStep, _rangeBase, _rangeWidth, _rangeUppercaseHex, _rangeUsePrefix);
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

        var value = _rangeStart + ((BigInteger)index * _rangeStep);
        return NumberCodec.Format(value, _rangeBase, _rangeWidth, _rangeUppercaseHex, _rangeUsePrefix);
    }

    public static bool TryComputeRangeCount(
        long start,
        long end,
        long step,
        int width,
        out long count,
        out string? error)
    {
        if (width < 0 || width > BatchVariable.MaxWidth)
        {
            count = 0;
            error = $"La largeur doit être comprise entre 0 et {BatchVariable.MaxWidth}.";
            return false;
        }

        return NumberCodec.TryComputeRangeCount(start, end, step, out count, out _, out error);
    }

    public static List<string> ParseListValues(string? rawText) => BatchVariable.ParseListValues(rawText);

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
