using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ApiTester.Models;

namespace ApiTester.Services;

internal static class PhpChainCliEmitter
{
    public static string Emit(IReadOnlyList<CliStepSpec> steps)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("#!/usr/bin/env php");
        sb.AppendLine("<?php");
        sb.AppendLine("// Script CLI généré par ApiTester (PHP 8.0+ avec extension cURL)");
        sb.AppendLine("// Utilisation : php chaine_api.php [--verbose]");
        sb.AppendLine();
        sb.AppendLine("declare(strict_types=1);");
        sb.AppendLine();
        sb.AppendLine("if (!function_exists('curl_init')) {");
        sb.AppendLine("    fwrite(STDERR, \"Extension PHP cURL requise.\\n\");");
        sb.AppendLine("    exit(1);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("const MARKER_RE = '/(?:\\{\\{\\s*(\\$(?![0-9])[\\p{L}\\p{N}_]+|(?![0-9])(?!\\$)[\\p{L}\\p{N}_]+(?:(?:\\.[^.{}\\[\\]\\r\\n\\s]+|\\[[^{}\\[\\]\\r\\n]*\\])+)?)\\s*\\}\\}|\\[\\[\\s*((?![0-9])(?!\\$)[\\p{L}\\p{N}_]+(?:(?:\\.[^.{}\\[\\]\\r\\n\\s]+|\\[[^{}\\[\\]\\r\\n]*\\])+)?)\\s*\\]\\])/u';");
        sb.AppendLine();

        AppendStepsData(sb, steps);
        AppendRuntimeHelpers(sb);

        return sb.ToString();
    }

