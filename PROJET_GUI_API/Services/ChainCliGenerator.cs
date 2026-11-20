using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ApiTester.Models;

namespace ApiTester.Services;

internal sealed record CliHeaderSpec(string Key, string Value);

internal sealed record CliQueryParamSpec(string Key, string Value, int? AxisIndex);

internal sealed record CliFormFieldSpec(string Key, string Value);

internal sealed record CliRangeSpec(
    string Start,
    string End,
    string Step,
    int Base,
    int Width,
    bool UppercaseHex,
    bool UsePrefix);

internal sealed record CliAxisSpec(
    string Kind,
    string Name,
    CliRangeSpec? Range,
    IReadOnlyList<string> Values);

internal sealed record CliExtractionSpec(
    string VariableName,
    string Source,
    string Kind,
    string Expression,
    int RegexGroup,
    string HeaderName,
    bool IsRequired,
    string DefaultValueIfMissing,
    bool IsList);

internal sealed record CliStepSpec(
    int Rank,
    string Name,
    string Method,
    string Url,
    int TimeoutSeconds,
    int DelayAfterMs,
    bool ContinueOnError,
    string Mode,
    string BodyType,
    string Body,
    IReadOnlyList<CliHeaderSpec> Headers,
    IReadOnlyList<CliQueryParamSpec> QueryParams,
    IReadOnlyList<CliFormFieldSpec> FormFields,
    IReadOnlyDictionary<string, string> FixedVariables,
    IReadOnlyList<CliAxisSpec> Axes,
    IReadOnlyList<CliExtractionSpec> Extractions);

public sealed class ChainCliGenerator : IChainCliGenerator
{
    private static readonly TemplateEngine TemplateScanner = new();

