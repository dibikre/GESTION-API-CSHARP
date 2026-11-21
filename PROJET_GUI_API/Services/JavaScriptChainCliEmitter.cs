using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ApiTester.Models;

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
        sb.AppendLine("const os = require(\"node:os\");");
        sb.AppendLine("const net = require(\"node:net\");");
        sb.AppendLine("const MARKER_RE = /(?:\\{\\{\\s*(\\$(?!\\d)[\\p{L}\\p{N}_]+|(?!\\d)(?!\\$)[\\p{L}\\p{N}_]+(?:(?:\\.[^.{}\\[\\]\\r\\n\\s]+|\\[[^{}\\[\\]\\r\\n]*\\])+)?)\\s*\\}\\}|\\[\\[\\s*((?!\\d)(?!\\$)[\\p{L}\\p{N}_]+(?:(?:\\.[^.{}\\[\\]\\r\\n\\s]+|\\[[^{}\\[\\]\\r\\n]*\\])+)?)\\s*\\]\\])/gu;");
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
            sb.AppendLine($"    isReturnOnly: {FormatBool(step.IsReturnOnly)},");
            sb.AppendLine($"    returnNode: {FormatReturnNode(step.ReturnNode)},");
            sb.AppendLine($"    returnError: {(step.ReturnError is null ? "null" : EscapeString(step.ReturnError))},");

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
                sb.AppendLine($"        sourceVariable: {EscapeString(ext.SourceVariableName)},");
                sb.AppendLine($"        required: {FormatBool(ext.IsRequired)},");
                sb.AppendLine($"        defaultValue: {EscapeString(ext.DefaultValueIfMissing)},");
                sb.AppendLine($"        isList: {FormatBool(ext.IsList)},");
                sb.AppendLine($"        conditionMode: {EscapeString(ext.ConditionMatchMode)},");
                sb.AppendLine($"        deduplicate: {FormatBool(ext.DeduplicateList)},");
                sb.AppendLine("        conditions: [");
                foreach (var c in ext.Conditions)
                {
                    var thenJs = $"{{ action: {EscapeString(c.ThenBranch.Action)}, value: {EscapeString(c.ThenBranch.Value)}, replacement: {EscapeString(c.ThenBranch.Replacement)}, regexGroup: {c.ThenBranch.RegexGroup}, segmentIndex: {c.ThenBranch.SegmentIndex}, ignoreCase: {FormatBool(c.ThenBranch.IgnoreCase)} }}";
                    var elseJs = $"{{ action: {EscapeString(c.ElseBranch.Action)}, value: {EscapeString(c.ElseBranch.Value)}, replacement: {EscapeString(c.ElseBranch.Replacement)}, regexGroup: {c.ElseBranch.RegexGroup}, segmentIndex: {c.ElseBranch.SegmentIndex}, ignoreCase: {FormatBool(c.ElseBranch.IgnoreCase)} }}";
                    sb.AppendLine($"          {{ isConditional: {FormatBool(c.IsConditional)}, op: {EscapeString(c.Operator)}, value: {EscapeString(c.Value)}, replacement: {EscapeString(c.Replacement)}, regexGroup: {c.RegexGroup}, segmentIndex: {c.SegmentIndex}, ignoreCase: {FormatBool(c.IgnoreCase)}, thenBranch: {thenJs}, elseBranch: {elseJs} }},");
                }
                sb.AppendLine("        ],");
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

const ALLOWED_URI_CHARS = new Set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:/?#[]@!$&'()*+,;=%");

function splitVarPath(expr) {
  const trimmed = String(expr || "").trim();
  if (!trimmed || trimmed.startsWith("$")) return { root: trimmed, jsonPath: null };
  const dotIdx = trimmed.indexOf(".");
  const brIdx = trimmed.indexOf("[");
  let sepIdx = -1;
  if (dotIdx >= 0 && brIdx >= 0) sepIdx = Math.min(dotIdx, brIdx);
  else sepIdx = dotIdx >= 0 ? dotIdx : brIdx;
  if (sepIdx <= 0) return { root: trimmed, jsonPath: null };
  const root = trimmed.slice(0, sepIdx).trim();
  const rawPath = trimmed[sepIdx] === "." ? trimmed.slice(sepIdx + 1).trim() : trimmed.slice(sepIdx).trim();
  if (!root || !rawPath) return { root: trimmed, jsonPath: null };
  return { root, jsonPath: rawPath };
}

function isInQueryValueContext(prefix) {
  if (prefix.includes("#")) return false;
  const qIdx = prefix.indexOf("?");
  if (qIdx < 0) return false;
  const afterQ = prefix.slice(qIdx + 1);
  const ampIdx = afterQ.lastIndexOf("&");
  const currentPair = ampIdx >= 0 ? afterQ.slice(ampIdx + 1) : afterQ;
  return currentPair.includes("=");
}

function escapeUrlPreservingStructure(value) {
  if (!value) return "";
  let out = "";
  for (const ch of value) {
    if (ALLOWED_URI_CHARS.has(ch)) {
      out += ch;
    } else {
      const buf = Buffer.from(ch, "utf8");
      for (const b of buf) {
        out += "%" + b.toString(16).toUpperCase().padStart(2, "0");
      }
    }
  }
  return out;
}

