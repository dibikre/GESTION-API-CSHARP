using System;
using System.Globalization;
using System.Numerics;
using ApiTester.Models;

namespace ApiTester.Services;

public readonly record struct NumberParseResult(bool IsSuccess, BigInteger Value, string? ErrorMessage)
{
    public static NumberParseResult Success(BigInteger value) => new(true, value, null);

    public static NumberParseResult Fail(string message) => new(false, BigInteger.Zero, message);
}

public static class NumberCodec
{
    public static NumberParseResult Parse(string? text, NumberBase numberBase)
    {
        if (!Enum.IsDefined(numberBase) || (int)numberBase < 2)
        {
            return NumberParseResult.Fail("Base numérique invalide.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return NumberParseResult.Fail("Saisie vide.");
        }

        var span = text.AsSpan().Trim();
        var isNegative = false;

        if (span[0] == '-')
        {
            isNegative = true;
            span = span[1..];
            if (span.IsEmpty)
            {
                return NumberParseResult.Fail("Chiffres manquants après le signe « - ».");
            }
        }

        if (numberBase == NumberBase.Binary &&
            span.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            if (span.IsEmpty)
            {
                return NumberParseResult.Fail("Chiffres manquants après le préfixe « 0b ».");
            }
        }
        else if (numberBase == NumberBase.Octal &&
                 span.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            if (span.IsEmpty)
            {
                return NumberParseResult.Fail("Chiffres manquants après le préfixe « 0o ».");
            }
        }
        else if (numberBase == NumberBase.Hexadecimal &&
                 span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            if (span.IsEmpty)
            {
                return NumberParseResult.Fail("Chiffres manquants après le préfixe « 0x ».");
            }
        }

        var radix = (int)numberBase;
        var baseLabel = GetBaseDisplayName(numberBase);
        var accumulator = BigInteger.Zero;

        foreach (var ch in span)
        {
            if (char.IsWhiteSpace(ch))
            {
                return NumberParseResult.Fail("Les espaces ne sont pas autorisés dans un nombre.");
            }

            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => 10 + (ch - 'a'),
                >= 'A' and <= 'F' => 10 + (ch - 'A'),
                _ => -1
            };

            if (digit < 0 || digit >= radix)
            {
                return NumberParseResult.Fail($"Caractère « {ch} » invalide en base {baseLabel}.");
            }

            accumulator = (accumulator * radix) + digit;
        }

        if (isNegative && !accumulator.IsZero)
        {
            accumulator = BigInteger.Negate(accumulator);
        }

        return NumberParseResult.Success(accumulator);
    }

    public static string Format(BigInteger value, NumberBase numberBase, int width, bool upper, bool prefix)
    {
        if (!Enum.IsDefined(numberBase) || (int)numberBase < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(numberBase), numberBase, "Base numérique invalide.");
        }

        var isNegative = value.Sign < 0;
        var magnitude = isNegative ? BigInteger.Negate(value) : value;
        var radix = (int)numberBase;

        string digits;
        if (magnitude.IsZero)
        {
            digits = "0";
        }
        else
        {
            var alphabet = upper ? "0123456789ABCDEF" : "0123456789abcdef";
            var buffer = new System.Collections.Generic.List<char>(64);

            while (magnitude > BigInteger.Zero)
            {
                magnitude = BigInteger.DivRem(magnitude, radix, out var remainder);
                buffer.Add(alphabet[(int)remainder]);
            }

            buffer.Reverse();
            digits = new string(buffer.ToArray());
        }

        if (width > digits.Length)
        {
            digits = digits.PadLeft(width, '0');
        }

        var prefixText = prefix
            ? numberBase switch
            {
                NumberBase.Binary => "0b",
                NumberBase.Octal => "0o",
                NumberBase.Hexadecimal => "0x",
                _ => string.Empty
            }
            : string.Empty;

        return isNegative
            ? string.Create(CultureInfo.InvariantCulture, $"-{prefixText}{digits}")
            : string.Create(CultureInfo.InvariantCulture, $"{prefixText}{digits}");
    }

    public static bool TryComputeRangeCount(
        BigInteger start,
        BigInteger end,
        BigInteger step,
        out long count,
        out BigInteger exactCount,
        out string? error)
    {
        count = 0;
        exactCount = BigInteger.Zero;
        error = null;

        if (step.IsZero)
        {
            error = "Le pas ne peut pas être 0.";
            return false;
        }

        if (start <= end && step < BigInteger.Zero)
        {
            error = "Le pas doit être positif lorsque Début <= Fin.";
            return false;
        }

        if (start > end && step > BigInteger.Zero)
        {
            error = "Le pas doit être négatif lorsque Début > Fin.";
            return false;
        }

        var diff = end - start;
        exactCount = (diff / step) + BigInteger.One;
        count = exactCount > long.MaxValue ? long.MaxValue : (long)exactCount;
        return true;
    }

    public static string GetBaseDisplayName(NumberBase numberBase) => numberBase switch
    {
        NumberBase.Binary => "binaire (2)",
        NumberBase.Octal => "octale (8)",
        NumberBase.Decimal => "décimale (10)",
        NumberBase.Hexadecimal => "hexadécimale (16)",
        _ => numberBase.ToString()
    };
}