    private static void AppendStepsData(StringBuilder sb, IReadOnlyList<CliStepSpec> steps)
    {
        sb.AppendLine("$steps = [");
        foreach (var step in steps)
        {
            sb.AppendLine("    [");
            sb.AppendLine($"        'rank' => {step.Rank},");
            sb.AppendLine($"        'name' => {EscapeString(step.Name)},");
            sb.AppendLine($"        'method' => {EscapeString(step.Method)},");
            sb.AppendLine($"        'url' => {EscapeString(step.Url)},");
            sb.AppendLine($"        'timeoutSeconds' => {step.TimeoutSeconds},");
            sb.AppendLine($"        'delayAfterMs' => {step.DelayAfterMs},");
            sb.AppendLine($"        'continueOnError' => {FormatBool(step.ContinueOnError)},");
            sb.AppendLine($"        'mode' => {EscapeString(step.Mode)},");
            sb.AppendLine($"        'bodyType' => {EscapeString(step.BodyType)},");
            sb.AppendLine($"        'body' => {EscapeString(step.Body)},");
            sb.AppendLine($"        'isReturnOnly' => {FormatBool(step.IsReturnOnly)},");
            sb.AppendLine($"        'returnNode' => {FormatReturnNode(step.ReturnNode)},");
            sb.AppendLine($"        'returnError' => {(step.ReturnError is null ? "null" : EscapeString(step.ReturnError))},");

            sb.AppendLine("        'headers' => [");
            foreach (var h in step.Headers)
            {
                sb.AppendLine($"            [{EscapeString(h.Key)}, {EscapeString(h.Value)}],");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        'queryParams' => [");
            foreach (var qp in step.QueryParams)
            {
                var axisIdx = qp.AxisIndex.HasValue
                    ? qp.AxisIndex.Value.ToString(CultureInfo.InvariantCulture)
                    : "null";
                sb.AppendLine($"            ['key' => {EscapeString(qp.Key)}, 'value' => {EscapeString(qp.Value)}, 'axisIndex' => {axisIdx}],");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        'formFields' => [");
            foreach (var ff in step.FormFields)
            {
                sb.AppendLine($"            [{EscapeString(ff.Key)}, {EscapeString(ff.Value)}],");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        'fixedVariables' => [");
            foreach (var kv in step.FixedVariables)
            {
                sb.AppendLine($"            {EscapeString(kv.Key)} => {EscapeString(kv.Value)},");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        'axes' => [");
            foreach (var axis in step.Axes)
            {
                sb.AppendLine("            [");
                sb.AppendLine($"                'kind' => {EscapeString(axis.Kind)},");
                sb.AppendLine($"                'name' => {EscapeString(axis.Name)},");
                if (axis.Range is { } r)
                {
                    sb.AppendLine($"                'range' => ['start' => {EscapeString(r.Start)}, 'end' => {EscapeString(r.End)}, 'step' => {EscapeString(r.Step)}, 'base' => {r.Base}, 'width' => {r.Width}, 'uppercase' => {FormatBool(r.UppercaseHex)}, 'prefix' => {FormatBool(r.UsePrefix)}],");
                }
                else
                {
                    sb.Append("                'values' => [");
                    for (var i = 0; i < axis.Values.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(EscapeString(axis.Values[i]));
                    }
                    sb.AppendLine("],");
                }
                sb.AppendLine("            ],");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        'extractions' => [");
            foreach (var ext in step.Extractions)
            {
                sb.AppendLine("            [");
                sb.AppendLine($"                'variable' => {EscapeString(ext.VariableName)},");
                sb.AppendLine($"                'source' => {EscapeString(ext.Source)},");
                sb.AppendLine($"                'kind' => {EscapeString(ext.Kind)},");
                sb.AppendLine($"                'expression' => {EscapeString(ext.Expression)},");
                sb.AppendLine($"                'regexGroup' => {ext.RegexGroup},");
                sb.AppendLine($"                'headerName' => {EscapeString(ext.HeaderName)},");
                sb.AppendLine($"                'sourceVariable' => {EscapeString(ext.SourceVariableName)},");
                sb.AppendLine($"                'required' => {FormatBool(ext.IsRequired)},");
                sb.AppendLine($"                'defaultValue' => {EscapeString(ext.DefaultValueIfMissing)},");
                sb.AppendLine($"                'isList' => {FormatBool(ext.IsList)},");
                sb.AppendLine($"                'conditionMode' => {EscapeString(ext.ConditionMatchMode)},");
                sb.AppendLine($"                'deduplicate' => {FormatBool(ext.DeduplicateList)},");
                sb.AppendLine("                'conditions' => [");
                foreach (var c in ext.Conditions)
                {
                    var thenPhp = $"['action' => {EscapeString(c.ThenBranch.Action)}, 'value' => {EscapeString(c.ThenBranch.Value)}, 'replacement' => {EscapeString(c.ThenBranch.Replacement)}, 'regexGroup' => {c.ThenBranch.RegexGroup}, 'segmentIndex' => {c.ThenBranch.SegmentIndex}, 'ignoreCase' => {FormatBool(c.ThenBranch.IgnoreCase)}]";
                    var elsePhp = $"['action' => {EscapeString(c.ElseBranch.Action)}, 'value' => {EscapeString(c.ElseBranch.Value)}, 'replacement' => {EscapeString(c.ElseBranch.Replacement)}, 'regexGroup' => {c.ElseBranch.RegexGroup}, 'segmentIndex' => {c.ElseBranch.SegmentIndex}, 'ignoreCase' => {FormatBool(c.ElseBranch.IgnoreCase)}]";
                    sb.AppendLine($"                    ['isConditional' => {FormatBool(c.IsConditional)}, 'op' => {EscapeString(c.Operator)}, 'value' => {EscapeString(c.Value)}, 'replacement' => {EscapeString(c.Replacement)}, 'regexGroup' => {c.RegexGroup}, 'segmentIndex' => {c.SegmentIndex}, 'ignoreCase' => {FormatBool(c.IgnoreCase)}, 'thenBranch' => {thenPhp}, 'elseBranch' => {elsePhp}],");
                }
                sb.AppendLine("                ],");
                sb.AppendLine("            ],");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("    ],");
        }
        sb.AppendLine("];");
        sb.AppendLine();
    }

    private static void AppendRuntimeHelpers(StringBuilder sb)
    {
        sb.AppendLine("""
function generateUuidV4(): string {
    $data = random_bytes(16);
    $data[6] = chr((ord($data[6]) & 0x0f) | 0x40);
    $data[8] = chr((ord($data[8]) & 0x3f) | 0x80);
    return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($data), 4));
}

function formatNumber(int $value, int $baseNum, int $width, bool $uppercaseHex, bool $usePrefix): string {
    $negative = $value < 0;
    $magnitude = $negative ? -$value : $value;
    $prefix = '';
    if ($baseNum === 2) {
        $digits = decbin($magnitude);
        if ($usePrefix) $prefix = '0b';
    } elseif ($baseNum === 8) {
        $digits = decoct($magnitude);
        if ($usePrefix) $prefix = '0o';
    } elseif ($baseNum === 16) {
        $digits = dechex($magnitude);
        if ($uppercaseHex) $digits = strtoupper($digits);
        if ($usePrefix) $prefix = $uppercaseHex ? '0X' : '0x';
    } else {
        $digits = (string)$magnitude;
    }
    if ($width > 0 && strlen($digits) < $width) {
        $digits = str_pad($digits, $width, '0', STR_PAD_LEFT);
    }
    return ($negative ? '-' : '') . $prefix . $digits;
}

function makeRange(array $spec): array {
    $start = (int)$spec['start'];
    $end = (int)$spec['end'];
    $step = (int)$spec['step'];
    if ($step === 0 || ($start <= $end && $step < 0) || ($start > $end && $step > 0)) {
        return [];
    }
    $values = [];
    for ($cur = $start; $step > 0 ? $cur <= $end : $cur >= $end; $cur += $step) {
        $values[] = formatNumber($cur, $spec['base'], $spec['width'], $spec['uppercase'], $spec['prefix']);
    }
    return $values;
}

const ALLOWED_URI_CHARS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:/?#[]@!$&\'()*+,;=%';

function splitVarPath(string $expr): array {
    $trimmed = trim($expr);
    if ($trimmed === '' || str_starts_with($trimmed, '$')) {
        return [$trimmed, null];
    }
    $dotIdx = strpos($trimmed, '.');
    $brIdx = strpos($trimmed, '[');
    $sepIdx = false;
    if ($dotIdx !== false && $brIdx !== false) {
        $sepIdx = min($dotIdx, $brIdx);
    } elseif ($dotIdx !== false) {
        $sepIdx = $dotIdx;
    } elseif ($brIdx !== false) {
        $sepIdx = $brIdx;
    }
    if ($sepIdx === false || $sepIdx <= 0) {
        return [$trimmed, null];
    }
    $root = trim(substr($trimmed, 0, $sepIdx));
    $rawPath = trim($trimmed[$sepIdx] === '.' ? substr($trimmed, $sepIdx + 1) : substr($trimmed, $sepIdx));
    if ($root === '' || $rawPath === '') {
        return [$trimmed, null];
    }
    return [$root, $rawPath];
}

function isInQueryValueContext(string $prefix): bool {
    if (str_contains($prefix, '#')) return false;
    $qIdx = strpos($prefix, '?');
    if ($qIdx === false) return false;
    $afterQ = substr($prefix, $qIdx + 1);
    $ampIdx = strrpos($afterQ, '&');
    $currentPair = $ampIdx !== false ? substr($afterQ, $ampIdx + 1) : $afterQ;
    return str_contains($currentPair, '=');
}

function escapeUrlPreservingStructure(string $value): string {
    if ($value === '') return '';
    $out = '';
    $len = strlen($value);
    for ($i = 0; $i < $len; $i++) {
        $byte = $value[$i];
        if (str_contains(ALLOWED_URI_CHARS, $byte)) {
            $out .= $byte;
        } else {
            $out .= sprintf('%%%02X', ord($byte));
        }
    }
    return $out;
}

function buildIterations(array $axes, string $mode, array $accumulatedLists, array $scalarVars): array {
    if (count($axes) === 0) {
        return [[[]], null, null];
    }
    $lists = [];
    foreach ($axes as $axis) {
        if ($axis['kind'] === 'list') {
            [$root, $jp] = splitVarPath($axis['name']);
            if ($jp !== null) {
                if (array_key_exists($root, $scalarVars)) {
                    [$ok, $res] = extractJsonPathList((string)$scalarVars[$root], $jp);
                } elseif (array_key_exists($root, $accumulatedLists)) {
                    [$ok, $res] = extractJsonPathListFromValues($accumulatedLists[$root], $jp);
                } else {
                    return [[], null, "Variable ou liste « {$root} » introuvable pour [[{$axis['name']}]]."];
                }
                if ($ok && is_array($res) && count($res) > 0) {
                    $list = $res;
                } elseif (is_string($res) && stripos($res, 'est vide') !== false) {
                    return [[], $axis['name'], null];
                } else {
                    return [[], null, "[[{$axis['name']}]] : {$res}"];
                }
            } else {
                $list = $accumulatedLists[$axis['name']] ?? [];
                if (count($list) === 0) {
                    return [[], $axis['name'], null];
                }
            }
            $lists[] = $list;
        } else {
            $list = isset($axis['range']) ? makeRange($axis['range']) : $axis['values'];
            if (count($list) === 0) {
                return [[], null, null];
            }
            $lists[] = $list;
        }
    }
    if ($mode === 'parallel') {
        $minLen = min(array_map('count', $lists));
        $rows = [];
        for ($i = 0; $i < $minLen; $i++) {
            $row = [];
            foreach ($lists as $list) {
                $row[] = $list[$i];
            }
            $rows[] = $row;
        }
        return [$rows, null, null];
    }
    $acc = [[]];
    foreach ($lists as $list) {
        $next = [];
        foreach ($acc as $prefix) {
            foreach ($list as $item) {
                $copy = $prefix;
                $copy[] = $item;
                $next[] = $copy;
            }
        }
        $acc = $next;
    }
    return [$acc, null, null];
}

function resolveMarker(?string $scalarName, ?string $listName, array $variables, array $listVars, ?array $accumulatedLists, int $globalIndex, string $reqGuid, string $reqTs): string {
    if ($listName !== null && $listName !== '') {
        if (array_key_exists($listName, $listVars)) {
            return (string)$listVars[$listName];
        }
        [$root, $jp] = splitVarPath($listName);
        if ($jp !== null) {
            if (array_key_exists($root, $variables)) {
                [$ok, $res] = extractJsonPathList((string)$variables[$root], $jp);
                if ($ok && is_array($res) && count($res) > 0) {
                    return (string)$res[0];
                }
                throw new RuntimeException("[[{$listName}]] : " . (is_string($res) ? $res : 'Erreur JSONPath'));
            }
            if ($accumulatedLists !== null && array_key_exists($root, $accumulatedLists)) {
                [$ok, $res] = extractJsonPathListFromValues($accumulatedLists[$root], $jp);
                if ($ok && is_array($res) && count($res) > 0) {
                    return (string)$res[0];
                }
                throw new RuntimeException("[[{$listName}]] : " . (is_string($res) ? $res : 'Erreur JSONPath'));
            }
        }
        throw new RuntimeException("Liste inconnue ou non définie : [[" . $listName . "]]");
    }
    $name = (string)$scalarName;
    if (array_key_exists($name, $variables)) {
        return (string)$variables[$name];
    }
    if ($name === '$index') return (string)$globalIndex;
    if ($name === '$guid') return $reqGuid;
    if ($name === '$timestamp') return $reqTs;
    [$root, $jp] = splitVarPath($name);
    if ($jp !== null) {
        if (array_key_exists($root, $variables)) {
            [$ok, $res] = extractJsonPath((string)$variables[$root], $jp);
            if (!$ok) {
                throw new RuntimeException("{{" . $name . "}} : " . (string)$res);
            }
            return (string)$res;
        }
        if ($accumulatedLists !== null && array_key_exists($root, $accumulatedLists)) {
            [$ok, $res] = extractJsonPathFromValues($accumulatedLists[$root], $jp);
            if (!$ok) {
                throw new RuntimeException("{{" . $name . "}} : " . (string)$res);
            }
            return (string)$res;
        }
        throw new RuntimeException("Variable inconnue ou non définie : {{" . $root . "}} (pour {{" . $name . "}})");
    }
    throw new RuntimeException("Variable inconnue ou non définie : {{" . $name . "}}");
}

function substituteUrl(string $text, array $variables, array $listVars, ?array $accumulatedLists, int $globalIndex, string $reqGuid, string $reqTs): string {
    if ($text === '') {
        return '';
    }
    if (!preg_match_all(MARKER_RE, $text, $matches, PREG_OFFSET_CAPTURE)) {
        return $text;
    }
    $out = '';
    $lastOffset = 0;
    foreach ($matches[0] as $idx => $fullMatch) {
        $matchStr = $fullMatch[0];
        $matchOffset = $fullMatch[1];
        $scalarName = $matches[1][$idx][0] ?? '';
        $listName = (isset($matches[2][$idx]) && $matches[2][$idx][1] >= 0) ? $matches[2][$idx][0] : '';

        $out .= substr($text, $lastOffset, $matchOffset - $lastOffset);
        $val = resolveMarker($scalarName, $listName, $variables, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
        if (isInQueryValueContext($out)) {
            $out .= rawurlencode($val);
        } else {
            $hasNonWsPrefix = trim($out) !== '';
            $out .= escapeUrlPreservingStructure($hasNonWsPrefix ? $val : trim($val));
        }
        $lastOffset = $matchOffset + strlen($matchStr);
    }
    $out .= substr($text, $lastOffset);
    return $out;
}

function substituteSimple(string $text, array $variables, array $listVars, ?array $accumulatedLists, int $globalIndex, string $reqGuid, string $reqTs, bool $urlEncode = false): string {
    if ($text === '') {
        return '';
    }
    return (string)preg_replace_callback(
        MARKER_RE,
        static function (array $match) use ($variables, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs, $urlEncode): string {
            $val = resolveMarker($match[1] ?? null, $match[2] ?? null, $variables, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
            return $urlEncode ? rawurlencode($val) : $val;
        },
        $text
    );
}

function substituteJsonBody(string $text, array $variables, array $listVars, ?array $accumulatedLists, int $globalIndex, string $reqGuid, string $reqTs): string {
    if ($text === '') {
        return '';
    }
    if (!preg_match_all(MARKER_RE, $text, $matches, PREG_OFFSET_CAPTURE)) {
        return $text;
    }

    $out = '';
    $lastScanned = 0;
    $inString = false;
    $escaped = false;

    foreach ($matches[0] as $idx => $fullMatch) {
        $matchStr = $fullMatch[0];
        $matchOffset = $fullMatch[1];
        $scalarName = $matches[1][$idx][0] ?? '';
        $listName = (isset($matches[2][$idx]) && $matches[2][$idx][1] >= 0) ? $matches[2][$idx][0] : '';

        for ($i = $lastScanned; $i < $matchOffset; $i++) {
            $ch = $text[$i];
            if ($inString) {
                if ($escaped) {
                    $escaped = false;
                } elseif ($ch === '\\') {
                    $escaped = true;
                } elseif ($ch === '"') {
                    $inString = false;
                }
            } elseif ($ch === '"') {
                $inString = true;
                $escaped = false;
            }
        }

        $out .= substr($text, $lastScanned, $matchOffset - $lastScanned);
        $val = resolveMarker($scalarName, $listName, $variables, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
        if ($inString) {
            $encoded = json_encode($val, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
            $out .= ($encoded !== false && strlen($encoded) >= 2) ? substr($encoded, 1, -1) : '';
        } else {
            $out .= $val;
        }
        $lastScanned = $matchOffset + strlen($matchStr);
    }

    $out .= substr($text, $lastScanned);
    return $out;
}

function buildFinalUrl(string $rawUrl, array $queryItems): string {
    $trimmed = trim($rawUrl);
    if (count($queryItems) === 0) {
        return $trimmed;
    }
    $parts = [];
    foreach ($queryItems as [$k, $v]) {
        $parts[] = rawurlencode((string)$k) . '=' . rawurlencode((string)$v);
    }
    $querySuffix = implode('&', $parts);
    $hashPos = strpos($trimmed, '#');
    $fragment = $hashPos !== false ? substr($trimmed, $hashPos) : '';
    $baseWithoutHash = $hashPos !== false ? substr($trimmed, 0, $hashPos) : $trimmed;
    $sep = str_contains($baseWithoutHash, '?') ? '&' : '?';
    return $baseWithoutHash . $sep . $querySuffix . $fragment;
}

function parseJsonPath(string $rawPath): array {
    $path = trim($rawPath);
    if ($path === '$') {
        return [];
    }
    if (str_starts_with($path, '$.')) {
        $path = substr($path, 2);
    } elseif (str_starts_with($path, '$')) {
        $path = substr($path, 1);
    }
    if (trim($path) === '') {
        throw new RuntimeException('Chemin JSONPath vide.');
    }

    $steps = [];
    $pos = 0;
    $len = strlen($path);
    while ($pos < $len) {
        if ($path[$pos] === '.') {
            if ($pos === 0 || $pos + 1 >= $len || $path[$pos + 1] === '.') {
                throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
            }
            $pos++;
            continue;
        }
        if ($path[$pos] === '[') {
            while ($pos < $len && $path[$pos] === '[') {
                $cursor = $pos + 1;
                while ($cursor < $len && ctype_space($path[$cursor])) {
                    $cursor++;
                }
                if ($cursor < $len && ($path[$cursor] === '"' || $path[$cursor] === "'")) {
                    $q = $path[$cursor];
                    $cursor++;
                    $key = '';
                    $closedQuote = false;
                    while ($cursor < $len) {
                        $ch = $path[$cursor++];
                        if ($ch === '\\' && $cursor < $len) {
                            $esc = $path[$cursor++];
                            $key .= match ($esc) {
                                '\\' => '\\',
                                '"' => '"',
                                "'" => "'",
                                'n' => "\n",
                                'r' => "\r",
                                't' => "\t",
                                default => $esc,
                            };
                            continue;
                        }
                        if ($ch === $q) {
                            $closedQuote = true;
                            break;
                        }
                        $key .= $ch;
                    }
                    if (!$closedQuote) {
                        throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
                    }
                    while ($cursor < $len && ctype_space($path[$cursor])) {
                        $cursor++;
                    }
                    if ($cursor >= $len || $path[$cursor] !== ']') {
                        throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
                    }
                    $steps[] = [$key, null, false];
                    $pos = $cursor + 1;
                    continue;
                }
                $close = strpos($path, ']', $pos + 1);
                if ($close === false) {
                    throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
                }
                $idxStr = trim(substr($path, $pos + 1, $close - $pos - 1));
                if ($idxStr === '*' || $idxStr === '' || $idxStr === '++' || preg_match('/^(?:[A-Za-z_]\w*\+\+|\+\+[A-Za-z_]\w*)$/', $idxStr)) {
                    $steps[] = [null, null, true];
                } elseif (preg_match('/^-?\d+$/', $idxStr)) {
                    $steps[] = [null, (int)$idxStr, false];
                } else {
                    throw new RuntimeException("Index invalide dans {$rawPath}");
                }
                $pos = $close + 1;
            }
            if ($pos < $len && $path[$pos] !== '.') {
                throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
            }
            continue;
        }
        $segStart = $pos;
        while ($pos < $len && $path[$pos] !== '.' && $path[$pos] !== '[' && $path[$pos] !== ']') {
            $pos++;
        }
        if ($pos < $len && $path[$pos] === ']') {
            throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
        }
        $prop = substr($path, $segStart, $pos - $segStart);
        if ($prop === '') {
            throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
        }
        $steps[] = [$prop, null, false];
    }
    return $steps;
}

function formatJsonValue(mixed $current): string {
    if (is_string($current)) return $current;
    if (is_bool($current)) return $current ? 'true' : 'false';
    if ($current === null) return 'null';
    if (is_int($current) || is_float($current)) return (string)$current;
    return (string)json_encode($current, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
}

function unwrapJsonString(mixed $val): mixed {
    if (!is_string($val)) return $val;
    $trimmed = ltrim($val, "\xEF\xBB\xBF \t\r\n");
    if ($trimmed === '' || ($trimmed[0] !== '{' && $trimmed[0] !== '[')) {
        return $val;
    }
    try {
        return json_decode($trimmed, false, 512, JSON_THROW_ON_ERROR);
    } catch (Throwable) {
        return $val;
    }
}

function getMaxContiguousPropIdx(array $steps, int $startIdx): int {
    $end = $startIdx;
    $count = count($steps);
    while ($end + 1 < $count) {
        $nxt = $steps[$end + 1];
        if ($nxt[0] === null || $nxt[1] !== null || $nxt[2]) {
            break;
        }
        $curr = $steps[$end];
        if ($curr[1] !== null || $curr[2]) {
            break;
        }
        $end++;
    }
    return $end;
}

function looksLikeJsonArray(?string $raw): bool {
    if ($raw === null || trim($raw) === '') return false;
    return str_starts_with(ltrim($raw, "\xEF\xBB\xBF \t\r\n"), '[');
}

function extractJsonPathSteps(string $body, array $steps, int $startIdx = 0): array {
    try {
        if (trim($body) === '') {
            return [false, 'Le corps est vide (JSON attendu).'];
        }
        $clean = ltrim($body, "\xEF\xBB\xBF");
        $current = json_decode($clean, false, 512, JSON_THROW_ON_ERROR);
        $count = count($steps);
        for ($i = $startIdx; $i < $count; $i++) {
            [$prop, $arrIdx] = $steps[$i];
            if ($prop !== null) {
                $current = unwrapJsonString($current);
                if (!is_object($current)) {
                    return [false, "Propriété JSON introuvable : {$prop}"];
                }
                if (property_exists($current, $prop)) {
                    $current = $current->{$prop};
                } else {
                    $matched = false;
                    $maxEnd = getMaxContiguousPropIdx($steps, $i);
                    for ($endIdx = $maxEnd; $endIdx > $i; $endIdx--) {
                        $parts = [];
                        for ($k = $i; $k <= $endIdx; $k++) {
                            $parts[] = (string)$steps[$k][0];
                        }
                        $joined = implode('.', $parts);
                        if (property_exists($current, $joined)) {
                            $current = $current->{$joined};
                            $i = $endIdx;
                            $matched = true;
                            break;
                        }
                    }
                    if (!$matched) {
                        return [false, "Propriété JSON introuvable : {$prop}"];
                    }
                }
            }
            if ($arrIdx !== null) {
                $current = unwrapJsonString($current);
                if (!is_array($current)) {
                    return [false, "Index [{$arrIdx}] inapplicable"];
                }
                $len = count($current);
                $resolved = $arrIdx < 0 ? $len + $arrIdx : $arrIdx;
                if ($resolved < 0 || $resolved >= $len) {
                    return [false, "Index [{$arrIdx}] hors limites"];
                }
                $current = $current[$resolved];
            }
        }
        return [true, formatJsonValue($current)];
    } catch (Throwable $e) {
        return [false, $e->getMessage()];
    }
}

function extractJsonPath(string $body, string $rawPath): array {
    try {
        $steps = parseJsonPath($rawPath);
        foreach ($steps as [, , $isWildcard]) {
            if ($isWildcard) {
                return [false, 'Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]].'];
            }
        }
        return extractJsonPathSteps($body, $steps, 0);
    } catch (Throwable $e) {
        return [false, $e->getMessage()];
    }
}

function extractJsonPathListSteps(string $body, array $steps, int $startIdx = 0): array {
    try {
        if (trim($body) === '') {
            return [false, 'Le corps est vide (JSON attendu).'];
        }
        $clean = ltrim($body, "\xEF\xBB\xBF");
        $currentElements = [json_decode($clean, false, 512, JSON_THROW_ON_ERROR)];
        $hasWildcard = false;
        $count = count($steps);

        for ($i = $startIdx; $i < $count; $i++) {
            [$prop, $arrIdx, $isWildcard] = $steps[$i];
            if ($prop !== null) {
                $exactMatches = [];
                $allExact = true;
                foreach ($currentElements as $rawEl) {
                    $el = unwrapJsonString($rawEl);
                    if (!is_object($el) || !property_exists($el, $prop)) {
                        $allExact = false;
                        continue;
                    }
                    $exactMatches[] = $el->{$prop};
                }
                if (count($exactMatches) > 0 && ($allExact || $hasWildcard)) {
                    $currentElements = $exactMatches;
                } else {
                    $matchedJoined = false;
                    $maxEnd = getMaxContiguousPropIdx($steps, $i);
                    for ($endIdx = $maxEnd; $endIdx > $i; $endIdx--) {
                        $parts = [];
                        for ($k = $i; $k <= $endIdx; $k++) {
                            $parts[] = (string)$steps[$k][0];
                        }
                        $joined = implode('.', $parts);
                        $jMatches = [];
                        $allJ = true;
                        foreach ($currentElements as $rawEl) {
                            $el = unwrapJsonString($rawEl);
                            if (!is_object($el) || !property_exists($el, $joined)) {
                                $allJ = false;
                                continue;
                            }
                            $jMatches[] = $el->{$joined};
                        }
                        if (count($jMatches) > 0 && ($allJ || $hasWildcard)) {
                            $currentElements = $jMatches;
                            $i = $endIdx;
                            [$prop, $arrIdx, $isWildcard] = $steps[$i];
                            $matchedJoined = true;
                            break;
                        }
                    }
                    if (!$matchedJoined) {
                        if (count($exactMatches) > 0) {
                            $currentElements = $exactMatches;
                        } else {
                            return [false, "Propriété JSON introuvable : {$prop}"];
                        }
                    }
                }
            }

            if ($isWildcard) {
                $wasWildcard = $hasWildcard;
                $hasWildcard = true;
                $nextElements = [];
                foreach ($currentElements as $rawEl) {
                    $el = unwrapJsonString($rawEl);
                    if (!is_array($el)) {
                        if (!$wasWildcard) {
                            return [false, 'Le sélecteur [*] est inapplicable sur cet élément'];
                        }
                        continue;
                    }
                    foreach ($el as $item) {
                        $nextElements[] = $item;
                    }
                }
                if (count($nextElements) === 0) {
                    return [false, 'Le tableau JSON ciblé est vide.'];
                }
                $currentElements = $nextElements;
            } elseif ($arrIdx !== null) {
                $nextElements = [];
                foreach ($currentElements as $rawEl) {
                    $el = unwrapJsonString($rawEl);
                    if (!is_array($el)) {
                        if (!$hasWildcard) {
                            return [false, "Index [{$arrIdx}] inapplicable"];
                        }
                        continue;
                    }
                    $len = count($el);
                    $resolved = $arrIdx < 0 ? $len + $arrIdx : $arrIdx;
                    if ($resolved < 0 || $resolved >= $len) {
                        if (!$hasWildcard) {
                            return [false, "Index [{$arrIdx}] hors limites"];
                        }
                        continue;
                    }
                    $nextElements[] = $el[$resolved];
                }
                if (count($nextElements) === 0) {
                    return [false, "Aucune valeur trouvée pour l'index [{$arrIdx}]"];
                }
                $currentElements = $nextElements;
            }
        }

        if (!$hasWildcard && count($currentElements) === 1 && is_array($currentElements[0])) {
            if (count($currentElements[0]) === 0) {
                return [false, 'Le tableau JSON ciblé est vide.'];
            }
            return [true, array_map('formatJsonValue', $currentElements[0])];
        }

        if (count($currentElements) === 0) {
            return [false, "Aucune valeur extraite pour l'expression JSONPath."];
        }
        return [true, array_map('formatJsonValue', $currentElements)];
    } catch (Throwable $e) {
        return [false, $e->getMessage()];
    }
}

function extractJsonPathList(string $body, string $rawPath): array {
    try {
        $steps = parseJsonPath($rawPath);
        return extractJsonPathListSteps($body, $steps, 0);
    } catch (Throwable $e) {
        return [false, $e->getMessage()];
    }
}

function extractJsonPathFromValues(array $sourceValues, string $rawPath): array {
    $cnt = count($sourceValues);
    if ($cnt === 0) {
        return [false, 'La liste source est vide.'];
    }
    try {
        $steps = parseJsonPath($rawPath);
    } catch (Throwable $e) {
        return [false, $e->getMessage()];
    }
    foreach ($steps as [, , $isWild]) {
        if ($isWild) {
            return [false, 'Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]].'];
        }
    }
    if (count($steps) === 0) {
        return [true, $cnt === 1 ? (string)$sourceValues[0] : (string)json_encode(array_values($sourceValues), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)];
    }
    if ($steps[0][0] === null && $steps[0][1] !== null && ($cnt > 1 || !looksLikeJsonArray((string)$sourceValues[0]))) {
        $idx = $steps[0][1];
        $resolved = $idx < 0 ? $cnt + $idx : $idx;
        if ($resolved < 0 || $resolved >= $cnt) {
            return [false, "Index [{$idx}] hors limites (taille : {$cnt})"];
        }
        $chosen = (string)$sourceValues[$resolved];
        if (count($steps) === 1) {
            return [true, $chosen];
        }
        return extractJsonPathSteps($chosen, $steps, 1);
    }
    $lastErr = 'Aucune correspondance JSONPath dans la variable source.';
    foreach ($sourceValues as $item) {
        $res = extractJsonPathSteps((string)$item, $steps, 0);
        if ($res[0]) {
            return $res;
        }
        $lastErr = $res[1];
    }
    return [false, $lastErr];
}

function extractJsonPathListFromValues(array $sourceValues, string $rawPath): array {
    $cnt = count($sourceValues);
    if ($cnt === 0) {
        return [false, 'La liste source est vide.'];
    }
    try {
        $steps = parseJsonPath($rawPath);
    } catch (Throwable $e) {
        return [false, $e->getMessage()];
    }
    if (count($steps) === 0) {
        return [true, array_values(array_map('strval', $sourceValues))];
    }
    if ($steps[0][0] === null && ($steps[0][2] || $steps[0][1] !== null) && ($cnt > 1 || !looksLikeJsonArray((string)$sourceValues[0]))) {
        if ($steps[0][2]) {
            if (count($steps) === 1) {
                return [true, array_values(array_map('strval', $sourceValues))];
            }
            $collected = [];
            $lastErr = null;
            foreach ($sourceValues as $item) {
                [$ok, $res] = extractJsonPathListSteps((string)$item, $steps, 1);
                if ($ok && is_array($res)) {
                    foreach ($res as $v) $collected[] = $v;
                } else {
                    $lastErr = is_string($res) ? $res : null;
                }
            }
            return count($collected) > 0
                ? [true, $collected]
                : [false, $lastErr ?? "Aucune valeur extraite pour l'expression JSONPath."];
        }
        $idx = $steps[0][1];
        $resolved = $idx < 0 ? $cnt + $idx : $idx;
        if ($resolved < 0 || $resolved >= $cnt) {
            return [false, "Index [{$idx}] hors limites (taille : {$cnt})"];
        }
        $chosen = (string)$sourceValues[$resolved];
        if (count($steps) === 1) {
            return [true, [$chosen]];
        }
        return extractJsonPathListSteps($chosen, $steps, 1);
    }
    $aggregated = [];
    $lastErr = null;
    foreach ($sourceValues as $item) {
        [$ok, $res] = extractJsonPathListSteps((string)$item, $steps, 0);
        if ($ok && is_array($res)) {
            foreach ($res as $v) $aggregated[] = $v;
        } else {
            $lastErr = is_string($res) ? $res : null;
        }
    }
    return count($aggregated) > 0
        ? [true, $aggregated]
        : [false, $lastErr ?? "Aucune valeur extraite pour l'expression JSONPath."];
}

function extractRegex(string $text, string $pattern, int $groupIdx): array {
    $delim = "\x01";
    $escaped = str_replace($delim, '\\' . $delim, $pattern);
    $regexU = $delim . $escaped . $delim . 'u';
    $matched = @preg_match($regexU, $text, $matches);
    if ($matched === false) {
        $regexPlain = $delim . $escaped . $delim;
        $matched = @preg_match($regexPlain, $text, $matches);
    }
    if ($matched === false) {
        return [false, 'Expression régulière invalide'];
    }
    if ($matched === 0) {
        return [false, 'Aucune correspondance regex'];
    }
    if (!array_key_exists($groupIdx, $matches)) {
        return [false, "Groupe regex #{$groupIdx} inexistant"];
    }
    return [true, (string)$matches[$groupIdx]];
}

function extractRegexList(string $text, string $pattern, int $groupIdx): array {
    $delim = "\x01";
    $escaped = str_replace($delim, '\\' . $delim, $pattern);
    $regexU = $delim . $escaped . $delim . 'u';
    $matched = @preg_match_all($regexU, $text, $allMatches, PREG_SET_ORDER | PREG_OFFSET_CAPTURE);
    if ($matched === false) {
        $regexPlain = $delim . $escaped . $delim;
        $matched = @preg_match_all($regexPlain, $text, $allMatches, PREG_SET_ORDER | PREG_OFFSET_CAPTURE);
    }
    if ($matched === false) {
        return [false, 'Expression régulière invalide'];
    }
    if ($matched === 0) {
        return [false, 'Aucune correspondance regex'];
    }
    $values = [];
    foreach ($allMatches as $m) {
        if (!array_key_exists($groupIdx, $m)) {
            return [false, "Groupe regex #{$groupIdx} inexistant"];
        }
        if ($m[$groupIdx][1] >= 0) {
            $values[] = (string)$m[$groupIdx][0];
        }
    }
    if (count($values) === 0) {
        return [false, 'Aucune correspondance regex'];
    }
    return [true, $values];
}

function buildConditionRegex(string $pattern, bool $ignoreCase): string {
    $delim = "\x01";
    $escaped = str_replace($delim, '\\' . $delim, $pattern);
    $mods = 'u' . ($ignoreCase ? 'i' : '');
    $candidate = $delim . $escaped . $delim . $mods;
    if (@preg_match($candidate, '') !== false) {
        return $candidate;
    }
    $fallback = $delim . $escaped . $delim . ($ignoreCase ? 'i' : '');
    if (@preg_match($fallback, '') !== false) {
        return $fallback;
    }
    throw new RuntimeException("Condition Regex invalide : {$pattern}");
}

function parseConditionList(string $raw): array {
    $parts = preg_split('/[,;|\r\n]+/', $raw);
    if (!is_array($parts)) return [];
    $out = [];
    foreach ($parts as $p) {
        $t = trim($p);
        if ($t !== '') $out[] = $t;
    }
    return $out;
}

function tryParseCondNumber(string $raw): ?float {
    $s = str_replace(',', '.', trim($raw));
    if ($s === '' || !is_numeric($s)) return null;
    $f = (float)$s;
    return is_finite($f) ? $f : null;
}

function evalConditionFilter(array $cond, string $value): bool {
    $op = $cond['op'];
    $target = (string)$cond['value'];
    $icase = !empty($cond['ignoreCase']);
    $vCmp = $icase ? mb_strtolower($value, 'UTF-8') : $value;
    $tCmp = $icase ? mb_strtolower($target, 'UTF-8') : $target;

    switch ($op) {
        case 'contains': return $tCmp === '' || str_contains($vCmp, $tCmp);
        case 'not_contains': return $tCmp !== '' && !str_contains($vCmp, $tCmp);
        case 'starts_with': return str_starts_with($vCmp, $tCmp);
        case 'not_starts_with': return !str_starts_with($vCmp, $tCmp);
        case 'ends_with': return str_ends_with($vCmp, $tCmp);
        case 'not_ends_with': return !str_ends_with($vCmp, $tCmp);
        case 'equals': return $vCmp === $tCmp;
        case 'not_equals': return $vCmp !== $tCmp;
        case 'in_list':
            foreach (parseConditionList($target) as $it) {
                if ($vCmp === ($icase ? mb_strtolower($it, 'UTF-8') : $it)) return true;
            }
            return false;
        case 'not_in_list':
            foreach (parseConditionList($target) as $it) {
                if ($vCmp === ($icase ? mb_strtolower($it, 'UTF-8') : $it)) return false;
            }
            return true;
        case 'regex_match': return @preg_match(buildConditionRegex($target, $icase), $value) === 1;
        case 'regex_not_match': return @preg_match(buildConditionRegex($target, $icase), $value) !== 1;
        case 'is_empty': return trim($value) === '';
        case 'not_empty': return trim($value) !== '';
        case 'length_equals': return mb_strlen($value, 'UTF-8') === (int)trim($target);
        case 'length_min': return mb_strlen($value, 'UTF-8') >= (int)trim($target);
        case 'length_max': return mb_strlen($value, 'UTF-8') <= (int)trim($target);
        case 'number_equals':
        case 'number_not_equals':
        case 'greater_than':
        case 'greater_or_equal':
        case 'less_than':
        case 'less_or_equal':
            $vNum = tryParseCondNumber($value);
            $tNum = tryParseCondNumber($target);
            if ($vNum === null || $tNum === null) return false;
            return match ($op) {
                'number_equals' => $vNum === $tNum,
                'number_not_equals' => $vNum !== $tNum,
                'greater_than' => $vNum > $tNum,
                'greater_or_equal' => $vNum >= $tNum,
                'less_than' => $vNum < $tNum,
                default => $vNum <= $tNum,
            };
        default:
            return true;
    }
}

function applyConditionExtractParts(string $pattern, int $grp, bool $icase, string $value): array {
    $regex = buildConditionRegex($pattern, $icase);
    $matched = @preg_match($regex, $value, $matches);
    if ($matched !== 1 || !array_key_exists($grp, $matches)) {
        return [false, ''];
    }
    return [true, (string)$matches[$grp]];
}

function applyConditionReplaceParts(string $pattern, string $rawTpl, bool $icase, string $value): string {
    $regex = buildConditionRegex($pattern, $icase);
    $phpTpl = (string)preg_replace_callback('/\$\$|\$&/', static fn(array $m): string => $m[0] === '$$' ? '\\$' : '$0', $rawTpl);
    return (string)@preg_replace($regex, $phpTpl, $value);
}

function applyConditionReplaceTextParts(string $target, string $rep, bool $icase, string $value): string {
    if ($target === '') return $value;
    return $icase ? str_ireplace($target, $rep, $value) : str_replace($target, $rep, $value);
}

function applyConditionBetweenParts(string $left, string $right, bool $icase, string $value): array {
    $startIdx = 0;
    if ($left !== '') {
        $pos = $icase ? mb_stripos($value, $left, 0, 'UTF-8') : mb_strpos($value, $left, 0, 'UTF-8');
        if ($pos === false) return [false, ''];
        $startIdx = $pos + mb_strlen($left, 'UTF-8');
    }
    if ($right !== '') {
        $endPos = $icase ? mb_stripos($value, $right, $startIdx, 'UTF-8') : mb_strpos($value, $right, $startIdx, 'UTF-8');
        if ($endPos === false) return [false, ''];
        return [true, mb_substr($value, $startIdx, $endPos - $startIdx, 'UTF-8')];
    }
    return [true, mb_substr($value, $startIdx, null, 'UTF-8')];
}

function applyConditionSplitPartParts(string $sep, int $segIdx, bool $icase, string $value): array {
    if ($icase) {
        $parts = @preg_split(buildConditionRegex(preg_quote($sep, '/'), true), $value);
        if (!is_array($parts)) $parts = [$value];
    } else {
        $parts = $sep === '' ? [$value] : explode($sep, $value);
    }
    $resolved = $segIdx < 0 ? count($parts) + $segIdx : $segIdx;
    if ($resolved < 0 || $resolved >= count($parts)) {
        return [false, ''];
    }
    return [true, (string)$parts[$resolved]];
}

function executeActionBranch(string $action, string $val, string $rep, int $grp, int $segIdx, bool $icase, string $curr, array &$out): void {
    switch ($action) {
        case 'keep':
            $out[] = $curr;
            break;
        case 'exclude':
            break;
        case 'set_value':
            $out[] = $val;
            break;
        case 'prepend_text':
            $out[] = $val . $curr;
            break;
        case 'append_text':
            $out[] = $curr . $val;
            break;
        case 'regex_extract':
            [$okEx, $valEx] = applyConditionExtractParts($val, $grp, $icase, $curr);
            if ($okEx) $out[] = $valEx;
            break;
        case 'regex_extract_all':
            $regex = buildConditionRegex($val, $icase);
            $cnt = @preg_match_all($regex, $curr, $allMatches, PREG_SET_ORDER);
            if ($cnt !== false && $cnt > 0) {
                foreach ($allMatches as $m) {
                    if (array_key_exists($grp, $m)) {
                        $out[] = (string)$m[$grp];
                    }
                }
            }
            break;
        case 'regex_replace':
            $out[] = applyConditionReplaceParts($val, $rep, $icase, $curr);
            break;
        case 'replace_text':
            $out[] = applyConditionReplaceTextParts($val, $rep, $icase, $curr);
            break;
        case 'between':
            [$okBt, $valBt] = applyConditionBetweenParts($val, $rep, $icase, $curr);
            if ($okBt) $out[] = $valBt;
            break;
        case 'split_part':
            [$okSp, $valSp] = applyConditionSplitPartParts($val, $segIdx, $icase, $curr);
            if ($okSp) $out[] = $valSp;
            break;
        case 'trim':
            $out[] = $val === '' ? trim($curr) : trim($curr, $val);
            break;
        case 'to_lower':
            $out[] = mb_strtolower($curr, 'UTF-8');
            break;
        case 'to_upper':
            $out[] = mb_strtoupper($curr, 'UTF-8');
            break;
        default:
            $out[] = $curr;
            break;
    }
}

const TRANSFORM_OPS = ['regex_extract', 'regex_extract_all', 'regex_replace', 'replace_text', 'prepend_text', 'append_text', 'set_value', 'between', 'split_part', 'trim', 'to_lower', 'to_upper'];
const EXTRACT_OPS = ['regex_extract', 'regex_extract_all'];
const NO_VAL_PREDICATE_OPS = ['is_empty', 'not_empty'];
const NO_VAL_ACTION_OPS = ['keep', 'exclude', 'set_value', 'trim', 'to_lower', 'to_upper'];

function applySingleRuleBatch(array $cond, array $inputs): array {
    $out = [];
    if (!empty($cond['isConditional'])) {
        $thenB = $cond['thenBranch'] ?? ['action' => 'keep'];
        $elseB = $cond['elseBranch'] ?? ['action' => 'exclude'];
        foreach ($inputs as $curr) {
            $br = evalConditionFilter($cond, $curr) ? $thenB : $elseB;
            executeActionBranch(
                (string)($br['action'] ?? 'keep'),
                (string)($br['value'] ?? ''),
                (string)($br['replacement'] ?? ''),
                max(0, (int)($br['regexGroup'] ?? 1)),
                (int)($br['segmentIndex'] ?? 0),
                !empty($br['ignoreCase']),
                $curr,
                $out
            );
        }
        return $out;
    }

    $op = $cond['op'];
    foreach ($inputs as $curr) {
        if (in_array($op, TRANSFORM_OPS, true)) {
            executeActionBranch(
                $op,
                (string)($cond['value'] ?? ''),
                (string)($cond['replacement'] ?? ''),
                max(0, (int)($cond['regexGroup'] ?? 1)),
                (int)($cond['segmentIndex'] ?? 0),
                !empty($cond['ignoreCase']),
                $curr,
                $out
            );
        } elseif (evalConditionFilter($cond, $curr)) {
            $out[] = $curr;
        }
    }
    return $out;
}

function validateBranchSpec(string $action, string $val, string $rep, bool $icase): ?string {
    if ($action === 'between') {
        if ($val === '' && $rep === '') return 'Condition entre délimiteurs vide.';
    } elseif (!in_array($action, NO_VAL_ACTION_OPS, true) && $val === '') {
        return 'Action ou transformation vide.';
    }
    if (in_array($action, ['regex_extract', 'regex_extract_all', 'regex_replace'], true)) {
        buildConditionRegex($val, $icase);
    }
    return null;
}

function validatePredicateSpec(string $op, string $val, bool $icase): ?string {
    if (!in_array($op, NO_VAL_PREDICATE_OPS, true) && $val === '') {
        return "Condition d'extraction vide.";
    }
    if (in_array($op, ['in_list', 'not_in_list'], true) && count(parseConditionList($val)) === 0) {
        return 'Liste de valeurs vide.';
    }
    if (in_array($op, ['regex_match', 'regex_not_match'], true)) {
        buildConditionRegex($val, $icase);
    } elseif (in_array($op, ['length_equals', 'length_min', 'length_max'], true)) {
        $trimmed = trim($val);
        if (!preg_match('/^\d+$/', $trimmed)) {
            return "Longueur invalide : {$val}";
        }
    } elseif (in_array($op, ['number_equals', 'number_not_equals', 'greater_than', 'greater_or_equal', 'less_than', 'less_or_equal'], true)) {
        if (tryParseCondNumber($val) === null) {
            return "Nombre invalide : {$val}";
        }
    }
    return null;
}

function applyExtractionConditions(array $rawValues, array $ext): array {
    $conditions = $ext['conditions'] ?? [];
    $dedup = !empty($ext['deduplicate']);
    if (count($conditions) === 0 && !$dedup) {
        return [true, $rawValues];
    }

    try {
        foreach ($conditions as $cond) {
            if (!empty($cond['isConditional'])) {
                $pErr = validatePredicateSpec((string)$cond['op'], (string)($cond['value'] ?? ''), !empty($cond['ignoreCase']));
                if ($pErr !== null) return [false, $pErr];
                foreach (['thenBranch', 'elseBranch'] as $bk) {
                    $br = $cond[$bk] ?? ['action' => 'keep'];
                    $bErr = validateBranchSpec((string)($br['action'] ?? 'keep'), (string)($br['value'] ?? ''), (string)($br['replacement'] ?? ''), !empty($br['ignoreCase']));
                    if ($bErr !== null) return [false, $bErr];
                }
            } elseif (in_array($cond['op'], TRANSFORM_OPS, true)) {
                $bErr = validateBranchSpec((string)$cond['op'], (string)($cond['value'] ?? ''), (string)($cond['replacement'] ?? ''), !empty($cond['ignoreCase']));
                if ($bErr !== null) return [false, $bErr];
            } else {
                $pErr = validatePredicateSpec((string)$cond['op'], (string)($cond['value'] ?? ''), !empty($cond['ignoreCase']));
                if ($pErr !== null) return [false, $pErr];
            }
        }
    } catch (Throwable $e) {
        return [false, $e->getMessage()];
    }

    $mode = $ext['conditionMode'] ?? 'all';
    $out = [];
    $seen = [];

    foreach ($rawValues as $raw) {
        $currItems = [(string)$raw];
        if ($mode === 'all') {
            foreach ($conditions as $cond) {
                $currItems = applySingleRuleBatch($cond, $currItems);
                if (count($currItems) === 0) {
                    break;
                }
            }
        } else {
            $filters = array_values(array_filter($conditions, static fn(array $c): bool => empty($c['isConditional']) && !in_array($c['op'], TRANSFORM_OPS, true)));
            $extracts = array_values(array_filter($conditions, static fn(array $c): bool => empty($c['isConditional']) && in_array($c['op'], EXTRACT_OPS, true)));
            $transforms = array_values(array_filter($conditions, static fn(array $c): bool => !empty($c['isConditional']) || (in_array($c['op'], TRANSFORM_OPS, true) && !in_array($c['op'], EXTRACT_OPS, true))));

            if (count($filters) > 0) {
                $passed = false;
                foreach ($filters as $fc) {
                    if (evalConditionFilter($fc, (string)$raw)) {
                        $passed = true;
                        break;
                    }
                }
                if (!$passed) {
                    continue;
                }
            }

            if (count($extracts) > 0) {
                if (count($filters) === 0 && count($extracts) > 1) {
                    $matchedAny = false;
                    foreach ($extracts as $ec) {
                        $cand = applySingleRuleBatch($ec, $currItems);
                        if (count($cand) > 0) {
                            $currItems = $cand;
                            $matchedAny = true;
                            break;
                        }
                    }
                    if (!$matchedAny) {
                        continue;
                    }
                } else {
                    foreach ($extracts as $ec) {
                        $currItems = applySingleRuleBatch($ec, $currItems);
                        if (count($currItems) === 0) {
                            break;
                        }
                    }
                    if (count($currItems) === 0) {
                        continue;
                    }
                }
            }

            foreach ($transforms as $tc) {
                $currItems = applySingleRuleBatch($tc, $currItems);
                if (count($currItems) === 0) {
                    break;
                }
            }
        }

        foreach ($currItems as $valOut) {
            if ($dedup) {
                if (array_key_exists($valOut, $seen)) {
                    continue;
                }
                $seen[$valOut] = true;
            }
            $out[] = $valOut;
        }
    }

    if (count($out) === 0 && count($rawValues) > 0) {
        $rawCnt = count($rawValues);
        return [false, "Aucun élément ne satisfait les conditions ({$rawCnt} brut(s) filtré(s))."];
    }
    return [true, $out];
}

function resolveVariableSourceValues(string $sourceVarName, array $localLists, array $localExtracted, array $accumulatedLists, array $mergedVars, array $listVars): array {
    $key = trim($sourceVarName);
    if ($key === '') return [];
    if (array_key_exists($key, $localLists)) return array_values($localLists[$key]);
    if (array_key_exists($key, $localExtracted)) return [$localExtracted[$key]];
    if (array_key_exists($key, $listVars)) return [$listVars[$key]];
    if (array_key_exists($key, $accumulatedLists)) return array_values($accumulatedLists[$key]);
    if (array_key_exists($key, $mergedVars)) return [$mergedVars[$key]];
    return [];
}

function runExtraction(array $ext, string $respBody, array $respHeaders, ?array $sourceValues = null): array {
    $rawOutcome = [false, 'Aucune correspondance'];
    if ($ext['source'] === 'variable') {
        $srcName = (string)($ext['sourceVariable'] ?? '');
        if ($srcName === '') {
            return [false, 'Nom de variable source vide.'];
        }
        if ($sourceValues === null || count($sourceValues) === 0) {
            return [false, "Variable source « {$srcName} » vide ou introuvable."];
        }
        if ($ext['kind'] === 'full_response') {
            $rawOutcome = [true, count($sourceValues) === 1 ? (string)$sourceValues[0] : (string)json_encode(array_values($sourceValues), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES)];
        } elseif ($ext['kind'] === 'jsonpath') {
            $rawOutcome = extractJsonPathFromValues($sourceValues, $ext['expression']);
        } else {
            foreach ($sourceValues as $item) {
                [$ok, $res] = extractRegex((string)$item, $ext['expression'], $ext['regexGroup']);
                $rawOutcome = [$ok, $res];
                if ($ok) break;
            }
        }
    } elseif ($ext['source'] === 'header') {
        $target = strtolower($ext['headerName']);
        $matching = [];
        foreach ($respHeaders as [$k, $v]) {
            if (strtolower($k) === $target) {
                $matching[] = $v;
            }
        }
        if (count($matching) === 0) {
            return [false, "En-tête « {$ext['headerName']} » absent"];
        }
        foreach ($matching as $val) {
            [$ok, $res] = extractRegex($val, $ext['expression'], $ext['regexGroup']);
            $rawOutcome = [$ok, $res];
            if ($ok) break;
        }
    } elseif ($ext['kind'] === 'full_response') {
        $rawOutcome = [true, $respBody];
    } elseif ($ext['kind'] === 'jsonpath') {
        $rawOutcome = extractJsonPath($respBody, $ext['expression']);
    } else {
        $rawOutcome = extractRegex($respBody, $ext['expression'], $ext['regexGroup']);
    }

    if (!$rawOutcome[0] || empty($ext['conditions'])) {
        return $rawOutcome;
    }
    [$cOk, $cRes] = applyExtractionConditions([(string)$rawOutcome[1]], $ext);
    if ($cOk && is_array($cRes) && count($cRes) > 0) {
        $hasAll = false;
        foreach ($ext['conditions'] as $c) {
            if ((empty($c['isConditional']) && ($c['op'] ?? '') === 'regex_extract_all') ||
                (!empty($c['isConditional']) && ((($c['thenBranch']['action'] ?? '') === 'regex_extract_all') || (($c['elseBranch']['action'] ?? '') === 'regex_extract_all')))) {
                $hasAll = true;
                break;
            }
        }
        return [true, ($hasAll && count($cRes) > 1) ? implode(', ', $cRes) : (string)$cRes[0]];
    }
    return [false, is_string($cRes) ? $cRes : 'La valeur extraite ne satisfait pas les conditions.'];
}

function runListExtraction(array $ext, string $respBody, array $respHeaders, ?array $sourceValues = null): array {
    $rawValues = [];
    if ($ext['source'] === 'variable') {
        $srcName = (string)($ext['sourceVariable'] ?? '');
        if ($srcName === '') {
            return [false, 'Nom de variable source vide.'];
        }
        if ($sourceValues === null || count($sourceValues) === 0) {
            return [false, "Variable source « {$srcName} » vide ou introuvable."];
        }
        if ($ext['kind'] === 'jsonpath') {
            [$ok, $res] = extractJsonPathListFromValues($sourceValues, $ext['expression']);
            if (!$ok) return [false, $res];
            $rawValues = $res;
        } else {
            $lastErr = 'Aucune correspondance';
            foreach ($sourceValues as $item) {
                [$ok, $res] = extractRegexList((string)$item, $ext['expression'], $ext['regexGroup']);
                if ($ok) {
                    foreach ($res as $v) $rawValues[] = $v;
                } else {
                    $lastErr = $res;
                }
            }
            if (count($rawValues) === 0) {
                return [false, $lastErr];
            }
        }
    } elseif ($ext['source'] === 'header') {
        $target = strtolower($ext['headerName']);
        $matching = [];
        foreach ($respHeaders as [$k, $v]) {
            if (strtolower($k) === $target) {
                $matching[] = $v;
            }
        }
        if (count($matching) === 0) {
            return [false, "En-tête « {$ext['headerName']} » absent"];
        }
        $lastErr = 'Aucune correspondance';
        foreach ($matching as $val) {
            [$ok, $res] = extractRegexList($val, $ext['expression'], $ext['regexGroup']);
            if ($ok) {
                foreach ($res as $item) {
                    $rawValues[] = $item;
                }
            } else {
                $lastErr = $res;
            }
        }
        if (count($rawValues) === 0) {
            return [false, $lastErr];
        }
    } elseif ($ext['kind'] === 'jsonpath') {
        [$ok, $res] = extractJsonPathList($respBody, $ext['expression']);
        if (!$ok) return [false, $res];
        $rawValues = $res;
    } else {
        [$ok, $res] = extractRegexList($respBody, $ext['expression'], $ext['regexGroup']);
        if (!$ok) return [false, $res];
        $rawValues = $res;
    }

    return applyExtractionConditions($rawValues, $ext);
}

function sendHttp(string $method, string $url, array $headers, ?string $body, int $timeoutSeconds): array {
    $ch = curl_init($url);
    $respHeaders = [];
    $headerLines = [];
    foreach ($headers as [$k, $v]) {
        $headerLines[] = "{$k}: {$v}";
    }

    curl_setopt_array($ch, [
        CURLOPT_CUSTOMREQUEST => $method,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_FOLLOWLOCATION => true,
        CURLOPT_ENCODING => '',
        CURLOPT_TIMEOUT => $timeoutSeconds,
        CURLOPT_HTTPHEADER => $headerLines,
        CURLOPT_HEADERFUNCTION => static function ($curl, string $headerLine) use (&$respHeaders): int {
            $len = strlen($headerLine);
            $colon = strpos($headerLine, ':');
            if ($colon !== false) {
                $name = trim(substr($headerLine, 0, $colon));
                $value = trim(substr($headerLine, $colon + 1));
                if ($name !== '') {
                    $respHeaders[] = [$name, $value];
                }
            }
            return $len;
        },
    ]);

    if ($body !== null) {
        curl_setopt($ch, CURLOPT_POSTFIELDS, $body);
    }

    $respBody = curl_exec($ch);
    $errNo = curl_errno($ch);
    $errMsg = $errNo !== 0 ? curl_error($ch) : null;
    $status = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
    curl_close($ch);

    if ($errMsg !== null) {
        return [0, $respHeaders, '', $errMsg];
    }

    return [$status, $respHeaders, is_string($respBody) ? $respBody : '', null];
}

function resolveCurrentIp(): string {
    static $cachedIp = null;
    if ($cachedIp !== null) {
        return $cachedIp;
    }

    foreach (['https://api.ipify.org', 'https://icanhazip.com'] as $endpoint) {
        $ch = curl_init($endpoint);
        if ($ch === false) {
            continue;
        }
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => true,
            CURLOPT_CONNECTTIMEOUT => 2,
            CURLOPT_TIMEOUT => 3,
            CURLOPT_USERAGENT => 'ApiTester-CLI/1.0',
        ]);
        $body = curl_exec($ch);
        $code = (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
        curl_close($ch);
        if (is_string($body) && $code >= 200 && $code < 300) {
            $candidate = trim($body);
            if (filter_var($candidate, FILTER_VALIDATE_IP) !== false) {
                $cachedIp = $candidate;
                return $cachedIp;
            }
        }
    }

    $sock = @stream_socket_client('udp://8.8.8.8:80', $errNo, $errStr, 1.0);
    if ($sock !== false) {
        $peer = @stream_socket_get_name($sock, false);
        @fclose($sock);
        if (is_string($peer) && $peer !== '') {
            $pos = strrpos($peer, ':');
            $localIp = $pos !== false ? substr($peer, 0, $pos) : $peer;
            $localIp = trim($localIp, '[]');
            if (filter_var($localIp, FILTER_VALIDATE_IP) !== false) {
                $cachedIp = $localIp;
                return $cachedIp;
            }
        }
    }

    $host = @gethostname();
    if (is_string($host) && $host !== '') {
        $hostIp = @gethostbyname($host);
        if (is_string($hostIp) && filter_var($hostIp, FILTER_VALIDATE_IP) !== false) {
            $cachedIp = $hostIp;
            return $cachedIp;
        }
    }

    $cachedIp = '127.0.0.1';
    return $cachedIp;
}

function resolveReturnIdentifier(string $name, bool $forceScalar, bool $forceList, array $scalars, array $listVars, array $accumulatedLists, int $globalIndex, string $reqGuid, string $reqTs): mixed {
    if ($name === '$index') return (string)$globalIndex;
    if ($name === '$guid') return $reqGuid;
    if ($name === '$timestamp') return $reqTs;
    if ($name === '$ip') return resolveCurrentIp();

    [$root, $jp] = splitVarPath($name);
    if ($jp !== null) {
        if ($forceList) {
            if (array_key_exists($name, $listVars)) {
                return $listVars[$name];
            }
            if (array_key_exists($root, $scalars)) {
                [$ok, $res] = extractJsonPathList((string)$scalars[$root], $jp);
                if ($ok && is_array($res)) {
                    return $res;
                }
                throw new RuntimeException("[[{$name}]] : " . (string)$res);
            }
            if (array_key_exists($root, $accumulatedLists)) {
                [$ok, $res] = extractJsonPathListFromValues($accumulatedLists[$root], $jp);
                if ($ok && is_array($res)) {
                    return $res;
                }
                throw new RuntimeException("[[{$name}]] : " . (string)$res);
            }
            throw new RuntimeException("Variable ou liste inconnue : {$root} (pour [[{$name}]])");
        }
        if (array_key_exists($root, $scalars)) {
            if (!$forceScalar) {
                $steps = parseJsonPath($jp);
                foreach ($steps as [, , $isWildcard]) {
                    if ($isWildcard) {
                        [$ok, $res] = extractJsonPathList((string)$scalars[$root], $jp);
                        if ($ok && is_array($res)) {
                            return $res;
                        }
                        throw new RuntimeException("{$name} : " . (string)$res);
                    }
                }
            }
            [$ok, $res] = extractJsonPath((string)$scalars[$root], $jp);
            if ($ok) {
                return $res;
            }
            throw new RuntimeException("{$name} : " . (string)$res);
        }
        if (array_key_exists($root, $accumulatedLists)) {
            if (!$forceScalar) {
                $steps = parseJsonPath($jp);
                foreach ($steps as [, , $isWildcard]) {
                    if ($isWildcard) {
                        [$ok, $res] = extractJsonPathListFromValues($accumulatedLists[$root], $jp);
                        if ($ok && is_array($res)) {
                            return $res;
                        }
                        throw new RuntimeException("{$name} : " . (string)$res);
                    }
                }
            }
            [$ok, $res] = extractJsonPathFromValues($accumulatedLists[$root], $jp);
            if ($ok) {
                return $res;
            }
            throw new RuntimeException("{$name} : " . (string)$res);
        }
        throw new RuntimeException("Variable ou liste inconnue : {$root} (pour {$name})");
    }

    if ($forceList) {
        if (array_key_exists($name, $listVars)) {
            return $listVars[$name];
        }
        if (array_key_exists($name, $accumulatedLists)) {
            return array_values($accumulatedLists[$name]);
        }
        throw new RuntimeException("Liste inconnue ou non définie : [[" . $name . "]]");
    }

    if ($forceScalar) {
        if (array_key_exists($name, $scalars)) {
            return $scalars[$name];
        }
        if ($name === 'ip') {
            return resolveCurrentIp();
        }
        throw new RuntimeException("Variable inconnue ou non définie : {{" . $name . "}}");
    }

    if (array_key_exists($name, $listVars)) {
        return $listVars[$name];
    }
    if (array_key_exists($name, $scalars)) {
        return $scalars[$name];
    }
    if (array_key_exists($name, $accumulatedLists)) {
        return array_values($accumulatedLists[$name]);
    }
    if ($name === 'ip') {
        return resolveCurrentIp();
    }
    throw new RuntimeException("Variable ou liste inconnue : " . $name);
}

function inlineReturnStr(mixed $val): string {
    if ($val === null) return '';
    if (is_bool($val)) return $val ? 'true' : 'false';
    if (is_string($val)) return $val;
    if (is_int($val) || is_float($val)) return (string)$val;
    return (string)json_encode($val, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
}

function evalReturnNode(array $node, array $scalars, array $listVars, array $accumulatedLists, int $globalIndex, string $reqGuid, string $reqTs): mixed {
    $kind = $node['type'];
    if ($kind === 'null') return null;
    if ($kind === 'bool') return (bool)$node['value'];
    if ($kind === 'num') {
        $raw = (string)$node['raw'];
        return str_contains($raw, '.') ? (float)$raw : (int)$raw;
    }
    if ($kind === 'str') {
        $text = (string)$node['value'];
        if (empty($node['interpolate'])) {
            return $text;
        }
        return (string)preg_replace_callback(
            MARKER_RE,
            static function (array $match) use ($scalars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs): string {
                $listName = (isset($match[2]) && $match[2] !== '') ? $match[2] : null;
                if ($listName !== null) {
                    $v = resolveReturnIdentifier($listName, false, true, $scalars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
                } else {
                    $v = resolveReturnIdentifier((string)($match[1] ?? ''), true, false, $scalars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
                }
                return inlineReturnStr($v);
            },
            $text
        );
    }
    if ($kind === 'id') {
        return resolveReturnIdentifier(
            (string)$node['name'],
            !empty($node['forceScalar']),
            !empty($node['forceList']),
            $scalars,
            $listVars,
            $accumulatedLists,
            $globalIndex,
            $reqGuid,
            $reqTs
        );
    }
    if ($kind === 'arr') {
        $out = [];
        foreach ($node['items'] as $item) {
            $out[] = evalReturnNode($item, $scalars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
        }
        return $out;
    }
    if ($kind === 'obj') {
        $assoc = [];
        foreach ($node['entries'] as [$k, $vNode]) {
            $assoc[(string)$k] = evalReturnNode($vNode, $scalars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
        }
        return (object)$assoc;
    }
    if ($kind === 'concat') {
        $sb = '';
        foreach ($node['parts'] as $part) {
            $sb .= inlineReturnStr(evalReturnNode($part, $scalars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs));
        }
        return $sb;
    }
    throw new RuntimeException("Nœud de retour inconnu : {$kind}");
}

function formatReturnOutput(mixed $val): string {
    if ($val === null) return 'null';
    if (is_string($val)) return $val;
    if (is_bool($val)) return $val ? 'true' : 'false';
    if (is_int($val) || is_float($val)) return (string)json_encode($val, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    return (string)json_encode($val, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
}

function printReturnOutput(string $formatted): void {
    if (str_contains($formatted, "\n")) {
        echo "  -> Retour :\n" . $formatted . "\n";
    } else {
        echo "  -> Retour : " . $formatted . "\n";
    }
}

$verbose = in_array('--verbose', $argv, true) || in_array('-v', $argv, true);
$accumulatedVars = [];
$accumulatedLists = [];
$globalIndex = 0;
$hasAnyFailure = false;
$stepCount = count($steps);

foreach ($steps as $stepIdx => $step) {
    if ($step['returnError'] !== null) {
        fwrite(STDERR, "[ERREUR] Étape {$step['rank']} ({$step['name']}) : {$step['returnError']}\n");
        exit(1);
    }

    if ($step['url'] === '' && empty($step['isReturnOnly'])) {
        fwrite(STDERR, "[ERREUR] Étape {$step['rank']} ({$step['name']}) : URL vide.\n");
        exit(1);
    }

    $scalarCandidates = array_merge($accumulatedVars, $step['fixedVariables']);
    [$iterations, $emptyListName, $listErr] = buildIterations($step['axes'], $step['mode'], $accumulatedLists, $scalarCandidates);
    if ($listErr !== null) {
        fwrite(STDERR, "[ÉCHEC] Étape {$step['rank']} ({$step['name']}) : {$listErr}\n");
        $hasAnyFailure = true;
        if (!$step['continueOnError']) {
            exit(1);
        }
        continue;
    }
    if ($emptyListName !== null) {
        echo "[IGNORÉE] Étape {$step['rank']} ({$step['name']}) : liste [[{$emptyListName}]] vide.\n";
        continue;
    }

    $iterCount = count($iterations);

    foreach ($iterations as $iterOffset => $axisValues) {
        $iterIdx = $iterOffset + 1;
        $globalIndex++;
        $reqGuid = generateUuidV4();
        $reqTs = (string)time();

        $mergedVars = array_merge($accumulatedVars, $step['fixedVariables']);
        $listVars = [];
        foreach ($step['axes'] as $aIdx => $axis) {
            if ($axis['kind'] === 'var') {
                $mergedVars[$axis['name']] = $axisValues[$aIdx];
            } elseif ($axis['kind'] === 'list') {
                $listVars[$axis['name']] = $axisValues[$aIdx];
            }
        }

        if (!empty($step['isReturnOnly'])) {
            try {
                $retVal = evalReturnNode(
                    $step['returnNode'],
                    $mergedVars,
                    $listVars,
                    $accumulatedLists,
                    $globalIndex,
                    $reqGuid,
                    $reqTs
                );
                $formattedRet = formatReturnOutput($retVal);
                echo "[OK] Étape {$step['rank']} ({$step['name']}) [{$iterIdx}/{$iterCount}] RETURN\n";
                printReturnOutput($formattedRet);
            } catch (Throwable $e) {
                fwrite(STDERR, "[ÉCHEC] Étape {$step['rank']} ({$step['name']}) [{$iterIdx}/{$iterCount}] RETURN : {$e->getMessage()}\n");
                $hasAnyFailure = true;
                if (!$step['continueOnError']) {
                    exit(1);
                }
                continue;
            }

            $hasMoreRet = ($iterIdx < $iterCount) || ($stepIdx + 1 < $stepCount);
            if ($hasMoreRet && $step['delayAfterMs'] > 0) {
                usleep($step['delayAfterMs'] * 1000);
            }
            continue;
        }

        try {
            $finalBaseUrl = substituteUrl($step['url'], $mergedVars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
            $queryItems = [];
            foreach ($step['queryParams'] as $qp) {
                if ($qp['axisIndex'] !== null) {
                    $queryItems[] = [$qp['key'], $axisValues[$qp['axisIndex']]];
                } else {
                    $queryItems[] = [$qp['key'], substituteSimple($qp['value'], $mergedVars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs)];
                }
            }
            $finalUrl = buildFinalUrl($finalBaseUrl, $queryItems);

            $finalHeaders = [];
            foreach ($step['headers'] as [$hk, $hv]) {
                $finalHeaders[] = [$hk, substituteSimple($hv, $mergedVars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs)];
            }

            $bodyToSend = null;
            if ($step['bodyType'] === 'json') {
                $bodyToSend = substituteJsonBody($step['body'], $mergedVars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
            } elseif ($step['bodyType'] === 'text') {
                $bodyToSend = substituteSimple($step['body'], $mergedVars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs);
            } elseif ($step['bodyType'] === 'form') {
                $formPairs = [];
                foreach ($step['formFields'] as [$fk, $fv]) {
                    $formPairs[] = rawurlencode((string)$fk) . '=' . rawurlencode(substituteSimple($fv, $mergedVars, $listVars, $accumulatedLists, $globalIndex, $reqGuid, $reqTs));
                }
                $bodyToSend = implode('&', $formPairs);
            }
        } catch (Throwable $e) {
            fwrite(STDERR, "[ÉCHEC] Étape {$step['rank']} ({$step['name']}) [{$iterIdx}/{$iterCount}] : {$e->getMessage()}\n");
            $hasAnyFailure = true;
            if (!$step['continueOnError']) {
                exit(1);
            }
            continue;
        }

        $t0 = hrtime(true);
        [$status, $respHeaders, $respBody, $netErr] = sendHttp($step['method'], $finalUrl, $finalHeaders, $bodyToSend, $step['timeoutSeconds']);
        $elapsedMs = (int)round((hrtime(true) - $t0) / 1_000_000);

        $httpFailed = $netErr !== null || $status >= 400;
        $extractionFailed = false;
        $localExtracted = [];
        $localLists = [];

        foreach ($step['extractions'] as $ext) {
            $srcValues = $ext['source'] === 'variable'
                ? resolveVariableSourceValues((string)($ext['sourceVariable'] ?? ''), $localLists, $localExtracted, $accumulatedLists, $mergedVars, $listVars)
                : null;
            if (!empty($ext['isList'])) {
                [$ok, $valuesOrErr] = runListExtraction($ext, $respBody, $respHeaders, $srcValues);
                if ($ok) {
                    $accumulatedLists[$ext['variable']] = $valuesOrErr;
                    $localLists[$ext['variable']] = $valuesOrErr;
                    if ($verbose) {
                        $cnt = count($valuesOrErr);
                        echo "  -> [[{$ext['variable']}]] = {$cnt} élément(s)\n";
                    }
                } else {
                    $fallbackList = $ext['defaultValue'] !== '' ? [$ext['defaultValue']] : [];
                    $accumulatedLists[$ext['variable']] = $fallbackList;
                    $localLists[$ext['variable']] = $fallbackList;
                    if ($ext['required']) {
                        $extractionFailed = true;
                        fwrite(STDERR, "  [EXTRACTION] [[{$ext['variable']}]] : {$valuesOrErr}\n");
                    }
                }
                continue;
            }

            [$ok, $valueOrErr] = runExtraction($ext, $respBody, $respHeaders, $srcValues);
            if ($ok) {
                $accumulatedVars[$ext['variable']] = $valueOrErr;
                $localExtracted[$ext['variable']] = $valueOrErr;
                if ($verbose) {
                    echo "  -> {$ext['variable']} = {$valueOrErr}\n";
                }
            } else {
                $accumulatedVars[$ext['variable']] = $ext['defaultValue'];
                $localExtracted[$ext['variable']] = $ext['defaultValue'];
                if ($ext['required']) {
                    $extractionFailed = true;
                    fwrite(STDERR, "  [EXTRACTION] {$ext['variable']} : {$valueOrErr}\n");
                }
            }
        }

        $stepFailed = $httpFailed || $extractionFailed;
        $formattedRet = null;
        $returnErr = null;
        if (!$stepFailed && $step['returnNode'] !== null) {
            $returnVars = array_merge($mergedVars, $localExtracted);
            try {
                $retVal = evalReturnNode(
                    $step['returnNode'],
                    $returnVars,
                    $listVars,
                    $accumulatedLists,
                    $globalIndex,
                    $reqGuid,
                    $reqTs
                );
                $formattedRet = formatReturnOutput($retVal);
            } catch (Throwable $e) {
                $stepFailed = true;
                $returnErr = $e->getMessage();
            }
        }

        $statusLabel = $stepFailed ? 'ÉCHEC' : 'OK';
        $codeLabel = $netErr !== null ? $netErr : "HTTP {$status}";
        echo "[{$statusLabel}] Étape {$step['rank']} ({$step['name']}) [{$iterIdx}/{$iterCount}] {$step['method']} {$finalUrl} -> {$codeLabel} ({$elapsedMs} ms)\n";

        if ($returnErr !== null) {
            fwrite(STDERR, "  [RETOUR] {$returnErr}\n");
        } elseif ($formattedRet !== null) {
            printReturnOutput($formattedRet);
        }

        if ($verbose && $respBody !== '') {
            echo substr($respBody, 0, 1000) . "\n";
        }

        if ($stepFailed) {
            $hasAnyFailure = true;
            if (!$step['continueOnError']) {
                exit(1);
            }
        }

        $hasMore = ($iterIdx < $iterCount) || ($stepIdx + 1 < $stepCount);
        if ($hasMore && $step['delayAfterMs'] > 0) {
            usleep($step['delayAfterMs'] * 1000);
        }
    }
}

exit($hasAnyFailure ? 1 : 0);
""");
    }

    private static string FormatReturnNode(StepReturnNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        switch (node)
        {
            case NullReturnNode:
                return "['type' => 'null']";
            case BooleanReturnNode b:
                return $"['type' => 'bool', 'value' => {FormatBool(b.Value)}]";
            case NumberReturnNode n:
                return $"['type' => 'num', 'raw' => {EscapeString(n.RawText)}]";
            case StringLiteralReturnNode s:
                return $"['type' => 'str', 'value' => {EscapeString(s.Value)}, 'interpolate' => {FormatBool(s.HasTemplateMarkers)}]";
            case IdentifierReturnNode id:
                return $"['type' => 'id', 'name' => {EscapeString(id.Name)}, 'forceScalar' => {FormatBool(id.ForceScalar)}, 'forceList' => {FormatBool(id.ForceList)}]";
            case ArrayReturnNode arr:
            {
                var sb = new StringBuilder("['type' => 'arr', 'items' => [");
                for (var i = 0; i < arr.Items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(FormatReturnNode(arr.Items[i]));
                }
                sb.Append("]]");
                return sb.ToString();
            }
            case ObjectReturnNode obj:
            {
                var sb = new StringBuilder("['type' => 'obj', 'entries' => [");
                for (var i = 0; i < obj.Entries.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('[');
                    sb.Append(EscapeString(obj.Entries[i].Key));
                    sb.Append(", ");
                    sb.Append(FormatReturnNode(obj.Entries[i].Value));
                    sb.Append(']');
                }
                sb.Append("]]");
                return sb.ToString();
            }
            case ConcatReturnNode concat:
            {
                var sb = new StringBuilder("['type' => 'concat', 'parts' => [");
                for (var i = 0; i < concat.Parts.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(FormatReturnNode(concat.Parts[i]));
                }
                sb.Append("]]");
                return sb.ToString();
            }
            default:
                return "null";
        }
    }

    private static string FormatBool(bool value) => value ? "true" : "false";

    private static string EscapeString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '$':
                    sb.Append("\\$");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\x");
                        sb.Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
