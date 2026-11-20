using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ApiTester.Services;

internal static class JavaScriptChainCliEmitter
{
    public static string Emit(IReadOnlyList<CliStepSpec> steps)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("#!/usr/bin/env node");
        sb.AppendLine("// Script CLI généré par ApiTester (Node.js 18+ requis, aucune dépendance externe)");
        sb.AppendLine("// Utilisation : node chaine_api.js [--verbose]");
        sb.AppendLine();
        sb.AppendLine("\"use strict\";");
        sb.AppendLine();
        sb.AppendLine("const { randomUUID } = require(\"node:crypto\");");
        sb.AppendLine("const MARKER_RE = /(?:\\{\\{\\s*(\\$?(?!\\d)[\\p{L}\\p{N}_]+)\\s*\\}\\}|\\[\\[\\s*((?!\\d)[\\p{L}\\p{N}_]+)\\s*\\]\\])/gu;");
        sb.AppendLine();

        AppendStepsData(sb, steps);
        AppendRuntimeHelpers(sb);

        return sb.ToString();
    }

    private static void AppendStepsData(StringBuilder sb, IReadOnlyList<CliStepSpec> steps)
    {
        sb.AppendLine("const STEPS = [");
        foreach (var step in steps)
        {
            sb.AppendLine("  {");
            sb.AppendLine($"    rank: {step.Rank},");
            sb.AppendLine($"    name: {EscapeString(step.Name)},");
            sb.AppendLine($"    method: {EscapeString(step.Method)},");
            sb.AppendLine($"    url: {EscapeString(step.Url)},");
            sb.AppendLine($"    timeoutSeconds: {step.TimeoutSeconds},");
            sb.AppendLine($"    delayAfterMs: {step.DelayAfterMs},");
            sb.AppendLine($"    continueOnError: {FormatBool(step.ContinueOnError)},");
            sb.AppendLine($"    mode: {EscapeString(step.Mode)},");
            sb.AppendLine($"    bodyType: {EscapeString(step.BodyType)},");
            sb.AppendLine($"    body: {EscapeString(step.Body)},");

            sb.AppendLine("    headers: [");
            foreach (var h in step.Headers)
            {
                sb.AppendLine($"      [{EscapeString(h.Key)}, {EscapeString(h.Value)}],");
            }
            sb.AppendLine("    ],");

            sb.AppendLine("    queryParams: [");
            foreach (var qp in step.QueryParams)
            {
                var axisIdx = qp.AxisIndex.HasValue
                    ? qp.AxisIndex.Value.ToString(CultureInfo.InvariantCulture)
                    : "null";
                sb.AppendLine($"      {{ key: {EscapeString(qp.Key)}, value: {EscapeString(qp.Value)}, axisIndex: {axisIdx} }},");
            }
            sb.AppendLine("    ],");

            sb.AppendLine("    formFields: [");
            foreach (var ff in step.FormFields)
            {
                sb.AppendLine($"      [{EscapeString(ff.Key)}, {EscapeString(ff.Value)}],");
            }
            sb.AppendLine("    ],");

            sb.AppendLine("    fixedVariables: {");
            foreach (var kv in step.FixedVariables)
            {
                sb.AppendLine($"      {EscapeString(kv.Key)}: {EscapeString(kv.Value)},");
            }
            sb.AppendLine("    },");

            sb.AppendLine("    axes: [");
            foreach (var axis in step.Axes)
            {
                sb.AppendLine("      {");
                sb.AppendLine($"        kind: {EscapeString(axis.Kind)},");
                sb.AppendLine($"        name: {EscapeString(axis.Name)},");
                if (axis.Range is { } r)
                {
                    sb.AppendLine($"        range: {{ start: {EscapeString(r.Start)}, end: {EscapeString(r.End)}, step: {EscapeString(r.Step)}, base: {r.Base}, width: {r.Width}, uppercase: {FormatBool(r.UppercaseHex)}, prefix: {FormatBool(r.UsePrefix)} }},");
                }
                else
                {
                    sb.Append("        values: [");
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
                sb.AppendLine("      },");
            }
            sb.AppendLine("    ],");

            sb.AppendLine("    extractions: [");
            foreach (var ext in step.Extractions)
            {
                sb.AppendLine("      {");
                sb.AppendLine($"        variable: {EscapeString(ext.VariableName)},");
                sb.AppendLine($"        source: {EscapeString(ext.Source)},");
                sb.AppendLine($"        kind: {EscapeString(ext.Kind)},");
                sb.AppendLine($"        expression: {EscapeString(ext.Expression)},");
                sb.AppendLine($"        regexGroup: {ext.RegexGroup},");
                sb.AppendLine($"        headerName: {EscapeString(ext.HeaderName)},");
                sb.AppendLine($"        required: {FormatBool(ext.IsRequired)},");
                sb.AppendLine($"        defaultValue: {EscapeString(ext.DefaultValueIfMissing)},");
                sb.AppendLine($"        isList: {FormatBool(ext.IsList)},");
                sb.AppendLine("      },");
            }
            sb.AppendLine("    ],");

            sb.AppendLine("  },");
        }
        sb.AppendLine("];");
        sb.AppendLine();
    }

    private static void AppendRuntimeHelpers(StringBuilder sb)
    {
        sb.AppendLine("""
function formatNumber(value, baseNum, width, uppercaseHex, usePrefix) {
  const negative = value < 0n;
  const magnitude = negative ? -value : value;
  let digits;
  let prefix = "";
  if (baseNum === 2) {
    digits = magnitude.toString(2);
    if (usePrefix) prefix = "0b";
  } else if (baseNum === 8) {
    digits = magnitude.toString(8);
    if (usePrefix) prefix = "0o";
  } else if (baseNum === 16) {
    digits = magnitude.toString(16);
    if (uppercaseHex) digits = digits.toUpperCase();
    if (usePrefix) prefix = uppercaseHex ? "0X" : "0x";
  } else {
    digits = magnitude.toString(10);
  }
  if (width > 0 && digits.length < width) {
    digits = digits.padStart(width, "0");
  }
  return (negative ? "-" : "") + prefix + digits;
}

function makeRange(spec) {
  const start = BigInt(spec.start);
  const end = BigInt(spec.end);
  const step = BigInt(spec.step);
  if (step === 0n || (start <= end && step < 0n) || (start > end && step > 0n)) {
    return [];
  }
  const values = [];
  for (let cur = start; step > 0n ? cur <= end : cur >= end; cur += step) {
    values.push(formatNumber(cur, spec.base, spec.width, spec.uppercase, spec.prefix));
  }
  return values;
}

function buildIterations(axes, mode, accumulatedLists) {
  if (!axes.length) return { iterations: [[]], emptyListName: null };
  const lists = [];
  for (const a of axes) {
    let lst;
    if (a.kind === "list") {
      lst = Array.isArray(accumulatedLists[a.name]) ? [...accumulatedLists[a.name]] : [];
      if (lst.length === 0) return { iterations: [], emptyListName: a.name };
    } else if (a.range) {
      lst = makeRange(a.range);
      if (lst.length === 0) return { iterations: [], emptyListName: null };
    } else {
      lst = [...a.values];
      if (lst.length === 0) return { iterations: [], emptyListName: null };
    }
    lists.push(lst);
  }

  if (mode === "parallel") {
    const minLen = Math.min(...lists.map((l) => l.length));
    const rows = [];
    for (let i = 0; i < minLen; i++) {
      rows.push(lists.map((l) => l[i]));
    }
    return { iterations: rows, emptyListName: null };
  }

  let acc = [[]];
  for (const list of lists) {
    const next = [];
    for (const prefix of acc) {
      for (const item of list) {
        next.push([...prefix, item]);
      }
    }
    acc = next;
  }
  return { iterations: acc, emptyListName: null };
}

function resolveMarker(scalarName, listName, variables, listVars, globalIndex, reqGuid, reqTs) {
  if (listName !== undefined && listName !== null) {
    if (Object.prototype.hasOwnProperty.call(listVars, listName)) {
      return listVars[listName];
    }
    throw new Error(`Liste inconnue ou non définie : [[${listName}]]`);
  }
  if (Object.prototype.hasOwnProperty.call(variables, scalarName)) {
    return variables[scalarName];
  }
  if (scalarName === "$index") return String(globalIndex);
  if (scalarName === "$guid") return reqGuid;
  if (scalarName === "$timestamp") return reqTs;
  throw new Error(`Variable inconnue ou non définie : {{${scalarName}}}`);
}

function substituteSimple(text, variables, listVars, globalIndex, reqGuid, reqTs, urlEncode = false) {
  if (!text) return "";
  return text.replace(MARKER_RE, (_, scalarName, listName) => {
    const val = resolveMarker(scalarName, listName, variables, listVars, globalIndex, reqGuid, reqTs);
    return urlEncode ? encodeURIComponent(val) : val;
  });
}

function substituteJsonBody(text, variables, listVars, globalIndex, reqGuid, reqTs) {
  if (!text) return "";
  const matches = Array.from(text.matchAll(MARKER_RE));
  if (matches.length === 0) return text;

  let out = "";
  let lastScanned = 0;
  let inString = false;
  let escaped = false;

  for (const match of matches) {
    const matchStart = match.index ?? 0;
    for (let i = lastScanned; i < matchStart; i++) {
      const ch = text[i];
      if (inString) {
        if (escaped) {
          escaped = false;
        } else if (ch === "\\") {
          escaped = true;
        } else if (ch === "\"") {
          inString = false;
        }
      } else if (ch === "\"") {
        inString = true;
        escaped = false;
      }
    }

    out += text.slice(lastScanned, matchStart);
    const val = resolveMarker(match[1], match[2], variables, listVars, globalIndex, reqGuid, reqTs);
    out += inString ? JSON.stringify(val).slice(1, -1) : val;
    lastScanned = matchStart + match[0].length;
  }

  out += text.slice(lastScanned);
  return out;
}

function buildFinalUrl(rawUrl, queryItems) {
  const trimmed = rawUrl.trim();
  if (!queryItems.length) return trimmed;
  const encodedParts = queryItems.map(
    ([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`
  );
  try {
    const parsed = new URL(trimmed);
    const existing = parsed.search.startsWith("?") ? parsed.search.slice(1) : "";
    parsed.search = existing ? `?${existing}&${encodedParts.join("&")}` : `?${encodedParts.join("&")}`;
    return parsed.toString();
  } catch {
    const sep = trimmed.includes("?") ? "&" : "?";
    return `${trimmed}${sep}${encodedParts.join("&")}`;
  }
}

function parseJsonPath(rawPath) {
  let path = rawPath.trim();
  if (path.startsWith("$.")) path = path.slice(2);
  else if (path.startsWith("$")) path = path.slice(1);
  if (!path.trim()) throw new Error("Chemin JSONPath vide.");

  const steps = [];
  for (const part of path.split(".")) {
    if (!part) throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
    const bracket = part.indexOf("[");
    if (bracket < 0) {
      steps.push({ prop: part, arrIdx: null, isWildcard: false });
      continue;
    }
    const prop = bracket > 0 ? part.slice(0, bracket) : null;
    let pos = bracket;
    let first = true;
    while (pos < part.length) {
      if (part[pos] !== "[") throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
      const close = part.indexOf("]", pos + 1);
      if (close < 0) throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
      const idxText = part.slice(pos + 1, close).trim();
      if (idxText === "*" || idxText === "" || idxText === "++" || /^(?:[A-Za-z_]\w*\+\+|\+\+[A-Za-z_]\w*)$/.test(idxText)) {
        steps.push({ prop: first ? prop : null, arrIdx: null, isWildcard: true });
      } else {
        const idx = Number.parseInt(idxText, 10);
        if (Number.isNaN(idx)) throw new Error(`Index invalide dans ${rawPath}`);
        steps.push({ prop: first ? prop : null, arrIdx: idx, isWildcard: false });
      }
      first = false;
      pos = close + 1;
    }
  }
  return steps;
}

function formatJsonValue(current) {
  return typeof current === "string" ? current : JSON.stringify(current);
}

function extractJsonPath(body, rawPath) {
  try {
    const steps = parseJsonPath(rawPath);
    if (steps.some((s) => s.isWildcard)) {
      return { ok: false, value: "Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]]." };
    }
    let current = JSON.parse(body.replace(/^\uFEFF/, ""));
    for (const step of steps) {
      if (step.prop !== null) {
        if (current === null || typeof current !== "object" || Array.isArray(current) || !(step.prop in current)) {
          return { ok: false, value: `Propriété JSON introuvable : ${step.prop}` };
        }
        current = current[step.prop];
      }
      if (step.arrIdx !== null) {
        if (!Array.isArray(current)) {
          return { ok: false, value: `Index [${step.arrIdx}] inapplicable` };
        }
        const resolved = step.arrIdx < 0 ? current.length + step.arrIdx : step.arrIdx;
        if (resolved < 0 || resolved >= current.length) {
          return { ok: false, value: `Index [${step.arrIdx}] hors limites` };
        }
        current = current[resolved];
      }
    }
    return { ok: true, value: formatJsonValue(current) };
  } catch (err) {
    return { ok: false, value: err instanceof Error ? err.message : String(err) };
  }
}

function extractJsonPathList(body, rawPath) {
  try {
    const steps = parseJsonPath(rawPath);
    let currentElements = [JSON.parse(body.replace(/^\uFEFF/, ""))];
    let hasWildcard = false;

    for (const step of steps) {
      if (step.prop !== null) {
        const nextElements = [];
        for (const el of currentElements) {
          if (el === null || typeof el !== "object" || Array.isArray(el) || !(step.prop in el)) {
            if (!hasWildcard) {
              return { ok: false, error: `Propriété JSON introuvable : ${step.prop}` };
            }
            continue;
          }
          nextElements.push(el[step.prop]);
        }
        if (nextElements.length === 0) {
          return { ok: false, error: `Propriété JSON introuvable : ${step.prop}` };
        }
        currentElements = nextElements;
      }

      if (step.isWildcard) {
        const wasWildcard = hasWildcard;
        hasWildcard = true;
        const nextElements = [];
        for (const el of currentElements) {
          if (!Array.isArray(el)) {
            if (!wasWildcard) {
              return { ok: false, error: "Le sélecteur [*] est inapplicable sur cet élément" };
            }
            continue;
          }
          nextElements.push(...el);
        }
        if (nextElements.length === 0) {
          return { ok: false, error: "Le tableau JSON ciblé est vide." };
        }
        currentElements = nextElements;
      } else if (step.arrIdx !== null) {
        const nextElements = [];
        for (const el of currentElements) {
          if (!Array.isArray(el)) {
            if (!hasWildcard) {
              return { ok: false, error: `Index [${step.arrIdx}] inapplicable` };
            }
            continue;
          }
          const resolved = step.arrIdx < 0 ? el.length + step.arrIdx : step.arrIdx;
          if (resolved < 0 || resolved >= el.length) {
            if (!hasWildcard) {
              return { ok: false, error: `Index [${step.arrIdx}] hors limites` };
            }
            continue;
          }
          nextElements.push(el[resolved]);
        }
        if (nextElements.length === 0) {
          return { ok: false, error: `Aucune valeur trouvée pour l'index [${step.arrIdx}]` };
        }
        currentElements = nextElements;
      }
    }

    if (!hasWildcard && currentElements.length === 1 && Array.isArray(currentElements[0])) {
      if (currentElements[0].length === 0) {
        return { ok: false, error: "Le tableau JSON ciblé est vide." };
      }
      return { ok: true, values: currentElements[0].map(formatJsonValue) };
    }

    if (currentElements.length === 0) {
      return { ok: false, error: "Aucune valeur extraite pour l'expression JSONPath." };
    }
    return { ok: true, values: currentElements.map(formatJsonValue) };
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  }
}

function extractRegex(text, pattern, groupIdx) {
  let regex;
  try {
    regex = new RegExp(pattern, "u");
  } catch {
    try {
      regex = new RegExp(pattern);
    } catch (err) {
      return { ok: false, value: `Regex invalide : ${err instanceof Error ? err.message : String(err)}` };
    }
  }
  const match = regex.exec(text);
  if (!match) return { ok: false, value: "Aucune correspondance regex" };
  if (groupIdx >= match.length) return { ok: false, value: `Groupe regex #${groupIdx} inexistant` };
  if (match[groupIdx] === undefined) return { ok: false, value: `Le groupe regex #${groupIdx} n'a rien capturé` };
  return { ok: true, value: match[groupIdx] };
}

function extractRegexList(text, pattern, groupIdx) {
  let regex;
  try {
    regex = new RegExp(pattern, "gu");
  } catch {
    try {
      regex = new RegExp(pattern, "g");
    } catch (err) {
      return { ok: false, error: `Regex invalide : ${err instanceof Error ? err.message : String(err)}` };
    }
  }
  const values = [];
  for (const match of text.matchAll(regex)) {
    if (groupIdx >= match.length) {
      return { ok: false, error: `Groupe regex #${groupIdx} inexistant` };
    }
    if (match[groupIdx] !== undefined) {
      values.push(match[groupIdx]);
    }
  }
  if (values.length === 0) {
    return { ok: false, error: "Aucune correspondance regex" };
  }
  return { ok: true, values };
}

function runExtraction(ext, respBody, respHeaders) {
  if (ext.source === "header") {
    const targetName = ext.headerName.toLowerCase();
    const matching = respHeaders.filter(([k]) => k.toLowerCase() === targetName).map(([, v]) => v);
    if (!matching.length) {
      return { ok: false, value: `En-tête « ${ext.headerName} » absent` };
    }
    let lastErr = "Aucune correspondance";
    for (const val of matching) {
      const res = extractRegex(val, ext.expression, ext.regexGroup);
      if (res.ok) return res;
      lastErr = res.value;
    }
    return { ok: false, value: lastErr };
  }
  if (ext.kind === "jsonpath") {
    return extractJsonPath(respBody, ext.expression);
  }
  return extractRegex(respBody, ext.expression, ext.regexGroup);
}

function runListExtraction(ext, respBody, respHeaders) {
  if (ext.source === "header") {
    const targetName = ext.headerName.toLowerCase();
    const matching = respHeaders.filter(([k]) => k.toLowerCase() === targetName).map(([, v]) => v);
    if (!matching.length) {
      return { ok: false, error: `En-tête « ${ext.headerName} » absent` };
    }
    const collected = [];
    let lastErr = "Aucune correspondance";
    for (const val of matching) {
      const res = extractRegexList(val, ext.expression, ext.regexGroup);
      if (res.ok) collected.push(...res.values);
      else lastErr = res.error;
    }
    return collected.length > 0 ? { ok: true, values: collected } : { ok: false, error: lastErr };
  }
  if (ext.kind === "jsonpath") {
    return extractJsonPathList(respBody, ext.expression);
  }
  return extractRegexList(respBody, ext.expression, ext.regexGroup);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function main() {
  const verbose = process.argv.includes("--verbose") || process.argv.includes("-v");
  const accumulatedVars = {};
  const accumulatedLists = {};
  let globalIndex = 0;
  let hasAnyFailure = false;

  for (let stepIdx = 0; stepIdx < STEPS.length; stepIdx++) {
    const step = STEPS[stepIdx];
    if (!step.url) {
      console.error(`[ERREUR] Étape ${step.rank} (${step.name}) : URL vide.`);
      process.exit(1);
    }

    const { iterations, emptyListName } = buildIterations(step.axes, step.mode, accumulatedLists);
    if (emptyListName !== null) {
      console.log(`[IGNORÉE] Étape ${step.rank} (${step.name}) : liste [[${emptyListName}]] vide.`);
      continue;
    }

    for (let iterIdx = 0; iterIdx < iterations.length; iterIdx++) {
      const axisValues = iterations[iterIdx];
      globalIndex++;
      const reqGuid = randomUUID();
      const reqTs = String(Math.floor(Date.now() / 1000));

      const mergedVars = { ...accumulatedVars, ...step.fixedVariables };
      const listVars = {};
      for (let aIdx = 0; aIdx < step.axes.length; aIdx++) {
        if (step.axes[aIdx].kind === "var") {
          mergedVars[step.axes[aIdx].name] = axisValues[aIdx];
        } else if (step.axes[aIdx].kind === "list") {
          listVars[step.axes[aIdx].name] = axisValues[aIdx];
        }
      }

      let finalUrl;
      let finalHeaders;
      let bodyToSend;
      try {
        const finalBaseUrl = substituteSimple(step.url, mergedVars, listVars, globalIndex, reqGuid, reqTs, true);
        const queryItems = step.queryParams.map((qp) =>
          qp.axisIndex !== null
            ? [qp.key, axisValues[qp.axisIndex]]
            : [qp.key, substituteSimple(qp.value, mergedVars, listVars, globalIndex, reqGuid, reqTs)]
        );
        finalUrl = buildFinalUrl(finalBaseUrl, queryItems);

        finalHeaders = new Headers();
        for (const [k, v] of step.headers) {
          finalHeaders.append(k, substituteSimple(v, mergedVars, listVars, globalIndex, reqGuid, reqTs));
        }

        if (step.method !== "GET" && step.method !== "HEAD") {
          if (step.bodyType === "json") {
            bodyToSend = substituteJsonBody(step.body, mergedVars, listVars, globalIndex, reqGuid, reqTs);
          } else if (step.bodyType === "text") {
            bodyToSend = substituteSimple(step.body, mergedVars, listVars, globalIndex, reqGuid, reqTs);
          } else if (step.bodyType === "form") {
            const params = new URLSearchParams();
            for (const [fk, fv] of step.formFields) {
              params.append(fk, substituteSimple(fv, mergedVars, listVars, globalIndex, reqGuid, reqTs));
            }
            bodyToSend = params.toString();
          }
        }
      } catch (err) {
        console.error(`[ÉCHEC] Étape ${step.rank} (${step.name}) [${iterIdx + 1}/${iterations.length}] : ${err instanceof Error ? err.message : String(err)}`);
        hasAnyFailure = true;
        if (!step.continueOnError) process.exit(1);
        continue;
      }

      const t0 = Date.now();
      let status = 0;
      let respHeaders = [];
      let respBody = "";
      let netErr = null;

      try {
        const resp = await fetch(finalUrl, {
          method: step.method,
          headers: finalHeaders,
          body: bodyToSend,
          signal: AbortSignal.timeout(step.timeoutSeconds * 1000)
        });
        status = resp.status;
        respHeaders = Array.from(resp.headers.entries());
        respBody = await resp.text();
      } catch (err) {
        netErr = err instanceof Error ? err.message : String(err);
      }
      const elapsedMs = Date.now() - t0;

      const httpFailed = netErr !== null || status >= 400;
      let extractionFailed = false;

      for (const ext of step.extractions) {
        if (ext.isList) {
          const listRes = runListExtraction(ext, respBody, respHeaders);
          if (listRes.ok) {
            accumulatedLists[ext.variable] = listRes.values;
            if (verbose) console.log(`  -> [[${ext.variable}]] = ${listRes.values.length} élément(s)`);
          } else {
            accumulatedLists[ext.variable] = ext.defaultValue ? [ext.defaultValue] : [];
            if (ext.required) {
              extractionFailed = true;
              console.error(`  [EXTRACTION] [[${ext.variable}]] : ${listRes.error}`);
            }
          }
          continue;
        }

        const res = runExtraction(ext, respBody, respHeaders);
        if (res.ok) {
          accumulatedVars[ext.variable] = res.value;
          if (verbose) console.log(`  -> ${ext.variable} = ${res.value}`);
        } else {
          accumulatedVars[ext.variable] = ext.defaultValue;
          if (ext.required) {
            extractionFailed = true;
            console.error(`  [EXTRACTION] ${ext.variable} : ${res.value}`);
          }
        }
      }

      const stepFailed = httpFailed || extractionFailed;
      const statusLabel = stepFailed ? "ÉCHEC" : "OK";
      const codeLabel = netErr !== null ? netErr : `HTTP ${status}`;
      console.log(`[${statusLabel}] Étape ${step.rank} (${step.name}) [${iterIdx + 1}/${iterations.length}] ${step.method} ${finalUrl} -> ${codeLabel} (${elapsedMs} ms)`);

      if (verbose && respBody) {
        console.log(respBody.slice(0, 1000));
      }

      if (stepFailed) {
        hasAnyFailure = true;
        if (!step.continueOnError) process.exit(1);
      }

      const hasMore = iterIdx + 1 < iterations.length || stepIdx + 1 < STEPS.length;
      if (hasMore && step.delayAfterMs > 0) {
        await sleep(step.delayAfterMs);
      }
    }
  }

  process.exit(hasAnyFailure ? 1 : 0);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
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
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\u2028':
                    sb.Append("\\u2028");
                    break;
                case '\u2029':
                    sb.Append("\\u2029");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
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