    public string Generate(IReadOnlyList<ChainStep> steps, ChainCliLanguage language)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var specs = BuildStepSpecs(steps);
        return language switch
        {
            ChainCliLanguage.Python => PythonChainCliEmitter.Emit(specs),
            ChainCliLanguage.JavaScript => JavaScriptChainCliEmitter.Emit(specs),
            ChainCliLanguage.Php => PhpChainCliEmitter.Emit(specs),
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Langage CLI non pris en charge.")
        };
    }

    private static List<CliStepSpec> BuildStepSpecs(IReadOnlyList<ChainStep> steps)
    {
        var specs = new List<CliStepSpec>(steps.Count);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var rank = i + 1;
            var name = string.IsNullOrWhiteSpace(step.Name) ? $"Étape {rank}" : step.Name.Trim();
            var method = step.Method.ToString().ToUpperInvariant();
            var url = step.Url?.Trim() ?? string.Empty;
            var timeoutSeconds = step.ParsedTimeoutSeconds > 0 ? step.ParsedTimeoutSeconds : 30;
            var delayAfterMs = Math.Max(0, step.DelayAfterMs);
            var mode = step.SelectedMode == BatchMode.Parallel ? "parallel" : "combinations";

            var headers = BuildHeaders(step);
            var bodyType = step.BodyType switch
            {
                BodyType.Json => "json",
                BodyType.Text => "text",
                BodyType.FormUrlEncoded => "form",
                _ => "none"
            };
            var body = BuildBody(step, headers);
            var formFields = step.BodyType == BodyType.FormUrlEncoded
                ? step.FormFields
                    .Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.Key))
                    .Select(f => new CliFormFieldSpec(f.Key.Trim(), f.Value ?? string.Empty))
                    .ToList()
                : [];

            var fixedVars = BatchExecutionHelper.BuildFixedVariableDictionary(step.Variables);

            var rawAxes = step.Axes.Count > 0
                ? (IReadOnlyList<BatchAxis>)step.Axes
                : BatchExecutionHelper.BuildDefaultStepAxes(step.VaryingParams, step.Variables);

            var axes = BuildAxes(rawAxes);
            var existingListAxes = new HashSet<string>(
                axes.Where(a => a.Kind == "list").Select(a => a.Name),
                StringComparer.Ordinal);
            foreach (var listVar in TemplateScanner.ExtractListVariables(step.BuildTemplateRequest()))
            {
                if (existingListAxes.Add(listVar.Name))
                {
                    axes.Add(new CliAxisSpec("list", listVar.Name, null, []));
                }
            }

            var queryParams = BuildQueryParams(step, rawAxes);
            var extractions = BuildExtractions(step.Extractions);

            specs.Add(new CliStepSpec(
                rank,
                name,
                method,
                url,
                timeoutSeconds,
                delayAfterMs,
                step.ContinueOnError,
                mode,
                bodyType,
                body,
                headers,
                queryParams,
                formFields,
                fixedVars,
                axes,
                extractions));
        }

        return specs;
    }

    private static List<CliHeaderSpec> BuildHeaders(ChainStep step)
    {
        var headers = new List<CliHeaderSpec>();
        foreach (var h in step.Headers)
        {
            if (!h.IsEnabled || string.IsNullOrWhiteSpace(h.Key))
            {
                continue;
            }

            var key = h.Key.Trim();
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers.Add(new CliHeaderSpec(key, h.Value ?? string.Empty));
        }

        switch (step.ParsedAuth.Type)
        {
            case AuthType.Bearer when !string.IsNullOrWhiteSpace(step.ParsedAuth.Token):
                RemoveHeaderIgnoreCase(headers, "Authorization");
                headers.Add(new CliHeaderSpec("Authorization", $"Bearer {step.ParsedAuth.Token.Trim()}"));
                break;

            case AuthType.Basic:
                RemoveHeaderIgnoreCase(headers, "Authorization");
                var raw = $"{step.ParsedAuth.Username ?? string.Empty}:{step.ParsedAuth.Password ?? string.Empty}";
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                headers.Add(new CliHeaderSpec("Authorization", $"Basic {b64}"));
                break;

            case AuthType.ApiKey when step.ParsedAuth.ApiKeyInHeader && !string.IsNullOrWhiteSpace(step.ParsedAuth.ApiKeyName):
                var keyName = step.ParsedAuth.ApiKeyName.Trim();
                RemoveHeaderIgnoreCase(headers, keyName);
                headers.Add(new CliHeaderSpec(keyName, step.ParsedAuth.ApiKeyValue ?? string.Empty));
                break;
        }

        if (step.BodyType != BodyType.None &&
            !headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            var defaultContentType = step.BodyType switch
            {
                BodyType.Json => "application/json; charset=utf-8",
                BodyType.Text => "text/plain; charset=utf-8",
                BodyType.FormUrlEncoded => "application/x-www-form-urlencoded; charset=utf-8",
                _ => null
            };

            if (defaultContentType is not null)
            {
                headers.Add(new CliHeaderSpec("Content-Type", defaultContentType));
            }
        }

        return headers;
    }

    private static string BuildBody(ChainStep step, IReadOnlyList<CliHeaderSpec> headers)
    {
        if (step.BodyType is BodyType.None or BodyType.FormUrlEncoded)
        {
            return string.Empty;
        }

        var body = step.Body ?? string.Empty;
        if (step.BodyType == BodyType.Text)
        {
            var ct = headers.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
            if (ct is not null && ct.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                body = body
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\r", "\n", StringComparison.Ordinal)
                    .Replace("\n", "\r\n", StringComparison.Ordinal);
            }
        }

        return body;
    }

    private static List<CliAxisSpec> BuildAxes(IReadOnlyList<BatchAxis> rawAxes)
    {
        var result = new List<CliAxisSpec>(rawAxes.Count);

        foreach (var axis in rawAxes)
        {
            if (axis.Kind == BatchAxisKind.ExtractedList)
            {
                result.Add(new CliAxisSpec("list", axis.Name, null, []));
                continue;
            }

            var kind = axis.Kind == BatchAxisKind.Variable ? "var" : "param";
            if (axis.Kind == BatchAxisKind.UrlParam && axis.SourceParam is { } p)
            {
                if (BatchAxis.TryComputeRangeCount(p.Start, p.End, p.Step, 0, out _, out _))
                {
                    result.Add(new CliAxisSpec(
                        kind,
                        axis.Name,
                        new CliRangeSpec(
                            p.Start.ToString(CultureInfo.InvariantCulture),
                            p.End.ToString(CultureInfo.InvariantCulture),
                            p.Step.ToString(CultureInfo.InvariantCulture),
                            Base: 10,
                            Width: 0,
                            UppercaseHex: true,
                            UsePrefix: false),
                        []));
                    continue;
                }
            }
            else if (axis.Kind == BatchAxisKind.Variable && axis.SourceVariable is { } v)
            {
                if (v.Mode == VariableMode.Range && !v.HasError)
                {
                    result.Add(new CliAxisSpec(
                        kind,
                        axis.Name,
                        new CliRangeSpec(
                            v.ParsedStart.ToString(CultureInfo.InvariantCulture),
                            v.ParsedEnd.ToString(CultureInfo.InvariantCulture),
                            v.ParsedStep.ToString(CultureInfo.InvariantCulture),
                            (int)v.Base,
                            v.Width,
                            v.UppercaseHex,
                            v.UsePrefix),
                        []));
                    continue;
                }

                if (v.Mode == VariableMode.List)
                {
                    result.Add(new CliAxisSpec(kind, axis.Name, null, BatchVariable.ParseListValues(v.ListText)));
                    continue;
                }

                if (v.Mode == VariableMode.Random)
                {
                    result.Add(new CliAxisSpec(kind, axis.Name, null, v.GetRandomValuesSnapshot().ToList()));
                    continue;
                }
            }

            var fallbackValues = new List<string>((int)Math.Min(axis.ValueCount, 10_000));
            for (long idx = 0; idx < axis.ValueCount && idx < 10_000; idx++)
            {
                fallbackValues.Add(axis.GetValue(idx));
            }
            result.Add(new CliAxisSpec(kind, axis.Name, null, fallbackValues));
        }

        return result;
    }

    private static List<CliQueryParamSpec> BuildQueryParams(ChainStep step, IReadOnlyList<BatchAxis> rawAxes)
    {
        var enabledParams = step.VaryingParams
            .Where(p => p.IsEnabled && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        var axisIndexByParamIndex = new Dictionary<int, int>();
        var matchedParamIndices = new HashSet<int>();

        for (var aIdx = 0; aIdx < rawAxes.Count; aIdx++)
        {
            var axis = rawAxes[aIdx];
            if (axis.Kind != BatchAxisKind.UrlParam)
            {
                continue;
            }

            var resolved = -1;
            if (axis.SourceParam is not null)
            {
                for (var pIdx = 0; pIdx < enabledParams.Count; pIdx++)
                {
                    if (!matchedParamIndices.Contains(pIdx) && ReferenceEquals(enabledParams[pIdx], axis.SourceParam))
                    {
                        resolved = pIdx;
                        break;
                    }
                }
            }

            if (resolved < 0)
            {
                for (var pIdx = 0; pIdx < enabledParams.Count; pIdx++)
                {
                    if (!matchedParamIndices.Contains(pIdx) &&
                        enabledParams[pIdx].IsVarying &&
                        enabledParams[pIdx].Name.Trim().Equals(axis.Name, StringComparison.Ordinal))
                    {
                        resolved = pIdx;
                        break;
                    }
                }
            }

            if (resolved >= 0)
            {
                matchedParamIndices.Add(resolved);
                axisIndexByParamIndex[resolved] = aIdx;
            }
        }

        var queryParams = new List<CliQueryParamSpec>(enabledParams.Count + 1);
        for (var pIdx = 0; pIdx < enabledParams.Count; pIdx++)
        {
            var param = enabledParams[pIdx];
            int? axisIdx = axisIndexByParamIndex.TryGetValue(pIdx, out var foundIdx) ? foundIdx : null;
            queryParams.Add(new CliQueryParamSpec(
                param.Name.Trim(),
                param.Value ?? string.Empty,
                axisIdx));
        }

        if (step.ParsedAuth.Type == AuthType.ApiKey &&
            !step.ParsedAuth.ApiKeyInHeader &&
            !string.IsNullOrWhiteSpace(step.ParsedAuth.ApiKeyName))
        {
            queryParams.Add(new CliQueryParamSpec(
                step.ParsedAuth.ApiKeyName.Trim(),
                step.ParsedAuth.ApiKeyValue ?? string.Empty,
                AxisIndex: null));
        }

        return queryParams;
    }

    private static List<CliExtractionSpec> BuildExtractions(IReadOnlyList<ChainExtraction> extractions)
    {
        var result = new List<CliExtractionSpec>(extractions.Count);
        foreach (var ext in extractions)
        {
            if (string.IsNullOrWhiteSpace(ext.VariableName) || string.IsNullOrWhiteSpace(ext.Expression))
            {
                continue;
            }

            result.Add(new CliExtractionSpec(
                ext.VariableName.Trim(),
                ext.Source == ChainExtractionSource.Header ? "header" : "body",
                ext.Kind == ExtractionKind.Regex ? "regex" : "jsonpath",
                ext.Expression.Trim(),
                Math.Max(0, ext.RegexGroup),
                ext.HeaderName?.Trim() ?? string.Empty,
                ext.IsRequired,
                ext.DefaultValueIfMissing ?? string.Empty,
                ext.IsList));
        }

        return result;
    }

    private static void RemoveHeaderIgnoreCase(List<CliHeaderSpec> headers, string key)
    {
        for (var i = headers.Count - 1; i >= 0; i--)
        {
            if (headers[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                headers.RemoveAt(i);
            }
        }
    }
}