function buildIterations(axes, mode, accumulatedLists, scalarVars) {
  if (!axes.length) return { iterations: [[]], emptyListName: null, error: null };
  const lists = [];
  for (const a of axes) {
    let lst;
    if (a.kind === "list") {
      const { root, jsonPath } = splitVarPath(a.name);
      if (jsonPath !== null) {
        let jpRes;
        if (Object.prototype.hasOwnProperty.call(scalarVars, root)) {
          jpRes = extractJsonPathList(scalarVars[root], jsonPath);
        } else if (Object.prototype.hasOwnProperty.call(accumulatedLists, root)) {
          jpRes = extractJsonPathListFromValues(accumulatedLists[root], jsonPath);
        } else {
          return { iterations: [], emptyListName: null, error: `Variable ou liste « ${root} » introuvable pour [[${a.name}]].` };
        }
        if (jpRes.ok && Array.isArray(jpRes.values) && jpRes.values.length > 0) {
          lst = jpRes.values;
        } else if (typeof jpRes.error === "string" && jpRes.error.toLowerCase().includes("est vide")) {
          return { iterations: [], emptyListName: a.name, error: null };
        } else {
          return { iterations: [], emptyListName: null, error: `[[${a.name}]] : ${jpRes.error}` };
        }
      } else {
        lst = Array.isArray(accumulatedLists[a.name]) ? [...accumulatedLists[a.name]] : [];
        if (lst.length === 0) return { iterations: [], emptyListName: a.name, error: null };
      }
    } else if (a.range) {
      lst = makeRange(a.range);
      if (lst.length === 0) return { iterations: [], emptyListName: null, error: null };
    } else {
      lst = [...a.values];
      if (lst.length === 0) return { iterations: [], emptyListName: null, error: null };
    }
    lists.push(lst);
  }

  if (mode === "parallel") {
    const minLen = Math.min(...lists.map((l) => l.length));
    const rows = [];
    for (let i = 0; i < minLen; i++) {
      rows.push(lists.map((l) => l[i]));
    }
    return { iterations: rows, emptyListName: null, error: null };
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
  return { iterations: acc, emptyListName: null, error: null };
}

function resolveMarker(scalarName, listName, variables, listVars, accumulatedLists, globalIndex, reqGuid, reqTs) {
  if (listName !== undefined && listName !== null) {
    if (Object.prototype.hasOwnProperty.call(listVars, listName)) {
      return listVars[listName];
    }
    const { root, jsonPath } = splitVarPath(listName);
    if (jsonPath !== null) {
      if (Object.prototype.hasOwnProperty.call(variables, root)) {
        const jpList = extractJsonPathList(variables[root], jsonPath);
        if (jpList.ok && Array.isArray(jpList.values) && jpList.values.length > 0) {
          return jpList.values[0];
        }
        throw new Error(`[[${listName}]] : ${jpList.error}`);
      }
      if (accumulatedLists && Object.prototype.hasOwnProperty.call(accumulatedLists, root)) {
        const jpList = extractJsonPathListFromValues(accumulatedLists[root], jsonPath);
        if (jpList.ok && Array.isArray(jpList.values) && jpList.values.length > 0) {
          return jpList.values[0];
        }
        throw new Error(`[[${listName}]] : ${jpList.error}`);
      }
    }
    throw new Error(`Liste inconnue ou non définie : [[${listName}]]`);
  }
  if (Object.prototype.hasOwnProperty.call(variables, scalarName)) {
    return variables[scalarName];
  }
  if (scalarName === "$index") return String(globalIndex);
  if (scalarName === "$guid") return reqGuid;
  if (scalarName === "$timestamp") return reqTs;
  const { root, jsonPath } = splitVarPath(scalarName);
  if (jsonPath !== null) {
    if (Object.prototype.hasOwnProperty.call(variables, root)) {
      const jpRes = extractJsonPath(variables[root], jsonPath);
      if (!jpRes.ok) {
        throw new Error(`{{${scalarName}}} : ${jpRes.value}`);
      }
      return jpRes.value;
    }
    if (accumulatedLists && Object.prototype.hasOwnProperty.call(accumulatedLists, root)) {
      const jpRes = extractJsonPathFromValues(accumulatedLists[root], jsonPath);
      if (!jpRes.ok) {
        throw new Error(`{{${scalarName}}} : ${jpRes.value}`);
      }
      return jpRes.value;
    }
    throw new Error(`Variable inconnue ou non définie : {{${root}}} (pour {{${scalarName}}})`);
  }
  throw new Error(`Variable inconnue ou non définie : {{${scalarName}}}`);
}

function substituteUrl(text, variables, listVars, accumulatedLists, globalIndex, reqGuid, reqTs) {
  if (!text) return "";
  const matches = Array.from(text.matchAll(MARKER_RE));
  if (matches.length === 0) return text;
  let out = "";
  let lastIdx = 0;
  for (const match of matches) {
    const matchStart = match.index ?? 0;
    out += text.slice(lastIdx, matchStart);
    const val = resolveMarker(match[1], match[2], variables, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
    if (isInQueryValueContext(out)) {
      out += encodeURIComponent(val);
    } else {
      const hasNonWsPrefix = out.trim().length > 0;
      out += escapeUrlPreservingStructure(hasNonWsPrefix ? val : val.trim());
    }
    lastIdx = matchStart + match[0].length;
  }
  out += text.slice(lastIdx);
  return out;
}

function substituteSimple(text, variables, listVars, accumulatedLists, globalIndex, reqGuid, reqTs, urlEncode = false) {
  if (!text) return "";
  return text.replace(MARKER_RE, (_, scalarName, listName) => {
    const val = resolveMarker(scalarName, listName, variables, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
    return urlEncode ? encodeURIComponent(val) : val;
  });
}

function substituteJsonBody(text, variables, listVars, accumulatedLists, globalIndex, reqGuid, reqTs) {
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
    const val = resolveMarker(match[1], match[2], variables, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
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
  if (path === "$") return [];
  if (path.startsWith("$.")) path = path.slice(2);
  else if (path.startsWith("$")) path = path.slice(1);
  if (!path.trim()) throw new Error("Chemin JSONPath vide.");

  const steps = [];
  let pos = 0;
  const len = path.length;
  while (pos < len) {
    if (path[pos] === ".") {
      if (pos === 0 || pos + 1 >= len || path[pos + 1] === ".") {
        throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
      }
      pos++;
      continue;
    }
    if (path[pos] === "[") {
      while (pos < len && path[pos] === "[") {
        let cursor = pos + 1;
        while (cursor < len && /\s/.test(path[cursor])) cursor++;
        if (cursor < len && (path[cursor] === "\"" || path[cursor] === "'")) {
          const q = path[cursor++];
          let key = "";
          let closedQuote = false;
          while (cursor < len) {
            const ch = path[cursor++];
            if (ch === "\\" && cursor < len) {
              const esc = path[cursor++];
              key += ({ "\\": "\\", "\"": "\"", "'": "'", n: "\n", r: "\r", t: "\t" }[esc] ?? esc);
              continue;
            }
            if (ch === q) {
              closedQuote = true;
              break;
            }
            key += ch;
          }
          if (!closedQuote) throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
          while (cursor < len && /\s/.test(path[cursor])) cursor++;
          if (cursor >= len || path[cursor] !== "]") throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
          steps.push({ prop: key, arrIdx: null, isWildcard: false });
          pos = cursor + 1;
          continue;
        }
        const close = path.indexOf("]", pos + 1);
        if (close < 0) throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
        const idxText = path.slice(pos + 1, close).trim();
        if (idxText === "*" || idxText === "" || idxText === "++" || /^(?:[A-Za-z_]\w*\+\+|\+\+[A-Za-z_]\w*)$/.test(idxText)) {
          steps.push({ prop: null, arrIdx: null, isWildcard: true });
        } else if (/^-?\d+$/.test(idxText)) {
          steps.push({ prop: null, arrIdx: Number.parseInt(idxText, 10), isWildcard: false });
        } else {
          throw new Error(`Index invalide dans ${rawPath}`);
        }
        pos = close + 1;
      }
      if (pos < len && path[pos] !== ".") {
        throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
      }
      continue;
    }
    const segStart = pos;
    while (pos < len && path[pos] !== "." && path[pos] !== "[" && path[pos] !== "]") pos++;
    if (pos < len && path[pos] === "]") throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
    const prop = path.slice(segStart, pos);
    if (!prop) throw new Error(`Chemin JSONPath invalide : ${rawPath}`);
    steps.push({ prop, arrIdx: null, isWildcard: false });
  }
  return steps;
}

function formatJsonValue(current) {
  return typeof current === "string" ? current : JSON.stringify(current);
}

function unwrapJsonString(val) {
  if (typeof val !== "string") return val;
  const trimmed = val.replace(/^[\uFEFF\s]+/, "");
  if (!trimmed.startsWith("{") && !trimmed.startsWith("[")) return val;
  try {
    return JSON.parse(trimmed);
  } catch {
    return val;
  }
}

function getMaxContiguousPropIdx(steps, startIdx) {
  let end = startIdx;
  while (end + 1 < steps.length) {
    const nxt = steps[end + 1];
    if (nxt.prop === null || nxt.arrIdx !== null || nxt.isWildcard) break;
    const curr = steps[end];
    if (curr.arrIdx !== null || curr.isWildcard) break;
    end++;
  }
  return end;
}

function looksLikeJsonArray(raw) {
  if (!raw) return false;
  return String(raw).replace(/^[\uFEFF\s]+/, "").startsWith("[");
}

function extractJsonPathSteps(body, steps, startIdx = 0) {
  try {
    if (!body || !String(body).trim()) {
      return { ok: false, value: "Le corps est vide (JSON attendu)." };
    }
    let current = JSON.parse(String(body).replace(/^\uFEFF/, ""));
    for (let i = startIdx; i < steps.length; i++) {
      const step = steps[i];
      if (step.prop !== null) {
        current = unwrapJsonString(current);
        if (current === null || typeof current !== "object" || Array.isArray(current)) {
          return { ok: false, value: `Propriété JSON introuvable : ${step.prop}` };
        }
        if (Object.prototype.hasOwnProperty.call(current, step.prop)) {
          current = current[step.prop];
        } else {
          let matched = false;
          const maxEnd = getMaxContiguousPropIdx(steps, i);
          for (let endIdx = maxEnd; endIdx > i; endIdx--) {
            const joined = steps.slice(i, endIdx + 1).map((s) => s.prop).join(".");
            if (Object.prototype.hasOwnProperty.call(current, joined)) {
              current = current[joined];
              i = endIdx;
              matched = true;
              break;
            }
          }
          if (!matched) {
            return { ok: false, value: `Propriété JSON introuvable : ${step.prop}` };
          }
        }
      }
      if (step.arrIdx !== null) {
        current = unwrapJsonString(current);
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

function extractJsonPath(body, rawPath) {
  try {
    const steps = parseJsonPath(rawPath);
    if (steps.some((s) => s.isWildcard)) {
      return { ok: false, value: "Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]]." };
    }
    return extractJsonPathSteps(body, steps, 0);
  } catch (err) {
    return { ok: false, value: err instanceof Error ? err.message : String(err) };
  }
}

function extractJsonPathListSteps(body, steps, startIdx = 0) {
  try {
    if (!body || !String(body).trim()) {
      return { ok: false, error: "Le corps est vide (JSON attendu)." };
    }
    let currentElements = [JSON.parse(String(body).replace(/^\uFEFF/, ""))];
    let hasWildcard = false;

    for (let i = startIdx; i < steps.length; i++) {
      let step = steps[i];
      if (step.prop !== null) {
        const exactMatches = [];
        let allExact = true;
        for (const rawEl of currentElements) {
          const el = unwrapJsonString(rawEl);
          if (el === null || typeof el !== "object" || Array.isArray(el) || !Object.prototype.hasOwnProperty.call(el, step.prop)) {
            allExact = false;
            continue;
          }
          exactMatches.push(el[step.prop]);
        }
        if (exactMatches.length > 0 && (allExact || hasWildcard)) {
          currentElements = exactMatches;
        } else {
          let matchedJoined = false;
          const maxEnd = getMaxContiguousPropIdx(steps, i);
          for (let endIdx = maxEnd; endIdx > i; endIdx--) {
            const joined = steps.slice(i, endIdx + 1).map((s) => s.prop).join(".");
            const jMatches = [];
            let allJ = true;
            for (const rawEl of currentElements) {
              const el = unwrapJsonString(rawEl);
              if (el === null || typeof el !== "object" || Array.isArray(el) || !Object.prototype.hasOwnProperty.call(el, joined)) {
                allJ = false;
                continue;
              }
              jMatches.push(el[joined]);
            }
            if (jMatches.length > 0 && (allJ || hasWildcard)) {
              currentElements = jMatches;
              i = endIdx;
              step = steps[i];
              matchedJoined = true;
              break;
            }
          }
          if (!matchedJoined) {
            if (exactMatches.length > 0) {
              currentElements = exactMatches;
            } else {
              return { ok: false, error: `Propriété JSON introuvable : ${step.prop}` };
            }
          }
        }
      }

      if (step.isWildcard) {
        const wasWildcard = hasWildcard;
        hasWildcard = true;
        const nextElements = [];
        for (const rawEl of currentElements) {
          const el = unwrapJsonString(rawEl);
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
        for (const rawEl of currentElements) {
          const el = unwrapJsonString(rawEl);
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

function extractJsonPathList(body, rawPath) {
  try {
    const steps = parseJsonPath(rawPath);
    return extractJsonPathListSteps(body, steps, 0);
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  }
}

function extractJsonPathFromValues(sourceValues, rawPath) {
  if (!Array.isArray(sourceValues) || sourceValues.length === 0) {
    return { ok: false, value: "La liste source est vide." };
  }
  let steps;
  try {
    steps = parseJsonPath(rawPath);
  } catch (err) {
    return { ok: false, value: err instanceof Error ? err.message : String(err) };
  }
  if (steps.some((s) => s.isWildcard)) {
    return { ok: false, value: "Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]]." };
  }
  if (steps.length === 0) {
    return { ok: true, value: sourceValues.length === 1 ? (sourceValues[0] ?? "") : JSON.stringify(sourceValues) };
  }
  if (steps[0].prop === null && steps[0].arrIdx !== null && (sourceValues.length > 1 || !looksLikeJsonArray(sourceValues[0]))) {
    const idx = steps[0].arrIdx;
    const resolved = idx < 0 ? sourceValues.length + idx : idx;
    if (resolved < 0 || resolved >= sourceValues.length) {
      return { ok: false, value: `Index [${idx}] hors limites (taille : ${sourceValues.length})` };
    }
    const chosen = sourceValues[resolved] ?? "";
    if (steps.length === 1) return { ok: true, value: chosen };
    return extractJsonPathSteps(chosen, steps, 1);
  }
  let lastErr = "Aucune correspondance JSONPath dans la variable source.";
  for (const item of sourceValues) {
    const res = extractJsonPathSteps(item ?? "", steps, 0);
    if (res.ok) return res;
    lastErr = res.value;
  }
  return { ok: false, value: lastErr };
}

function extractJsonPathListFromValues(sourceValues, rawPath) {
  if (!Array.isArray(sourceValues) || sourceValues.length === 0) {
    return { ok: false, error: "La liste source est vide." };
  }
  let steps;
  try {
    steps = parseJsonPath(rawPath);
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  }
  if (steps.length === 0) {
    return { ok: true, values: [...sourceValues] };
  }
  if (steps[0].prop === null && (steps[0].isWildcard || steps[0].arrIdx !== null) && (sourceValues.length > 1 || !looksLikeJsonArray(sourceValues[0]))) {
    if (steps[0].isWildcard) {
      if (steps.length === 1) return { ok: true, values: [...sourceValues] };
      const collected = [];
      let lastErr = null;
      for (const item of sourceValues) {
        const res = extractJsonPathListSteps(item ?? "", steps, 1);
        if (res.ok) collected.push(...res.values);
        else lastErr = res.error;
      }
      return collected.length > 0 ? { ok: true, values: collected } : { ok: false, error: lastErr || "Aucune valeur extraite pour l'expression JSONPath." };
    }
    const idx = steps[0].arrIdx;
    const resolved = idx < 0 ? sourceValues.length + idx : idx;
    if (resolved < 0 || resolved >= sourceValues.length) {
      return { ok: false, error: `Index [${idx}] hors limites (taille : ${sourceValues.length})` };
    }
    const chosen = sourceValues[resolved] ?? "";
    if (steps.length === 1) return { ok: true, values: [chosen] };
    return extractJsonPathListSteps(chosen, steps, 1);
  }
  const aggregated = [];
  let lastErr = null;
  for (const item of sourceValues) {
    const res = extractJsonPathListSteps(item ?? "", steps, 0);
    if (res.ok) aggregated.push(...res.values);
    else lastErr = res.error;
  }
  return aggregated.length > 0 ? { ok: true, values: aggregated } : { ok: false, error: lastErr || "Aucune valeur extraite pour l'expression JSONPath." };
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

function compileConditionRegex(pattern, ignoreCase, global = false) {
  const flags = (global ? "g" : "") + (ignoreCase ? "i" : "");
  try {
    return new RegExp(pattern, flags + "u");
  } catch {
    return new RegExp(pattern, flags);
  }
}

function parseConditionList(raw) {
  return String(raw || "").split(/[,;|\r\n]+/).map((s) => s.trim()).filter((s) => s.length > 0);
}

function tryParseCondNumber(raw) {
  const s = String(raw || "").trim().replace(",", ".");
  if (!s) return null;
  const n = Number(s);
  return Number.isFinite(n) ? n : null;
}

function evalConditionFilter(cond, value) {
  const target = cond.value || "";
  const icase = Boolean(cond.ignoreCase);
  const vCmp = icase ? value.toLowerCase() : value;
  const tCmp = icase ? target.toLowerCase() : target;

  switch (cond.op) {
    case "contains": return vCmp.includes(tCmp);
    case "not_contains": return !vCmp.includes(tCmp);
    case "starts_with": return vCmp.startsWith(tCmp);
    case "not_starts_with": return !vCmp.startsWith(tCmp);
    case "ends_with": return vCmp.endsWith(tCmp);
    case "not_ends_with": return !vCmp.endsWith(tCmp);
    case "equals": return vCmp === tCmp;
    case "not_equals": return vCmp !== tCmp;
    case "in_list": {
      const items = parseConditionList(target);
      return items.some((it) => vCmp === (icase ? it.toLowerCase() : it));
    }
    case "not_in_list": {
      const items = parseConditionList(target);
      return !items.some((it) => vCmp === (icase ? it.toLowerCase() : it));
    }
    case "regex_match": return compileConditionRegex(target, icase).test(value);
    case "regex_not_match": return !compileConditionRegex(target, icase).test(value);
    case "is_empty": return value.trim().length === 0;
    case "not_empty": return value.trim().length > 0;
    case "length_equals": return value.length === Number.parseInt(target.trim(), 10);
    case "length_min": return value.length >= Number.parseInt(target.trim(), 10);
    case "length_max": return value.length <= Number.parseInt(target.trim(), 10);
    case "number_equals":
    case "number_not_equals":
    case "greater_than":
    case "greater_or_equal":
    case "less_than":
    case "less_or_equal": {
      const vNum = tryParseCondNumber(value);
      const tNum = tryParseCondNumber(target);
      if (vNum === null || tNum === null) return false;
      if (cond.op === "number_equals") return vNum === tNum;
      if (cond.op === "number_not_equals") return vNum !== tNum;
      if (cond.op === "greater_than") return vNum > tNum;
      if (cond.op === "greater_or_equal") return vNum >= tNum;
      if (cond.op === "less_than") return vNum < tNum;
      return vNum <= tNum;
    }
    default: return true;
  }
}

function escapeRegExpLiteral(str) {
  return str.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function applyConditionExtractParts(pattern, grp, icase, value) {
  const match = compileConditionRegex(pattern, icase).exec(value);
  if (!match) return { ok: false, value: "" };
  if (grp >= match.length || match[grp] === undefined) return { ok: false, value: "" };
  return { ok: true, value: match[grp] };
}

function applyConditionReplaceParts(pattern, rawTpl, icase, value) {
  const regex = compileConditionRegex(pattern, icase, true);
  const jsTpl = rawTpl.replace(/\$\$|\$0|\$\{(\d+)\}/g, (tok, braced) => {
    if (tok === "$$") return "$$";
    if (tok === "$0") return "$&";
    return "$" + braced;
  });
  return value.replace(regex, jsTpl);
}

function applyConditionReplaceTextParts(target, rep, icase, value) {
  if (!target) return value;
  if (icase) {
    return value.replace(new RegExp(escapeRegExpLiteral(target), "gi"), () => rep);
  }
  return value.split(target).join(rep);
}

function applyConditionBetweenParts(left, right, icase, value) {
  const hay = icase ? value.toLowerCase() : value;
  let startIdx = 0;
  if (left) {
    const leftCmp = icase ? left.toLowerCase() : left;
    const pos = hay.indexOf(leftCmp);
    if (pos < 0) return { ok: false, value: "" };
    startIdx = pos + left.length;
  }
  if (right) {
    const rightCmp = icase ? right.toLowerCase() : right;
    const endPos = hay.indexOf(rightCmp, startIdx);
    if (endPos < 0) return { ok: false, value: "" };
    return { ok: true, value: value.slice(startIdx, endPos) };
  }
  return { ok: true, value: value.slice(startIdx) };
}

function applyConditionSplitPartParts(sep, segIdx, icase, value) {
  const parts = icase
    ? value.split(new RegExp(escapeRegExpLiteral(sep), "i"))
    : value.split(sep);
  const resolved = segIdx < 0 ? parts.length + segIdx : segIdx;
  if (resolved < 0 || resolved >= parts.length) return { ok: false, value: "" };
  return { ok: true, value: parts[resolved] };
}

function applyConditionTrimParts(chars, value) {
  if (!chars) return value.trim();
  const escaped = escapeRegExpLiteral(chars);
  return value.replace(new RegExp(`^[${escaped}]+|[${escaped}]+$`, "g"), "");
}

function executeActionBranch(action, val, rep, grp, segIdx, icase, curr, out) {
  switch (action) {
    case "keep":
      out.push(curr);
      break;
    case "exclude":
      break;
    case "set_value":
      out.push(val);
      break;
    case "prepend_text":
      out.push(val + curr);
      break;
    case "append_text":
      out.push(curr + val);
      break;
    case "regex_extract": {
      const ex = applyConditionExtractParts(val, grp, icase, curr);
      if (ex.ok) out.push(ex.value);
      break;
    }
    case "regex_extract_all": {
      const regex = compileConditionRegex(val, icase, true);
      for (const m of curr.matchAll(regex)) {
        if (grp < m.length && m[grp] !== undefined) out.push(m[grp]);
      }
      break;
    }
    case "regex_replace":
      out.push(applyConditionReplaceParts(val, rep, icase, curr));
      break;
    case "replace_text":
      out.push(applyConditionReplaceTextParts(val, rep, icase, curr));
      break;
    case "between": {
      const bt = applyConditionBetweenParts(val, rep, icase, curr);
      if (bt.ok) out.push(bt.value);
      break;
    }
    case "split_part": {
      const sp = applyConditionSplitPartParts(val, segIdx, icase, curr);
      if (sp.ok) out.push(sp.value);
      break;
    }
    case "trim":
      out.push(applyConditionTrimParts(val, curr));
      break;
    case "to_lower":
      out.push(curr.toLowerCase());
      break;
    case "to_upper":
      out.push(curr.toUpperCase());
      break;
    default:
      out.push(curr);
      break;
  }
}

function applySingleRuleBatch(cond, inputs) {
  const out = [];
  if (cond.isConditional) {
    const thenB = cond.thenBranch || { action: "keep" };
    const elseB = cond.elseBranch || { action: "exclude" };
    for (const curr of inputs) {
      const br = evalConditionFilter(cond, curr) ? thenB : elseB;
      executeActionBranch(
        br.action || "keep",
        br.value || "",
        br.replacement || "",
        Number.isInteger(br.regexGroup) ? br.regexGroup : 1,
        Number.isInteger(br.segmentIndex) ? br.segmentIndex : 0,
        Boolean(br.ignoreCase),
        curr,
        out
      );
    }
    return out;
  }

  for (const curr of inputs) {
    if (TRANSFORM_OPS.has(cond.op)) {
      executeActionBranch(
        cond.op,
        cond.value || "",
        cond.replacement || "",
        Number.isInteger(cond.regexGroup) ? cond.regexGroup : 1,
        Number.isInteger(cond.segmentIndex) ? cond.segmentIndex : 0,
        Boolean(cond.ignoreCase),
        curr,
        out
      );
    } else if (evalConditionFilter(cond, curr)) {
      out.push(curr);
    }
  }
  return out;
}

const TRANSFORM_OPS = new Set(["regex_extract", "regex_extract_all", "regex_replace", "replace_text", "prepend_text", "append_text", "set_value", "between", "split_part", "trim", "to_lower", "to_upper"]);
const EXTRACT_OPS = new Set(["regex_extract", "regex_extract_all"]);
const NO_VAL_PREDICATE_OPS = new Set(["is_empty", "not_empty"]);
const NO_VAL_ACTION_OPS = new Set(["keep", "exclude", "set_value", "trim", "to_lower", "to_upper"]);

function validateBranchSpec(action, val, rep, icase) {
  if (action === "between") {
    if (!val && !rep) return "Condition entre délimiteurs vide.";
  } else if (!NO_VAL_ACTION_OPS.has(action) && !val) {
    return "Action ou transformation vide.";
  }
  if (action === "regex_extract" || action === "regex_extract_all" || action === "regex_replace") {
    try {
      compileConditionRegex(val, icase, action !== "regex_extract");
    } catch (err) {
      return `Condition Regex invalide : ${err instanceof Error ? err.message : String(err)}`;
    }
  }
  return null;
}

function validatePredicateSpec(op, val, icase) {
  if (!NO_VAL_PREDICATE_OPS.has(op) && !val) {
    return "Condition d'extraction vide.";
  }
  if ((op === "in_list" || op === "not_in_list") && parseConditionList(val).length === 0) {
    return "Liste de valeurs vide.";
  }
  if (op === "regex_match" || op === "regex_not_match") {
    try {
      compileConditionRegex(val, icase, false);
    } catch (err) {
      return `Condition Regex invalide : ${err instanceof Error ? err.message : String(err)}`;
    }
  } else if (op === "length_equals" || op === "length_min" || op === "length_max") {
    if (!/^\d+$/.test(val.trim())) return `Longueur invalide : ${val}`;
  } else if (op === "number_equals" || op === "number_not_equals" || op === "greater_than" || op === "greater_or_equal" || op === "less_than" || op === "less_or_equal") {
    if (tryParseCondNumber(val) === null) return `Nombre invalide : ${val}`;
  }
  return null;
}

function applyExtractionConditions(rawValues, ext) {
  const conditions = Array.isArray(ext.conditions) ? ext.conditions : [];
  const dedup = Boolean(ext.deduplicate);
  if (conditions.length === 0 && !dedup) {
    return { ok: true, values: rawValues };
  }

  for (const cond of conditions) {
    if (cond.isConditional) {
      const pErr = validatePredicateSpec(cond.op, cond.value || "", Boolean(cond.ignoreCase));
      if (pErr) return { ok: false, error: pErr };
      for (const br of [cond.thenBranch || { action: "keep" }, cond.elseBranch || { action: "keep" }]) {
        const bErr = validateBranchSpec(br.action || "keep", br.value || "", br.replacement || "", Boolean(br.ignoreCase));
        if (bErr) return { ok: false, error: bErr };
      }
    } else if (TRANSFORM_OPS.has(cond.op)) {
      const bErr = validateBranchSpec(cond.op, cond.value || "", cond.replacement || "", Boolean(cond.ignoreCase));
      if (bErr) return { ok: false, error: bErr };
    } else {
      const pErr = validatePredicateSpec(cond.op, cond.value || "", Boolean(cond.ignoreCase));
      if (pErr) return { ok: false, error: pErr };
    }
  }

  const mode = ext.conditionMode || "all";
  const out = [];
  const seen = dedup ? new Set() : null;

  for (const raw of rawValues) {
    let currItems = [raw];
    if (mode === "all") {
      for (const cond of conditions) {
        currItems = applySingleRuleBatch(cond, currItems);
        if (currItems.length === 0) break;
      }
    } else {
      const filters = conditions.filter((c) => !c.isConditional && !TRANSFORM_OPS.has(c.op));
      const extracts = conditions.filter((c) => !c.isConditional && EXTRACT_OPS.has(c.op));
      const transforms = conditions.filter((c) => c.isConditional || (TRANSFORM_OPS.has(c.op) && !EXTRACT_OPS.has(c.op)));
      if (filters.length > 0 && !filters.some((c) => evalConditionFilter(c, raw))) {
        continue;
      }
      if (extracts.length > 0) {
        if (filters.length === 0 && extracts.length > 1) {
          let matchedAny = false;
          for (const ec of extracts) {
            const cand = applySingleRuleBatch(ec, currItems);
            if (cand.length > 0) {
              currItems = cand;
              matchedAny = true;
              break;
            }
          }
          if (!matchedAny) continue;
        } else {
          for (const ec of extracts) {
            currItems = applySingleRuleBatch(ec, currItems);
            if (currItems.length === 0) break;
          }
          if (currItems.length === 0) continue;
        }
      }
      for (const tc of transforms) {
        currItems = applySingleRuleBatch(tc, currItems);
        if (currItems.length === 0) break;
      }
    }

    for (const valOut of currItems) {
      if (seen) {
        if (seen.has(valOut)) continue;
        seen.add(valOut);
      }
      out.push(valOut);
    }
  }

  if (out.length === 0 && rawValues.length > 0) {
    return { ok: false, error: `Aucun élément ne satisfait les conditions (${rawValues.length} brut(s) filtré(s)).` };
  }
  return { ok: true, values: out };
}

function resolveVariableSourceValues(sourceVarName, localLists, localExtracted, accumulatedLists, mergedVars, listVars) {
  const key = String(sourceVarName || "").trim();
  if (!key) return [];
  if (Object.prototype.hasOwnProperty.call(localLists, key)) return [...localLists[key]];
  if (Object.prototype.hasOwnProperty.call(localExtracted, key)) return [localExtracted[key]];
  if (Object.prototype.hasOwnProperty.call(listVars, key)) return [listVars[key]];
  if (Object.prototype.hasOwnProperty.call(accumulatedLists, key)) return [...accumulatedLists[key]];
  if (Object.prototype.hasOwnProperty.call(mergedVars, key)) return [mergedVars[key]];
  return [];
}

function runExtraction(ext, respBody, respHeaders, sourceValues = null) {
  let rawOutcome;
  if (ext.source === "variable") {
    const srcName = ext.sourceVariable || "";
    if (!srcName) {
      return { ok: false, value: "Nom de variable source vide." };
    }
    if (!Array.isArray(sourceValues) || sourceValues.length === 0) {
      return { ok: false, value: `Variable source « ${srcName} » vide ou introuvable.` };
    }
    if (ext.kind === "full_response") {
      rawOutcome = { ok: true, value: sourceValues.length === 1 ? (sourceValues[0] ?? "") : JSON.stringify(sourceValues) };
    } else if (ext.kind === "jsonpath") {
      rawOutcome = extractJsonPathFromValues(sourceValues, ext.expression);
    } else {
      rawOutcome = { ok: false, value: "Aucune correspondance" };
      for (const item of sourceValues) {
        const res = extractRegex(item ?? "", ext.expression, ext.regexGroup);
        if (res.ok) {
          rawOutcome = res;
          break;
        }
        rawOutcome = res;
      }
    }
  } else if (ext.source === "header") {
    const targetName = ext.headerName.toLowerCase();
    const matching = respHeaders.filter(([k]) => k.toLowerCase() === targetName).map(([, v]) => v);
    if (!matching.length) {
      return { ok: false, value: `En-tête « ${ext.headerName} » absent` };
    }
    rawOutcome = { ok: false, value: "Aucune correspondance" };
    for (const val of matching) {
      const res = extractRegex(val, ext.expression, ext.regexGroup);
      if (res.ok) {
        rawOutcome = res;
        break;
      }
      rawOutcome = res;
    }
  } else if (ext.kind === "full_response") {
    rawOutcome = { ok: true, value: respBody ?? "" };
  } else if (ext.kind === "jsonpath") {
    rawOutcome = extractJsonPath(respBody, ext.expression);
  } else {
    rawOutcome = extractRegex(respBody, ext.expression, ext.regexGroup);
  }

  if (!rawOutcome.ok || !Array.isArray(ext.conditions) || ext.conditions.length === 0) {
    return rawOutcome;
  }
  const filtered = applyExtractionConditions([rawOutcome.value], ext);
  if (filtered.ok && filtered.values.length > 0) {
    const hasAll = ext.conditions.some((c) =>
      (!c.isConditional && c.op === "regex_extract_all") ||
      (c.isConditional && ((c.thenBranch && c.thenBranch.action === "regex_extract_all") || (c.elseBranch && c.elseBranch.action === "regex_extract_all"))));
    return { ok: true, value: hasAll && filtered.values.length > 1 ? filtered.values.join(", ") : filtered.values[0] };
  }
  return { ok: false, value: filtered.error || "La valeur extraite ne satisfait pas les conditions." };
}

function runListExtraction(ext, respBody, respHeaders, sourceValues = null) {
  let rawValues;
  if (ext.source === "variable") {
    const srcName = ext.sourceVariable || "";
    if (!srcName) {
      return { ok: false, error: "Nom de variable source vide." };
    }
    if (!Array.isArray(sourceValues) || sourceValues.length === 0) {
      return { ok: false, error: `Variable source « ${srcName} » vide ou introuvable.` };
    }
    if (ext.kind === "jsonpath") {
      const res = extractJsonPathListFromValues(sourceValues, ext.expression);
      if (!res.ok) return res;
      rawValues = res.values;
    } else {
      const collected = [];
      let lastErr = "Aucune correspondance";
      for (const item of sourceValues) {
        const res = extractRegexList(item ?? "", ext.expression, ext.regexGroup);
        if (res.ok) collected.push(...res.values);
        else lastErr = res.error;
      }
      if (collected.length === 0) {
        return { ok: false, error: lastErr };
      }
      rawValues = collected;
    }
  } else if (ext.source === "header") {
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
    if (collected.length === 0) {
      return { ok: false, error: lastErr };
    }
    rawValues = collected;
  } else if (ext.kind === "jsonpath") {
    const res = extractJsonPathList(respBody, ext.expression);
    if (!res.ok) return res;
    rawValues = res.values;
  } else {
    const res = extractRegexList(respBody, ext.expression, ext.regexGroup);
    if (!res.ok) return res;
    rawValues = res.values;
  }

  return applyExtractionConditions(rawValues, ext);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

let cachedIp = null;

function returnNodeUsesIp(node) {
  if (!node) return false;
  switch (node.type) {
    case "id":
      return node.name === "ip" || node.name === "$ip";
    case "str":
      if (!node.interpolate) return false;
      return /\{\{\s*\$?ip\s*\}\}/.test(node.value);
    case "arr":
      return node.items.some(returnNodeUsesIp);
    case "obj":
      return node.entries.some(([, v]) => returnNodeUsesIp(v));
    case "concat":
      return node.parts.some(returnNodeUsesIp);
    default:
      return false;
  }
}

async function resolveCurrentIp() {
  if (cachedIp !== null) return cachedIp;

  for (const endpoint of ["https://api.ipify.org", "https://icanhazip.com"]) {
    try {
      const resp = await fetch(endpoint, { signal: AbortSignal.timeout(2500) });
      if (resp.ok) {
        const candidate = (await resp.text()).trim();
        if (net.isIP(candidate) !== 0) {
          cachedIp = candidate;
          return cachedIp;
        }
      }
    } catch {
      // Essayer le fournisseur suivant
    }
  }

  try {
    const ifaces = os.networkInterfaces();
    for (const infos of Object.values(ifaces)) {
      if (!infos) continue;
      for (const info of infos) {
        if (info && info.family === "IPv4" && !info.internal && info.address) {
          cachedIp = info.address;
          return cachedIp;
        }
      }
    }
  } catch {
    // Repli sur loopback
  }

  cachedIp = "127.0.0.1";
  return cachedIp;
}

function resolveReturnIdentifier(name, forceScalar, forceList, scalars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs) {
  if (name === "$index") return String(globalIndex);
  if (name === "$guid") return reqGuid;
  if (name === "$timestamp") return reqTs;
  if (name === "$ip") return cachedIp ?? "127.0.0.1";

  const { root, jsonPath } = splitVarPath(name);
  if (jsonPath !== null) {
    if (forceList) {
      if (Object.prototype.hasOwnProperty.call(listVars, name)) return listVars[name];
      if (Object.prototype.hasOwnProperty.call(scalars, root)) {
        const jpList = extractJsonPathList(scalars[root], jsonPath);
        if (jpList.ok && Array.isArray(jpList.values)) return jpList.values;
        throw new Error(`[[${name}]] : ${jpList.error}`);
      }
      if (Object.prototype.hasOwnProperty.call(accumulatedLists, root)) {
        const jpList = extractJsonPathListFromValues(accumulatedLists[root], jsonPath);
        if (jpList.ok && Array.isArray(jpList.values)) return jpList.values;
        throw new Error(`[[${name}]] : ${jpList.error}`);
      }
      throw new Error(`Variable ou liste inconnue : ${root} (pour [[${name}]])`);
    }
    if (Object.prototype.hasOwnProperty.call(scalars, root)) {
      if (!forceScalar) {
        const steps = parseJsonPath(jsonPath);
        if (steps.some((s) => s.isWildcard)) {
          const jpList = extractJsonPathList(scalars[root], jsonPath);
          if (jpList.ok && Array.isArray(jpList.values)) return jpList.values;
          throw new Error(`${name} : ${jpList.error}`);
        }
      }
      const jpRes = extractJsonPath(scalars[root], jsonPath);
      if (jpRes.ok) return jpRes.value;
      throw new Error(`${name} : ${jpRes.value}`);
    }
    if (Object.prototype.hasOwnProperty.call(accumulatedLists, root)) {
      if (!forceScalar) {
        const steps = parseJsonPath(jsonPath);
        if (steps.some((s) => s.isWildcard)) {
          const jpList = extractJsonPathListFromValues(accumulatedLists[root], jsonPath);
          if (jpList.ok && Array.isArray(jpList.values)) return jpList.values;
          throw new Error(`${name} : ${jpList.error}`);
        }
      }
      const jpRes = extractJsonPathFromValues(accumulatedLists[root], jsonPath);
      if (jpRes.ok) return jpRes.value;
      throw new Error(`${name} : ${jpRes.value}`);
    }
    throw new Error(`Variable ou liste inconnue : ${root} (pour ${name})`);
  }

  if (forceList) {
    if (Object.prototype.hasOwnProperty.call(listVars, name)) return listVars[name];
    if (Object.prototype.hasOwnProperty.call(accumulatedLists, name)) return [...accumulatedLists[name]];
    throw new Error(`Liste inconnue ou non définie : [[${name}]]`);
  }
  if (forceScalar) {
    if (Object.prototype.hasOwnProperty.call(scalars, name)) return scalars[name];
    if (name === "ip") return cachedIp ?? "127.0.0.1";
    throw new Error(`Variable inconnue ou non définie : {{${name}}}`);
  }
  if (Object.prototype.hasOwnProperty.call(listVars, name)) return listVars[name];
  if (Object.prototype.hasOwnProperty.call(scalars, name)) return scalars[name];
  if (Object.prototype.hasOwnProperty.call(accumulatedLists, name)) return [...accumulatedLists[name]];
  if (name === "ip") return cachedIp ?? "127.0.0.1";
  throw new Error(`Variable ou liste inconnue : ${name}`);
}

function inlineReturnStr(val) {
  if (val === null || val === undefined) return "";
  if (typeof val === "string") return val;
  if (typeof val === "boolean") return val ? "true" : "false";
  if (typeof val === "object") return JSON.stringify(val);
  return String(val);
}

function evalReturnNode(node, scalars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs) {
  switch (node.type) {
    case "null":
      return null;
    case "bool":
      return node.value;
    case "num": {
      const parsed = Number(node.raw);
      return Number.isFinite(parsed) ? parsed : node.raw;
    }
    case "str": {
      if (!node.interpolate) return node.value;
      return node.value.replace(MARKER_RE, (_, scalarName, listName) => {
        const resolved = listName !== undefined && listName !== null
          ? resolveReturnIdentifier(listName, false, true, scalars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs)
          : resolveReturnIdentifier(scalarName, true, false, scalars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
        return inlineReturnStr(resolved);
      });
    }
    case "id":
      return resolveReturnIdentifier(
        node.name,
        Boolean(node.forceScalar),
        Boolean(node.forceList),
        scalars,
        listVars,
        accumulatedLists,
        globalIndex,
        reqGuid,
        reqTs
      );
    case "arr":
      return node.items.map((item) =>
        evalReturnNode(item, scalars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs)
      );
    case "obj": {
      const obj = {};
      for (const [k, vNode] of node.entries) {
        obj[k] = evalReturnNode(vNode, scalars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
      }
      return obj;
    }
    case "concat":
      return node.parts
        .map((p) => inlineReturnStr(evalReturnNode(p, scalars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs)))
        .join("");
    default:
      throw new Error(`Nœud de retour inconnu : ${node.type}`);
  }
}

function formatReturnOutput(val) {
  if (val === null) return "null";
  if (typeof val === "string") return val;
  if (typeof val === "boolean" || typeof val === "number") return JSON.stringify(val);
  return JSON.stringify(val, null, 2);
}

function printReturnOutput(formatted) {
  if (formatted.includes("\n")) {
    console.log("  -> Retour :");
    console.log(formatted);
  } else {
    console.log(`  -> Retour : ${formatted}`);
  }
}

async function main() {
  const verbose = process.argv.includes("--verbose") || process.argv.includes("-v");
  const accumulatedVars = {};
  const accumulatedLists = {};
  let globalIndex = 0;
  let hasAnyFailure = false;

  for (let stepIdx = 0; stepIdx < STEPS.length; stepIdx++) {
    const step = STEPS[stepIdx];
    if (step.returnError) {
      console.error(`[ERREUR] Étape ${step.rank} (${step.name}) : ${step.returnError}`);
      process.exit(1);
    }
    if (!step.url && !step.isReturnOnly) {
      console.error(`[ERREUR] Étape ${step.rank} (${step.name}) : URL vide.`);
      process.exit(1);
    }

    const scalarCandidates = { ...accumulatedVars, ...step.fixedVariables };
    const { iterations, emptyListName, error: listErr } = buildIterations(step.axes, step.mode, accumulatedLists, scalarCandidates);
    if (listErr !== null) {
      console.error(`[ÉCHEC] Étape ${step.rank} (${step.name}) : ${listErr}`);
      hasAnyFailure = true;
      if (!step.continueOnError) process.exit(1);
      continue;
    }
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

      if (step.isReturnOnly) {
        if (cachedIp === null && returnNodeUsesIp(step.returnNode)) {
          await resolveCurrentIp();
        }
        try {
          const retVal = evalReturnNode(
            step.returnNode,
            mergedVars,
            listVars,
            accumulatedLists,
            globalIndex,
            reqGuid,
            reqTs
          );
          const formattedRet = formatReturnOutput(retVal);
          console.log(`[OK] Étape ${step.rank} (${step.name}) [${iterIdx + 1}/${iterations.length}] RETURN`);
          printReturnOutput(formattedRet);
        } catch (err) {
          console.error(`[ÉCHEC] Étape ${step.rank} (${step.name}) [${iterIdx + 1}/${iterations.length}] RETURN : ${err instanceof Error ? err.message : String(err)}`);
          hasAnyFailure = true;
          if (!step.continueOnError) process.exit(1);
          continue;
        }

        const hasMoreRet = iterIdx + 1 < iterations.length || stepIdx + 1 < STEPS.length;
        if (hasMoreRet && step.delayAfterMs > 0) {
          await sleep(step.delayAfterMs);
        }
        continue;
      }

      let finalUrl;
      let finalHeaders;
      let bodyToSend;
      try {
        const finalBaseUrl = substituteUrl(step.url, mergedVars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
        const queryItems = step.queryParams.map((qp) =>
          qp.axisIndex !== null
            ? [qp.key, axisValues[qp.axisIndex]]
            : [qp.key, substituteSimple(qp.value, mergedVars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs)]
        );
        finalUrl = buildFinalUrl(finalBaseUrl, queryItems);

        finalHeaders = new Headers();
        for (const [k, v] of step.headers) {
          finalHeaders.append(k, substituteSimple(v, mergedVars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs));
        }

        if (step.method !== "GET" && step.method !== "HEAD") {
          if (step.bodyType === "json") {
            bodyToSend = substituteJsonBody(step.body, mergedVars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
          } else if (step.bodyType === "text") {
            bodyToSend = substituteSimple(step.body, mergedVars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs);
          } else if (step.bodyType === "form") {
            const params = new URLSearchParams();
            for (const [fk, fv] of step.formFields) {
              params.append(fk, substituteSimple(fv, mergedVars, listVars, accumulatedLists, globalIndex, reqGuid, reqTs));
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
      const localExtracted = {};
      const localLists = {};

      for (const ext of step.extractions) {
        const srcValues = ext.source === "variable"
          ? resolveVariableSourceValues(ext.sourceVariable || "", localLists, localExtracted, accumulatedLists, mergedVars, listVars)
          : null;
        if (ext.isList) {
          const listRes = runListExtraction(ext, respBody, respHeaders, srcValues);
          if (listRes.ok) {
            accumulatedLists[ext.variable] = listRes.values;
            localLists[ext.variable] = listRes.values;
            if (verbose) console.log(`  -> [[${ext.variable}]] = ${listRes.values.length} élément(s)`);
          } else {
            const fallbackList = ext.defaultValue ? [ext.defaultValue] : [];
            accumulatedLists[ext.variable] = fallbackList;
            localLists[ext.variable] = fallbackList;
            if (ext.required) {
              extractionFailed = true;
              console.error(`  [EXTRACTION] [[${ext.variable}]] : ${listRes.error}`);
            }
          }
          continue;
        }

        const res = runExtraction(ext, respBody, respHeaders, srcValues);
        if (res.ok) {
          accumulatedVars[ext.variable] = res.value;
          localExtracted[ext.variable] = res.value;
          if (verbose) console.log(`  -> ${ext.variable} = ${res.value}`);
        } else {
          accumulatedVars[ext.variable] = ext.defaultValue;
          localExtracted[ext.variable] = ext.defaultValue;
          if (ext.required) {
            extractionFailed = true;
            console.error(`  [EXTRACTION] ${ext.variable} : ${res.value}`);
          }
        }
      }

      let stepFailed = httpFailed || extractionFailed;
      let formattedRet = null;
      let returnErr = null;
      if (!stepFailed && step.returnNode !== null) {
        if (cachedIp === null && returnNodeUsesIp(step.returnNode)) {
          await resolveCurrentIp();
        }
        const returnVars = { ...mergedVars, ...localExtracted };
        try {
          const retVal = evalReturnNode(
            step.returnNode,
            returnVars,
            listVars,
            accumulatedLists,
            globalIndex,
            reqGuid,
            reqTs
          );
          formattedRet = formatReturnOutput(retVal);
        } catch (err) {
          stepFailed = true;
          returnErr = err instanceof Error ? err.message : String(err);
        }
      }

      const statusLabel = stepFailed ? "ÉCHEC" : "OK";
      const codeLabel = netErr !== null ? netErr : `HTTP ${status}`;
      console.log(`[${statusLabel}] Étape ${step.rank} (${step.name}) [${iterIdx + 1}/${iterations.length}] ${step.method} ${finalUrl} -> ${codeLabel} (${elapsedMs} ms)`);

      if (returnErr !== null) {
        console.error(`  [RETOUR] ${returnErr}`);
      } else if (formattedRet !== null) {
        printReturnOutput(formattedRet);
      }

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

    private static string FormatReturnNode(StepReturnNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        switch (node)
        {
            case NullReturnNode:
                return "{ type: \"null\" }";
            case BooleanReturnNode b:
                return $"{{ type: \"bool\", value: {FormatBool(b.Value)} }}";
            case NumberReturnNode n:
                return $"{{ type: \"num\", raw: {EscapeString(n.RawText)} }}";
            case StringLiteralReturnNode s:
                return $"{{ type: \"str\", value: {EscapeString(s.Value)}, interpolate: {FormatBool(s.HasTemplateMarkers)} }}";
            case IdentifierReturnNode id:
                return $"{{ type: \"id\", name: {EscapeString(id.Name)}, forceScalar: {FormatBool(id.ForceScalar)}, forceList: {FormatBool(id.ForceList)} }}";
            case ArrayReturnNode arr:
            {
                var sb = new StringBuilder("{ type: \"arr\", items: [");
                for (var i = 0; i < arr.Items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(FormatReturnNode(arr.Items[i]));
                }
                sb.Append("] }");
                return sb.ToString();
            }
            case ObjectReturnNode obj:
            {
                var sb = new StringBuilder("{ type: \"obj\", entries: [");
                for (var i = 0; i < obj.Entries.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('[');
                    sb.Append(EscapeString(obj.Entries[i].Key));
                    sb.Append(", ");
                    sb.Append(FormatReturnNode(obj.Entries[i].Value));
                    sb.Append(']');
                }
                sb.Append("] }");
                return sb.ToString();
            }
            case ConcatReturnNode concat:
            {
                var sb = new StringBuilder("{ type: \"concat\", parts: [");
                for (var i = 0; i < concat.Parts.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(FormatReturnNode(concat.Parts[i]));
                }
                sb.Append("] }");
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
