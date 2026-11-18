using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ApiTester.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiTester.Models;

public enum NumberBase
{
    Binary = 2,
    Octal = 8,
    Decimal = 10,
    Hexadecimal = 16
}

public enum VariableMode
{
    Range,
    List,
    Random
}

public enum RandomKind
{
    Integer,
    HexBytes,
    Text,
    Guid,
    PickFromList
}

public partial class BatchVariable : ObservableObject
{
    public const int MaxWidth = 128;
    public const int MaxRandomCount = 10_000;
    public const int MaxByteCount = 64;
    public const int MaxTextLength = 256;
    private const int FisherYatesThreshold = 100_000;

    private bool _isInitializing = true;
    private BigInteger _parsedStart;
    private BigInteger _parsedEnd;
    private BigInteger _parsedStep = BigInteger.One;
    private IReadOnlyList<string> _parsedListValues = [];
    private string[]? _cachedRandomValues;

    public BatchVariable()
    {
        _isInitializing = false;
        RecalculateState();
    }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocations))]
    public partial string UsedIn { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUsed { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedMode))]
    [NotifyPropertyChangedFor(nameof(IsRangeMode))]
    [NotifyPropertyChangedFor(nameof(IsListMode))]
    [NotifyPropertyChangedFor(nameof(IsRandomMode))]
    [NotifyPropertyChangedFor(nameof(ShowFixedUnconfiguredHint))]
    public partial bool IsVarying { get; set; }

    [ObservableProperty]
    public partial string FixedValue { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangeMode))]
    [NotifyPropertyChangedFor(nameof(IsListMode))]
    [NotifyPropertyChangedFor(nameof(IsRandomMode))]
    public partial VariableMode Mode { get; set; } = VariableMode.Range;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHexBase))]
    [NotifyPropertyChangedFor(nameof(IsNonDecimalBase))]
    public partial NumberBase Base { get; set; } = NumberBase.Decimal;

    [ObservableProperty]
    public partial string StartText { get; set; } = "1";

    [ObservableProperty]
    public partial string EndText { get; set; } = "10";

    [ObservableProperty]
    public partial string StepText { get; set; } = "1";

    [ObservableProperty]
    public partial int Width { get; set; }

    [ObservableProperty]
    public partial bool UppercaseHex { get; set; } = true;

    [ObservableProperty]
    public partial bool UsePrefix { get; set; }

    [ObservableProperty]
    public partial string ListText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRandomInteger))]
    [NotifyPropertyChangedFor(nameof(IsRandomHexBytes))]
    [NotifyPropertyChangedFor(nameof(IsRandomText))]
    [NotifyPropertyChangedFor(nameof(IsRandomGuid))]
    [NotifyPropertyChangedFor(nameof(IsRandomPickFromList))]
    public partial RandomKind RandomKind { get; set; } = RandomKind.Integer;

    [ObservableProperty]
    public partial int RandomCount { get; set; } = 10;

    [ObservableProperty]
    public partial bool IsUnique { get; set; }

    [ObservableProperty]
    public partial string SeedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSeedUsed))]
    [NotifyPropertyChangedFor(nameof(SeedUsedText))]
    public partial long? SeedUsed { get; private set; }

    [ObservableProperty]
    public partial string MinText { get; set; } = "1";

    [ObservableProperty]
    public partial string MaxText { get; set; } = "100";

    [ObservableProperty]
    public partial int ByteCount { get; set; } = 4;

    [ObservableProperty]
    public partial int TextLength { get; set; } = 8;

    [ObservableProperty]
    public partial bool IncludeLowercase { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeUppercase { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeDigits { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeDashUnderscore { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixedUnconfiguredHint))]
    public partial bool IsConfigured { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueCountText))]
    public partial long ValueCount { get; private set; }

    [ObservableProperty]
    public partial string ValuesPreview { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool IsFixedMode => !IsVarying;

    public bool IsRangeMode => IsVarying && Mode == VariableMode.Range;

    public bool IsListMode => IsVarying && Mode == VariableMode.List;

    public bool IsRandomMode => IsVarying && Mode == VariableMode.Random;

    public bool IsHexBase => Base == NumberBase.Hexadecimal;

    public bool IsNonDecimalBase => Base != NumberBase.Decimal;

    public bool IsRandomInteger => RandomKind == RandomKind.Integer;

    public bool IsRandomHexBytes => RandomKind == RandomKind.HexBytes;

    public bool IsRandomText => RandomKind == RandomKind.Text;

    public bool IsRandomGuid => RandomKind == RandomKind.Guid;

    public bool IsRandomPickFromList => RandomKind == RandomKind.PickFromList;

    public bool HasLocations => !string.IsNullOrWhiteSpace(UsedIn);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool HasSeedUsed => SeedUsed.HasValue;

    public string SeedUsedText => SeedUsed.HasValue
        ? $"Graine utilisée : {SeedUsed.Value}"
        : string.Empty;

    public bool ShowFixedUnconfiguredHint => !IsVarying && !IsConfigured;

    public string ValueCountText => ValueCount <= 1
        ? $"{ValueCount} valeur"
        : $"{ValueCount} valeurs";

    internal BigInteger ParsedStart => _parsedStart;
    internal BigInteger ParsedEnd => _parsedEnd;
    internal BigInteger ParsedStep => _parsedStep;

    partial void OnIsVaryingChanged(bool value) => HandleConfigChanged();

    partial void OnFixedValueChanged(string value) => HandleConfigChanged();

    partial void OnModeChanged(VariableMode value) => HandleConfigChanged();

    partial void OnBaseChanged(NumberBase value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnStartTextChanged(string value) => HandleConfigChanged();

    partial void OnEndTextChanged(string value) => HandleConfigChanged();

    partial void OnStepTextChanged(string value) => HandleConfigChanged();

    partial void OnWidthChanged(int value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnUppercaseHexChanged(bool value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnUsePrefixChanged(bool value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnListTextChanged(string value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnRandomKindChanged(RandomKind value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnRandomCountChanged(int value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnIsUniqueChanged(bool value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnSeedTextChanged(string value)
    {
        if (_isInitializing)
        {
            return;
        }

        _cachedRandomValues = null;
        SeedUsed = null;
        RecalculateState();
    }

    partial void OnMinTextChanged(string value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnMaxTextChanged(string value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnByteCountChanged(int value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnTextLengthChanged(int value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnIncludeLowercaseChanged(bool value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnIncludeUppercaseChanged(bool value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnIncludeDigitsChanged(bool value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnIncludeDashUnderscoreChanged(bool value) => HandleConfigChanged(invalidateRandomCache: true);

    partial void OnIsUsedChanged(bool value)
    {
        if (!_isInitializing)
        {
            RecalculateState();
        }
    }

    [RelayCommand]
    private void NewSeed()
    {
        _isInitializing = true;
        try
        {
            SeedText = string.Empty;
            SeedUsed = null;
            _cachedRandomValues = null;
        }
        finally
        {
            _isInitializing = false;
        }

        RecalculateState();
    }

    public BatchVariable Clone()
    {
        var clone = new BatchVariable();
        clone._isInitializing = true;
        try
        {
            clone.Name = Name;
            clone.UsedIn = UsedIn;
            clone.IsUsed = IsUsed;
            clone.IsVarying = IsVarying;
            clone.FixedValue = FixedValue;
            clone.Mode = Mode;
            clone.Base = Base;
            clone.StartText = StartText;
            clone.EndText = EndText;
            clone.StepText = StepText;
            clone.Width = Width;
            clone.UppercaseHex = UppercaseHex;
            clone.UsePrefix = UsePrefix;
            clone.ListText = ListText;
            clone.RandomKind = RandomKind;
            clone.RandomCount = RandomCount;
            clone.IsUnique = IsUnique;
            clone.SeedText = SeedText;
            clone.SeedUsed = SeedUsed;
            clone.MinText = MinText;
            clone.MaxText = MaxText;
            clone.ByteCount = ByteCount;
            clone.TextLength = TextLength;
            clone.IncludeLowercase = IncludeLowercase;
            clone.IncludeUppercase = IncludeUppercase;
            clone.IncludeDigits = IncludeDigits;
            clone.IncludeDashUnderscore = IncludeDashUnderscore;
            clone._cachedRandomValues = _cachedRandomValues is not null ? [.. _cachedRandomValues] : null;
        }
        finally
        {
            clone._isInitializing = false;
        }

        clone.RecalculateState();
        return clone;
    }

    public IReadOnlyList<string> GetRandomValuesSnapshot()
    {
        if (!IsVarying || Mode != VariableMode.Random)
        {
            return [];
        }

        RecalculateState();
        return _cachedRandomValues is not null ? [.. _cachedRandomValues] : [];
    }

    public string GetValue(long index)
    {
        if (!IsConfigured || HasError)
        {
            throw new InvalidOperationException(ErrorText ?? $"La variable « {Name} » n'est pas valide.");
        }

        if (index < 0 || index >= ValueCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"L'index {index} est hors limites pour la variable « {Name} » ({ValueCount} valeurs).");
        }

        if (!IsVarying)
        {
            return FixedValue;
        }

        return Mode switch
        {
            VariableMode.Range => NumberCodec.Format(
                _parsedStart + ((BigInteger)index * _parsedStep),
                Base,
                Width,
                UppercaseHex,
                UsePrefix),
            VariableMode.List => _parsedListValues[(int)index],
            VariableMode.Random => (_cachedRandomValues ?? throw new InvalidOperationException("Séquence aléatoire non initialisée."))[(int)index],
            _ => throw new InvalidOperationException($"Mode de variable inconnu : {Mode}.")
        };
    }

    public IEnumerable<string> EnumerateValues()
    {
        if (!IsConfigured || HasError)
        {
            yield break;
        }

        for (long i = 0; i < ValueCount; i++)
        {
            yield return GetValue(i);
        }
    }

    public void RecalculateState()
    {
        if (!IsVarying)
        {
            _parsedListValues = [];
            if (string.IsNullOrEmpty(FixedValue))
            {
                IsConfigured = false;
                ValueCount = 0;
                ValuesPreview = string.Empty;
                ErrorText = null;
            }
            else
            {
                IsConfigured = true;
                ValueCount = 1;
                ValuesPreview = TruncatePreviewItem(FixedValue);
                ErrorText = null;
            }

            OnPropertyChanged(nameof(ValueCountText));
            return;
        }

        IsConfigured = true;

        switch (Mode)
        {
            case VariableMode.Range:
                RecalculateRangeState();
                break;

            case VariableMode.List:
                RecalculateListState();
                break;

            case VariableMode.Random:
                RecalculateRandomState();
                break;
        }

        OnPropertyChanged(nameof(ValueCountText));
    }

    private void HandleConfigChanged(bool invalidateRandomCache = false)
    {
        if (_isInitializing)
        {
            return;
        }

        if (invalidateRandomCache)
        {
            _cachedRandomValues = null;
        }

        RecalculateState();
    }

    private void RecalculateRangeState()
    {
        if (Width < 0 || Width > MaxWidth)
        {
            SetError($"La largeur doit être comprise entre 0 et {MaxWidth}.");
            return;
        }

        var startRes = NumberCodec.Parse(StartText, Base);
        if (!startRes.IsSuccess)
        {
            SetError($"Début : {startRes.ErrorMessage}");
            return;
        }

        var endRes = NumberCodec.Parse(EndText, Base);
        if (!endRes.IsSuccess)
        {
            SetError($"Fin : {endRes.ErrorMessage}");
            return;
        }

        var stepRes = NumberCodec.Parse(StepText, Base);
        if (!stepRes.IsSuccess)
        {
            SetError($"Pas : {stepRes.ErrorMessage}");
            return;
        }

        if (!NumberCodec.TryComputeRangeCount(startRes.Value, endRes.Value, stepRes.Value, out var count, out _, out var countError))
        {
            SetError(countError ?? "Plage invalide.");
            return;
        }

        _parsedStart = startRes.Value;
        _parsedEnd = endRes.Value;
        _parsedStep = stepRes.Value;
        ValueCount = count;
        ErrorText = null;
        ValuesPreview = BuildRangePreview(count);
    }

    private void RecalculateListState()
    {
        var items = ParseListValues(ListText);
        if (items.Count == 0)
        {
            _parsedListValues = [];
            SetError("La liste de valeurs est vide.");
            return;
        }

        _parsedListValues = items;
        ValueCount = items.Count;
        ErrorText = null;
        ValuesPreview = BuildListPreview(items);
    }

    private void RecalculateRandomState()
    {
        if (RandomCount < 1 || RandomCount > MaxRandomCount)
        {
            _cachedRandomValues = null;
            SetError($"Le nombre de valeurs doit être compris entre 1 et {MaxRandomCount:N0}.");
            return;
        }

        long resolvedSeed;
        if (!string.IsNullOrWhiteSpace(SeedText))
        {
            if (!long.TryParse(SeedText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out resolvedSeed))
            {
                _cachedRandomValues = null;
                SeedUsed = null;
                SetError("La graine doit être un entier.");
                return;
            }

            if (SeedUsed != resolvedSeed)
            {
                SeedUsed = resolvedSeed;
            }
        }
        else
        {
            if (!SeedUsed.HasValue)
            {
                SeedUsed = Random.Shared.Next(1, int.MaxValue);
            }

            resolvedSeed = SeedUsed.Value;
        }

        if (!TryValidateRandomGenerator(out var validationError))
        {
            _cachedRandomValues = null;
            SetError(validationError!);
            return;
        }

        if (_cachedRandomValues is null || _cachedRandomValues.Length != RandomCount)
        {
            _cachedRandomValues = GenerateRandomSequence(resolvedSeed);
        }

        ValueCount = _cachedRandomValues.Length;
        ErrorText = null;
        ValuesPreview = BuildListPreview(_cachedRandomValues);
    }

    private bool TryValidateRandomGenerator(out string? error)
    {
        error = null;

        switch (RandomKind)
        {
            case RandomKind.Integer:
            {
                if (Width < 0 || Width > MaxWidth)
                {
                    error = $"La largeur doit être comprise entre 0 et {MaxWidth}.";
                    return false;
                }

                var minRes = NumberCodec.Parse(MinText, Base);
                if (!minRes.IsSuccess)
                {
                    error = $"Minimum : {minRes.ErrorMessage}";
                    return false;
                }

                var maxRes = NumberCodec.Parse(MaxText, Base);
                if (!maxRes.IsSuccess)
                {
                    error = $"Maximum : {maxRes.ErrorMessage}";
                    return false;
                }

                if (minRes.Value > maxRes.Value)
                {
                    error = "Le minimum doit être inférieur ou égal au maximum.";
                    return false;
                }

                var rangeSize = (maxRes.Value - minRes.Value) + BigInteger.One;
                if (IsUnique && (BigInteger)RandomCount > rangeSize)
                {
                    error = $"Plage trop petite pour {RandomCount} valeurs uniques.";
                    return false;
                }

                return true;
            }

            case RandomKind.HexBytes:
            {
                if (ByteCount < 1 || ByteCount > MaxByteCount)
                {
                    error = $"Le nombre d'octets doit être compris entre 1 et {MaxByteCount}.";
                    return false;
                }

                if (IsUnique && ByteCount <= 3)
                {
                    var maxPossible = BigInteger.One << (ByteCount * 8);
                    if ((BigInteger)RandomCount > maxPossible)
                    {
                        error = $"Plage trop petite pour {RandomCount} valeurs uniques.";
                        return false;
                    }
                }

                return true;
            }

            case RandomKind.Text:
            {
                if (TextLength < 1 || TextLength > MaxTextLength)
                {
                    error = $"La longueur du texte doit être comprise entre 1 et {MaxTextLength}.";
                    return false;
                }

                var alphabet = BuildTextAlphabet();
                if (alphabet.Length == 0)
                {
                    error = "Sélectionne au moins un jeu de caractères.";
                    return false;
                }

                if (IsUnique)
                {
                    var maxPossible = BigInteger.Pow(alphabet.Length, TextLength);
                    if ((BigInteger)RandomCount > maxPossible)
                    {
                        error = $"Plage trop petite pour {RandomCount} valeurs uniques.";
                        return false;
                    }
                }

                return true;
            }

            case RandomKind.Guid:
                return true;

            case RandomKind.PickFromList:
            {
                var items = ParseListValues(ListText);
                if (items.Count == 0)
                {
                    error = "La liste de valeurs est vide.";
                    return false;
                }

                if (IsUnique)
                {
                    var distinct = new HashSet<string>(items, StringComparer.Ordinal);
                    if (RandomCount > distinct.Count)
                    {
                        error = $"Plage trop petite pour {RandomCount} valeurs uniques.";
                        return false;
                    }
                }

                return true;
            }

            default:
                error = "Type aléatoire inconnu.";
                return false;
        }
    }

    private string[] GenerateRandomSequence(long seed)
    {
        var intSeed = unchecked((int)(seed ^ (seed >> 32)));
        var rng = new Random(intSeed);
        var result = new string[RandomCount];

        switch (RandomKind)
        {
            case RandomKind.Integer:
                FillRandomIntegers(rng, result);
                break;

            case RandomKind.HexBytes:
                FillRandomHexBytes(rng, result);
                break;

            case RandomKind.Text:
                FillRandomTexts(rng, result);
                break;

            case RandomKind.Guid:
                FillRandomGuids(rng, result);
                break;

            case RandomKind.PickFromList:
                FillRandomPickFromList(rng, result);
                break;
        }

        return result;
    }

    private void FillRandomIntegers(Random rng, string[] target)
    {
        var min = NumberCodec.Parse(MinText, Base).Value;
        var max = NumberCodec.Parse(MaxText, Base).Value;
        var rangeSize = (max - min) + BigInteger.One;

        if (!IsUnique)
        {
            for (var i = 0; i < target.Length; i++)
            {
                var val = min + NextBigInteger(rng, rangeSize);
                target[i] = NumberCodec.Format(val, Base, Width, UppercaseHex, UsePrefix);
            }
            return;
        }

        if (rangeSize <= FisherYatesThreshold)
        {
            var size = (int)rangeSize;
            var pool = new int[size];
            for (var i = 0; i < size; i++)
            {
                pool[i] = i;
            }

            for (var i = 0; i < target.Length; i++)
            {
                var j = i + rng.Next(size - i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                var val = min + pool[i];
                target[i] = NumberCodec.Format(val, Base, Width, UppercaseHex, UsePrefix);
            }
            return;
        }

        var seen = new HashSet<BigInteger>(target.Length);
        var maxAttempts = Math.Max(target.Length * 25, 1000);
        var filled = 0;
        var attempts = 0;

        while (filled < target.Length && attempts < maxAttempts)
        {
            attempts++;
            var val = min + NextBigInteger(rng, rangeSize);
            if (seen.Add(val))
            {
                target[filled++] = NumberCodec.Format(val, Base, Width, UppercaseHex, UsePrefix);
            }
        }
    }

    private void FillRandomHexBytes(Random rng, string[] target)
    {
        if (IsUnique && ByteCount == 1)
        {
            var pool = new int[256];
            for (var i = 0; i < 256; i++)
            {
                pool[i] = i;
            }

            for (var i = 0; i < target.Length; i++)
            {
                var j = i + rng.Next(256 - i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
                var hex = ((byte)pool[i]).ToString(UppercaseHex ? "X2" : "x2", CultureInfo.InvariantCulture);
                target[i] = hex;
            }
            return;
        }

        var buffer = new byte[ByteCount];
        var seen = IsUnique ? new HashSet<string>(target.Length, StringComparer.Ordinal) : null;
        var maxAttempts = Math.Max(target.Length * 25, 1000);
        var filled = 0;
        var attempts = 0;

        while (filled < target.Length && attempts < maxAttempts)
        {
            attempts++;
            rng.NextBytes(buffer);
            var hex = Convert.ToHexString(buffer);
            if (!UppercaseHex)
            {
                hex = hex.ToLowerInvariant();
            }

            if (seen is null || seen.Add(hex))
            {
                target[filled++] = hex;
            }
        }
    }

    private void FillRandomTexts(Random rng, string[] target)
    {
        var alphabet = BuildTextAlphabet();
        var chars = new char[TextLength];
        var seen = IsUnique ? new HashSet<string>(target.Length, StringComparer.Ordinal) : null;
        var maxAttempts = Math.Max(target.Length * 25, 1000);
        var filled = 0;
        var attempts = 0;

        while (filled < target.Length && attempts < maxAttempts)
        {
            attempts++;
            for (var k = 0; k < chars.Length; k++)
            {
                chars[k] = alphabet[rng.Next(alphabet.Length)];
            }

            var value = new string(chars);
            if (seen is null || seen.Add(value))
            {
                target[filled++] = value;
            }
        }
    }

    private void FillRandomGuids(Random rng, string[] target)
    {
        var bytes = new byte[16];
        var seen = IsUnique ? new HashSet<string>(target.Length, StringComparer.Ordinal) : null;
        var maxAttempts = Math.Max(target.Length * 10, 1000);
        var filled = 0;
        var attempts = 0;

        while (filled < target.Length && attempts < maxAttempts)
        {
            attempts++;
            rng.NextBytes(bytes);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            var guid = new Guid(bytes).ToString("D");

            if (seen is null || seen.Add(guid))
            {
                target[filled++] = guid;
            }
        }
    }

    private void FillRandomPickFromList(Random rng, string[] target)
    {
        var items = ParseListValues(ListText);
        if (!IsUnique)
        {
            for (var i = 0; i < target.Length; i++)
            {
                target[i] = items[rng.Next(items.Count)];
            }
            return;
        }

        var distinct = new List<string>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (seen.Add(item))
            {
                distinct.Add(item);
            }
        }

        var n = distinct.Count;
        for (var i = 0; i < target.Length; i++)
        {
            var j = i + rng.Next(n - i);
            (distinct[i], distinct[j]) = (distinct[j], distinct[i]);
            target[i] = distinct[i];
        }
    }

    private string BuildTextAlphabet()
    {
        var sb = new System.Text.StringBuilder(64);
        if (IncludeLowercase)
        {
            sb.Append("abcdefghijklmnopqrstuvwxyz");
        }
        if (IncludeUppercase)
        {
            sb.Append("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        }
        if (IncludeDigits)
        {
            sb.Append("0123456789");
        }
        if (IncludeDashUnderscore)
        {
            sb.Append("-_");
        }
        return sb.ToString();
    }

    private static BigInteger NextBigInteger(Random rng, BigInteger exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= BigInteger.One)
        {
            return BigInteger.Zero;
        }

        var bitLength = exclusiveUpperBound.GetBitLength();
        var byteCount = (int)((bitLength + 7) / 8);
        var extraBits = (byteCount * 8) - (int)bitLength;
        var mask = extraBits == 0 ? (byte)0xFF : (byte)(0xFF >> extraBits);
        var buffer = new byte[byteCount + 1];

        while (true)
        {
            rng.NextBytes(buffer.AsSpan(0, byteCount));
            buffer[byteCount - 1] &= mask;
            buffer[byteCount] = 0;
            var candidate = new BigInteger(buffer);
            if (candidate < exclusiveUpperBound)
            {
                return candidate;
            }
        }
    }

    private void SetError(string message)
    {
        ValueCount = 0;
        ValuesPreview = string.Empty;
        ErrorText = message;
    }

    private string BuildRangePreview(long count)
    {
        var take = (int)Math.Min(3, count);
        var parts = new List<string>(take);

        for (var i = 0; i < take; i++)
        {
            var value = _parsedStart + ((BigInteger)i * _parsedStep);
            parts.Add(TruncatePreviewItem(NumberCodec.Format(value, Base, Width, UppercaseHex, UsePrefix)));
        }

        return count > 3
            ? $"{string.Join(", ", parts)} …"
            : string.Join(", ", parts);
    }

    private static string BuildListPreview(IReadOnlyList<string> items)
    {
        var take = Math.Min(3, items.Count);
        var parts = new List<string>(take);

        for (var i = 0; i < take; i++)
        {
            parts.Add(TruncatePreviewItem(items[i]));
        }

        return items.Count > 3
            ? $"{string.Join(", ", parts)} …"
            : string.Join(", ", parts);
    }

    private static string TruncatePreviewItem(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > 24 ? $"{value[..24]}…" : value;
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
}
