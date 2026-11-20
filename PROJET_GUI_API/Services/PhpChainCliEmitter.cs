using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        sb.AppendLine("const MARKER_RE = '/(?:\\{\\{\\s*(\\$?(?![0-9])[\\p{L}\\p{N}_]+)\\s*\\}\\}|\\[\\[\\s*((?![0-9])[\\p{L}\\p{N}_]+)\\s*\\]\\])/u';");
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
                sb.AppendLine($"                'required' => {FormatBool(ext.IsRequired)},");
                sb.AppendLine($"                'defaultValue' => {EscapeString(ext.DefaultValueIfMissing)},");
                sb.AppendLine($"                'isList' => {FormatBool(ext.IsList)},");
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

function buildIterations(array $axes, string $mode, array $accumulatedLists): array {
    if (count($axes) === 0) {
        return [[[]], null];
    }
    $lists = [];
    foreach ($axes as $axis) {
        if ($axis['kind'] === 'list') {
            $list = $accumulatedLists[$axis['name']] ?? [];
            if (count($list) === 0) {
                return [[], $axis['name']];
            }
            $lists[] = $list;
        } else {
            $list = isset($axis['range']) ? makeRange($axis['range']) : $axis['values'];
            if (count($list) === 0) {
                return [[], null];
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
        return [$rows, null];
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
    return [$acc, null];
}

function resolveMarker(?string $scalarName, ?string $listName, array $variables, array $listVars, int $globalIndex, string $reqGuid, string $reqTs): string {
    if ($listName !== null && $listName !== '') {
        if (array_key_exists($listName, $listVars)) {
            return (string)$listVars[$listName];
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
    throw new RuntimeException("Variable inconnue ou non définie : {{" . $name . "}}");
}

function substituteSimple(string $text, array $variables, array $listVars, int $globalIndex, string $reqGuid, string $reqTs, bool $urlEncode = false): string {
    if ($text === '') {
        return '';
    }
    return (string)preg_replace_callback(
        MARKER_RE,
        static function (array $match) use ($variables, $listVars, $globalIndex, $reqGuid, $reqTs, $urlEncode): string {
            $val = resolveMarker($match[1] ?? null, $match[2] ?? null, $variables, $listVars, $globalIndex, $reqGuid, $reqTs);
            return $urlEncode ? rawurlencode($val) : $val;
        },
        $text
    );
}

function substituteJsonBody(string $text, array $variables, array $listVars, int $globalIndex, string $reqGuid, string $reqTs): string {
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
        $val = resolveMarker($scalarName, $listName, $variables, $listVars, $globalIndex, $reqGuid, $reqTs);
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
    if (str_starts_with($path, '$.')) {
        $path = substr($path, 2);
    } elseif (str_starts_with($path, '$')) {
        $path = substr($path, 1);
    }
    if (trim($path) === '') {
        throw new RuntimeException('Chemin JSONPath vide.');
    }

    $steps = [];
    foreach (explode('.', $path) as $part) {
        if ($part === '') {
            throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
        }
        $bracket = strpos($part, '[');
        if ($bracket === false) {
            $steps[] = [$part, null, false];
            continue;
        }
        $prop = $bracket > 0 ? substr($part, 0, $bracket) : null;
        $pos = $bracket;
        $first = true;
        $len = strlen($part);
        while ($pos < $len) {
            if ($part[$pos] !== '[') {
                throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
            }
            $close = strpos($part, ']', $pos + 1);
            if ($close === false) {
                throw new RuntimeException("Chemin JSONPath invalide : {$rawPath}");
            }
            $idxStr = trim(substr($part, $pos + 1, $close - $pos - 1));
            if ($idxStr === '*' || $idxStr === '' || $idxStr === '++' || preg_match('/^(?:[A-Za-z_]\w*\+\+|\+\+[A-Za-z_]\w*)$/', $idxStr)) {
                $steps[] = [$first ? $prop : null, null, true];
            } elseif (preg_match('/^-?\d+$/', $idxStr)) {
                $steps[] = [$first ? $prop : null, (int)$idxStr, false];
            } else {
                throw new RuntimeException("Index invalide dans {$rawPath}");
            }
            $first = false;
            $pos = $close + 1;
        }
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

function extractJsonPath(string $body, string $rawPath): array {
    try {
        $steps = parseJsonPath($rawPath);
        foreach ($steps as [, , $isWildcard]) {
            if ($isWildcard) {
                return [false, 'Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]].'];
            }
        }
        $clean = ltrim($body, "\xEF\xBB\xBF");
        $current = json_decode($clean, false, 512, JSON_THROW_ON_ERROR);
        foreach ($steps as [$prop, $arrIdx]) {
            if ($prop !== null) {
                if (!is_object($current) || !property_exists($current, $prop)) {
                    return [false, "Propriété JSON introuvable : {$prop}"];
                }
                $current = $current->{$prop};
            }
            if ($arrIdx !== null) {
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

function extractJsonPathList(string $body, string $rawPath): array {
    try {
        $steps = parseJsonPath($rawPath);
        $clean = ltrim($body, "\xEF\xBB\xBF");
        $currentElements = [json_decode($clean, false, 512, JSON_THROW_ON_ERROR)];
        $hasWildcard = false;

        foreach ($steps as [$prop, $arrIdx, $isWildcard]) {
            if ($prop !== null) {
                $nextElements = [];
                foreach ($currentElements as $el) {
                    if (!is_object($el) || !property_exists($el, $prop)) {
                        if (!$hasWildcard) {
                            return [false, "Propriété JSON introuvable : {$prop}"];
                        }
                        continue;
                    }
                    $nextElements[] = $el->{$prop};
                }
                if (count($nextElements) === 0) {
                    return [false, "Propriété JSON introuvable : {$prop}"];
                }
                $currentElements = $nextElements;
            }

            if ($isWildcard) {
                $wasWildcard = $hasWildcard;
                $hasWildcard = true;
                $nextElements = [];
                foreach ($currentElements as $el) {
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
                foreach ($currentElements as $el) {
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

function runExtraction(array $ext, string $respBody, array $respHeaders): array {
    if ($ext['source'] === 'header') {
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
            [$ok, $res] = extractRegex($val, $ext['expression'], $ext['regexGroup']);
            if ($ok) return [true, $res];
            $lastErr = $res;
        }
        return [false, $lastErr];
    }
    if ($ext['kind'] === 'jsonpath') {
        return extractJsonPath($respBody, $ext['expression']);
    }
    return extractRegex($respBody, $ext['expression'], $ext['regexGroup']);
}

function runListExtraction(array $ext, string $respBody, array $respHeaders): array {
    if ($ext['source'] === 'header') {
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
        $collected = [];
        $lastErr = 'Aucune correspondance';
        foreach ($matching as $val) {
            [$ok, $res] = extractRegexList($val, $ext['expression'], $ext['regexGroup']);
            if ($ok) {
                foreach ($res as $item) {
                    $collected[] = $item;
                }
            } else {
                $lastErr = $res;
            }
        }
        return count($collected) > 0 ? [true, $collected] : [false, $lastErr];
    }
    if ($ext['kind'] === 'jsonpath') {
        return extractJsonPathList($respBody, $ext['expression']);
    }
    return extractRegexList($respBody, $ext['expression'], $ext['regexGroup']);
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

$verbose = in_array('--verbose', $argv, true) || in_array('-v', $argv, true);
$accumulatedVars = [];
$accumulatedLists = [];
$globalIndex = 0;
$hasAnyFailure = false;
$stepCount = count($steps);

foreach ($steps as $stepIdx => $step) {
    if ($step['url'] === '') {
        fwrite(STDERR, "[ERREUR] Étape {$step['rank']} ({$step['name']}) : URL vide.\n");
        exit(1);
    }

    [$iterations, $emptyListName] = buildIterations($step['axes'], $step['mode'], $accumulatedLists);
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

        try {
            $finalBaseUrl = substituteSimple($step['url'], $mergedVars, $listVars, $globalIndex, $reqGuid, $reqTs, true);
            $queryItems = [];
            foreach ($step['queryParams'] as $qp) {
                if ($qp['axisIndex'] !== null) {
                    $queryItems[] = [$qp['key'], $axisValues[$qp['axisIndex']]];
                } else {
                    $queryItems[] = [$qp['key'], substituteSimple($qp['value'], $mergedVars, $listVars, $globalIndex, $reqGuid, $reqTs)];
                }
            }
            $finalUrl = buildFinalUrl($finalBaseUrl, $queryItems);

            $finalHeaders = [];
            foreach ($step['headers'] as [$hk, $hv]) {
                $finalHeaders[] = [$hk, substituteSimple($hv, $mergedVars, $listVars, $globalIndex, $reqGuid, $reqTs)];
            }

            $bodyToSend = null;
            if ($step['bodyType'] === 'json') {
                $bodyToSend = substituteJsonBody($step['body'], $mergedVars, $listVars, $globalIndex, $reqGuid, $reqTs);
            } elseif ($step['bodyType'] === 'text') {
                $bodyToSend = substituteSimple($step['body'], $mergedVars, $listVars, $globalIndex, $reqGuid, $reqTs);
            } elseif ($step['bodyType'] === 'form') {
                $formPairs = [];
                foreach ($step['formFields'] as [$fk, $fv]) {
                    $formPairs[] = rawurlencode((string)$fk) . '=' . rawurlencode(substituteSimple($fv, $mergedVars, $listVars, $globalIndex, $reqGuid, $reqTs));
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

        foreach ($step['extractions'] as $ext) {
            if (!empty($ext['isList'])) {
                [$ok, $valuesOrErr] = runListExtraction($ext, $respBody, $respHeaders);
                if ($ok) {
                    $accumulatedLists[$ext['variable']] = $valuesOrErr;
                    if ($verbose) {
                        $cnt = count($valuesOrErr);
                        echo "  -> [[{$ext['variable']}]] = {$cnt} élément(s)\n";
                    }
                } else {
                    $accumulatedLists[$ext['variable']] = $ext['defaultValue'] !== '' ? [$ext['defaultValue']] : [];
                    if ($ext['required']) {
                        $extractionFailed = true;
                        fwrite(STDERR, "  [EXTRACTION] [[{$ext['variable']}]] : {$valuesOrErr}\n");
                    }
                }
                continue;
            }

            [$ok, $valueOrErr] = runExtraction($ext, $respBody, $respHeaders);
            if ($ok) {
                $accumulatedVars[$ext['variable']] = $valueOrErr;
                if ($verbose) {
                    echo "  -> {$ext['variable']} = {$valueOrErr}\n";
                }
            } else {
                $accumulatedVars[$ext['variable']] = $ext['defaultValue'];
                if ($ext['required']) {
                    $extractionFailed = true;
                    fwrite(STDERR, "  [EXTRACTION] {$ext['variable']} : {$valueOrErr}\n");
                }
            }
        }

        $stepFailed = $httpFailed || $extractionFailed;
        $statusLabel = $stepFailed ? 'ÉCHEC' : 'OK';
        $codeLabel = $netErr !== null ? $netErr : "HTTP {$status}";
        echo "[{$statusLabel}] Étape {$step['rank']} ({$step['name']}) [{$iterIdx}/{$iterCount}] {$step['method']} {$finalUrl} -> {$codeLabel} ({$elapsedMs} ms)\n";

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
