using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApiTester.Models;

public partial class RateLimitPolicy : ObservableObject
{
    public const int DefaultPauseFloorMs = 300;
    public const int LowPauseWarningThresholdMs = 100;
    public const int MinRetries = 0;
    public const int MaxRetriesLimit = 20;
    public const int MinHttpStatusCode = 100;
    public const int MaxHttpStatusCode = 599;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool RespectRetryAfter { get; set; } = true;

    [ObservableProperty]
    public partial int MaxRetryAfterSeconds { get; set; } = 120;

    [ObservableProperty]
    public partial bool EnableBackoff { get; set; }

    [ObservableProperty]
    public partial int BackoffInitialMs { get; set; } = 500;

    [ObservableProperty]
    public partial double BackoffMultiplier { get; set; } = 2.0;

    [ObservableProperty]
    public partial int BackoffMaxMs { get; set; } = 30_000;

    [ObservableProperty]
    public partial int MaxRetriesPerRequest { get; set; } = 3;

    public List<int> ExtraStatusCodes { get; set; } = [];

    [ObservableProperty]
    public partial string QuotaRemainingHeader { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string QuotaResetHeader { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int QuotaThreshold { get; set; } = 1;

    [ObservableProperty]
    public partial int PauseFloorMs { get; set; } = DefaultPauseFloorMs;

    public RateLimitPolicy Clone()
    {
        return new RateLimitPolicy
        {
            IsEnabled = IsEnabled,
            RespectRetryAfter = RespectRetryAfter,
            MaxRetryAfterSeconds = MaxRetryAfterSeconds,
            EnableBackoff = EnableBackoff,
            BackoffInitialMs = BackoffInitialMs,
            BackoffMultiplier = BackoffMultiplier,
            BackoffMaxMs = BackoffMaxMs,
            MaxRetriesPerRequest = MaxRetriesPerRequest,
            ExtraStatusCodes = [.. ExtraStatusCodes],
            QuotaRemainingHeader = QuotaRemainingHeader,
            QuotaResetHeader = QuotaResetHeader,
            QuotaThreshold = QuotaThreshold,
            PauseFloorMs = PauseFloorMs
        };
    }

    public bool TryValidate(out string? errorMessage)
    {
        if (MaxRetryAfterSeconds < 0)
        {
            errorMessage = "Le plafond Retry-After doit être supérieur ou égal à 0 seconde.";
            return false;
        }

        if (BackoffInitialMs < 0)
        {
            errorMessage = "Le délai initial de backoff doit être positif ou nul.";
            return false;
        }

        if (!double.IsFinite(BackoffMultiplier) || BackoffMultiplier <= 1.0)
        {
            errorMessage = "Le multiplicateur de backoff doit être strictement supérieur à 1.";
            return false;
        }

        if (BackoffMaxMs < BackoffInitialMs)
        {
            errorMessage = "Le délai maximal de backoff doit être supérieur ou égal au délai initial.";
            return false;
        }

        if (MaxRetriesPerRequest < MinRetries || MaxRetriesPerRequest > MaxRetriesLimit)
        {
            errorMessage = $"Le nombre maximal de tentatives par requête doit être compris entre {MinRetries} et {MaxRetriesLimit}.";
            return false;
        }

        if (QuotaThreshold < 0)
        {
            errorMessage = "Le seuil de ralentissement préventif doit être supérieur ou égal à 0.";
            return false;
        }

        if (PauseFloorMs < 0)
        {
            errorMessage = "La pause minimale doit être supérieure ou égale à 0 ms.";
            return false;
        }

        foreach (var code in ExtraStatusCodes)
        {
            if (code < MinHttpStatusCode || code > MaxHttpStatusCode)
            {
                errorMessage = $"Code HTTP supplémentaire invalide ({code}) : doit être compris entre {MinHttpStatusCode} et {MaxHttpStatusCode}.";
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    public static bool TryParseExtraStatusCodes(string? rawText, out List<int> codes, out string? errorMessage)
    {
        codes = [];
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return true;
        }

        var seen = new HashSet<int>();
        var tokens = rawText.Split(',', StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                codes = [];
                errorMessage = $"Code HTTP invalide « {token} » : saisis uniquement des entiers séparés par des virgules.";
                return false;
            }

            if (code < MinHttpStatusCode || code > MaxHttpStatusCode)
            {
                codes = [];
                errorMessage = $"Code HTTP hors plage ({code}) : chaque code doit être compris entre {MinHttpStatusCode} et {MaxHttpStatusCode}.";
                return false;
            }

            if (seen.Add(code))
            {
                codes.Add(code);
            }
        }

        return true;
    }
}
