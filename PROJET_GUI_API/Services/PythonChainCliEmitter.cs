using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ApiTester.Models;

namespace ApiTester.Services;

internal static class PythonChainCliEmitter
{
    public static string Emit(IReadOnlyList<CliStepSpec> steps)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("#!/usr/bin/env python3");
        sb.AppendLine("# Script CLI généré par ApiTester (aucune dépendance externe requise)");
        sb.AppendLine("# Utilisation : python3 chaine_api.py [--verbose]");
        sb.AppendLine();
        sb.AppendLine("import argparse");
        sb.AppendLine("import gzip");
        sb.AppendLine("import ipaddress");
        sb.AppendLine("import itertools");
        sb.AppendLine("import json");
        sb.AppendLine("import re");
        sb.AppendLine("import socket");
        sb.AppendLine("import sys");
        sb.AppendLine("import time");
        sb.AppendLine("import urllib.error");
        sb.AppendLine("import urllib.parse");
        sb.AppendLine("import urllib.request");
        sb.AppendLine("import uuid");
        sb.AppendLine("import zlib");
        sb.AppendLine();
        sb.AppendLine(@"MARKER_RE = re.compile(r""(?:\{\{\s*(\$(?!\d)\w+|(?!\d)(?!\$)\w+(?:(?:\.[^.\{\}\[\]\r\n\s]+|\[[^\{\}\[\]\r\n]*\])+)?)\s*\}\}|\[\[\s*((?!\d)(?!\$)\w+(?:(?:\.[^.\{\}\[\]\r\n\s]+|\[[^\{\}\[\]\r\n]*\])+)?)\s*\]\])"")");
        sb.AppendLine();

        AppendStepsData(sb, steps);
        AppendRuntimeHelpers(sb);

        return sb.ToString();
    }

    private static void AppendStepsData(StringBuilder sb, IReadOnlyList<CliStepSpec> steps)
    {
        sb.AppendLine("STEPS = [");
        foreach (var step in steps)
        {
            sb.AppendLine("    {");
            sb.AppendLine($"        \"rank\": {step.Rank},");
            sb.AppendLine($"        \"name\": {EscapeString(step.Name)},");
            sb.AppendLine($"        \"method\": {EscapeString(step.Method)},");
            sb.AppendLine($"        \"url\": {EscapeString(step.Url)},");
            sb.AppendLine($"        \"timeout_seconds\": {step.TimeoutSeconds},");
            sb.AppendLine($"        \"delay_after_ms\": {step.DelayAfterMs},");
            sb.AppendLine($"        \"continue_on_error\": {FormatBool(step.ContinueOnError)},");
            sb.AppendLine($"        \"mode\": {EscapeString(step.Mode)},");
            sb.AppendLine($"        \"body_type\": {EscapeString(step.BodyType)},");
            sb.AppendLine($"        \"body\": {EscapeString(step.Body)},");
            sb.AppendLine($"        \"is_return_only\": {FormatBool(step.IsReturnOnly)},");
            sb.AppendLine($"        \"return_node\": {FormatReturnNode(step.ReturnNode)},");
            sb.AppendLine($"        \"return_error\": {(step.ReturnError is null ? "None" : EscapeString(step.ReturnError))},");

            sb.AppendLine("        \"headers\": [");
            foreach (var h in step.Headers)
            {
                sb.AppendLine($"            ({EscapeString(h.Key)}, {EscapeString(h.Value)}),");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        \"query_params\": [");
            foreach (var qp in step.QueryParams)
            {
                var axisIdx = qp.AxisIndex.HasValue
                    ? qp.AxisIndex.Value.ToString(CultureInfo.InvariantCulture)
                    : "None";
                sb.AppendLine($"            {{\"key\": {EscapeString(qp.Key)}, \"value\": {EscapeString(qp.Value)}, \"axis_index\": {axisIdx}}},");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        \"form_fields\": [");
            foreach (var ff in step.FormFields)
            {
                sb.AppendLine($"            ({EscapeString(ff.Key)}, {EscapeString(ff.Value)}),");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        \"fixed_variables\": {");
            foreach (var kv in step.FixedVariables)
            {
                sb.AppendLine($"            {EscapeString(kv.Key)}: {EscapeString(kv.Value)},");
            }
            sb.AppendLine("        },");

            sb.AppendLine("        \"axes\": [");
            foreach (var axis in step.Axes)
            {
                sb.AppendLine("            {");
                sb.AppendLine($"                \"kind\": {EscapeString(axis.Kind)},");
                sb.AppendLine($"                \"name\": {EscapeString(axis.Name)},");
                if (axis.Range is { } r)
                {
                    sb.AppendLine($"                \"range\": {{\"start\": {r.Start}, \"end\": {r.End}, \"step\": {r.Step}, \"base\": {r.Base}, \"width\": {r.Width}, \"uppercase\": {FormatBool(r.UppercaseHex)}, \"prefix\": {FormatBool(r.UsePrefix)}}},");
                }
                else
                {
                    sb.Append("                \"values\": [");
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
                sb.AppendLine("            },");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("        \"extractions\": [");
            foreach (var ext in step.Extractions)
            {
                sb.AppendLine("            {");
                sb.AppendLine($"                \"variable\": {EscapeString(ext.VariableName)},");
                sb.AppendLine($"                \"source\": {EscapeString(ext.Source)},");
                sb.AppendLine($"                \"kind\": {EscapeString(ext.Kind)},");
                sb.AppendLine($"                \"expression\": {EscapeString(ext.Expression)},");
                sb.AppendLine($"                \"regex_group\": {ext.RegexGroup},");
                sb.AppendLine($"                \"header_name\": {EscapeString(ext.HeaderName)},");
                sb.AppendLine($"                \"source_variable\": {EscapeString(ext.SourceVariableName)},");
                sb.AppendLine($"                \"required\": {FormatBool(ext.IsRequired)},");
                sb.AppendLine($"                \"default\": {EscapeString(ext.DefaultValueIfMissing)},");
                sb.AppendLine($"                \"is_list\": {FormatBool(ext.IsList)},");
                sb.AppendLine($"                \"condition_mode\": {EscapeString(ext.ConditionMatchMode)},");
                sb.AppendLine($"                \"deduplicate\": {FormatBool(ext.DeduplicateList)},");
                sb.AppendLine("                \"conditions\": [");
                foreach (var c in ext.Conditions)
                {
                    var thenJson = $"{{\"action\": {EscapeString(c.ThenBranch.Action)}, \"value\": {EscapeString(c.ThenBranch.Value)}, \"replacement\": {EscapeString(c.ThenBranch.Replacement)}, \"regex_group\": {c.ThenBranch.RegexGroup}, \"segment_index\": {c.ThenBranch.SegmentIndex}, \"ignore_case\": {FormatBool(c.ThenBranch.IgnoreCase)}}}";
                    var elseJson = $"{{\"action\": {EscapeString(c.ElseBranch.Action)}, \"value\": {EscapeString(c.ElseBranch.Value)}, \"replacement\": {EscapeString(c.ElseBranch.Replacement)}, \"regex_group\": {c.ElseBranch.RegexGroup}, \"segment_index\": {c.ElseBranch.SegmentIndex}, \"ignore_case\": {FormatBool(c.ElseBranch.IgnoreCase)}}}";
                    sb.AppendLine($"                    {{\"is_conditional\": {FormatBool(c.IsConditional)}, \"op\": {EscapeString(c.Operator)}, \"value\": {EscapeString(c.Value)}, \"replacement\": {EscapeString(c.Replacement)}, \"regex_group\": {c.RegexGroup}, \"segment_index\": {c.SegmentIndex}, \"ignore_case\": {FormatBool(c.IgnoreCase)}, \"then_branch\": {thenJson}, \"else_branch\": {elseJson}}},");
                }
                sb.AppendLine("                ],");
                sb.AppendLine("            },");
            }
            sb.AppendLine("        ],");

            sb.AppendLine("    },");
        }
        sb.AppendLine("]");
        sb.AppendLine();
    }

    private static void AppendRuntimeHelpers(StringBuilder sb)
    {
        sb.AppendLine("""
def format_number(value: int, base_num: int, width: int, uppercase_hex: bool, use_prefix: bool) -> str:
    negative = value < 0
    magnitude = -value if negative else value
    if base_num == 2:
        digits = bin(magnitude)[2:]
        prefix = "0b" if use_prefix else ""
    elif base_num == 8:
        digits = oct(magnitude)[2:]
        prefix = "0o" if use_prefix else ""
    elif base_num == 16:
        digits = format(magnitude, "X" if uppercase_hex else "x")
        prefix = ("0X" if uppercase_hex else "0x") if use_prefix else ""
    else:
        digits = str(magnitude)
        prefix = ""
    if width > 0 and len(digits) < width:
        digits = digits.zfill(width)
    return ("-" if negative else "") + prefix + digits


def make_range(spec: dict) -> list[str]:
    start = int(spec["start"])
    end = int(spec["end"])
    step = int(spec["step"])
    if step == 0 or (start <= end and step < 0) or (start > end and step > 0):
        return []
    values = []
    cur = start
    while (cur <= end) if step > 0 else (cur >= end):
        values.append(format_number(cur, spec["base"], spec["width"], spec["uppercase"], spec["prefix"]))
        cur += step
    return values


_ALLOWED_URI_CHARS = set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:/?#[]@!$&'()*+,;=%")


def split_var_path(expr: str) -> tuple[str, str | None]:
    trimmed = expr.strip()
    if not trimmed or trimmed.startswith("$"):
        return trimmed, None
    dot_idx = trimmed.find(".")
    br_idx = trimmed.find("[")
    candidates = [idx for idx in (dot_idx, br_idx) if idx >= 0]
    if not candidates:
        return trimmed, None
    sep_idx = min(candidates)
    if sep_idx <= 0:
        return trimmed, None
    root = trimmed[:sep_idx].strip()
    raw_path = trimmed[sep_idx + 1:].strip() if trimmed[sep_idx] == "." else trimmed[sep_idx:].strip()
    if not root or not raw_path:
        return trimmed, None
    return root, raw_path


def _is_in_query_value_context(prefix: str) -> bool:
    if "#" in prefix or "?" not in prefix:
        return False
    after_q = prefix.split("?", 1)[1]
    current_pair = after_q.rsplit("&", 1)[-1]
    return "=" in current_pair


def _escape_url_preserving_structure(value: str) -> str:
    if not value:
        return ""
    out: list[str] = []
    for ch in value:
        if ch in _ALLOWED_URI_CHARS:
            out.append(ch)
        else:
            for b in ch.encode("utf-8"):
                out.append(f"%{b:02X}")
    return "".join(out)


def build_iterations(axes: list[dict], mode: str, accumulated_lists: dict[str, list[str]], scalar_vars: dict[str, str]) -> tuple[list[tuple[str, ...]], str | None, str | None]:
    if not axes:
        return [()], None, None
    axis_lists: list[list[str]] = []
    for a in axes:
        if a["kind"] == "list":
            root, jp = split_var_path(a["name"])
            if jp is not None:
                if root in scalar_vars:
                    ok, res = extract_json_path_list(scalar_vars[root], jp)
                elif root in accumulated_lists:
                    ok, res = extract_json_path_list_from_values(accumulated_lists[root], jp)
                else:
                    return [], None, f"Variable ou liste « {root} » introuvable pour [[{a['name']}]]."
                if ok and isinstance(res, list) and res:
                    lst = res
                elif isinstance(res, str) and "est vide" in res.lower():
                    return [], a["name"], None
                else:
                    return [], None, f"[[{a['name']}]] : {res}"
            else:
                lst = list(accumulated_lists.get(a["name"], []))
                if not lst:
                    return [], a["name"], None
            axis_lists.append(lst)
        elif "range" in a:
            lst = make_range(a["range"])
            if not lst:
                return [], None, None
            axis_lists.append(lst)
        else:
            lst = list(a["values"])
            if not lst:
                return [], None, None
            axis_lists.append(lst)
    if mode == "parallel":
        return list(zip(*axis_lists)), None, None
    return list(itertools.product(*axis_lists)), None, None


def resolve_marker(match: re.Match[str], variables: dict[str, str], list_vars: dict[str, str], accumulated_lists: dict[str, list[str]] | None, global_index: int, req_guid: str, req_ts: str) -> str:
    list_name = match.group(2)
    if list_name is not None:
        if list_name in list_vars:
            return list_vars[list_name]
        root, jp = split_var_path(list_name)
        if jp is not None:
            if root in variables:
                ok, res = extract_json_path_list(variables[root], jp)
                if ok and isinstance(res, list) and res:
                    return res[0]
                raise KeyError(f"[[{list_name}]] : {res}")
            if accumulated_lists is not None and root in accumulated_lists:
                ok, res = extract_json_path_list_from_values(accumulated_lists[root], jp)
                if ok and isinstance(res, list) and res:
                    return res[0]
                raise KeyError(f"[[{list_name}]] : {res}")
        raise KeyError(f"Liste inconnue ou non définie : [[{list_name}]]")
    name = match.group(1)
    if name in variables:
        return variables[name]
    if name == "$index":
        return str(global_index)
    if name == "$guid":
        return req_guid
    if name == "$timestamp":
        return req_ts
    root, jp = split_var_path(name)
    if jp is not None:
        if root in variables:
            ok, res = extract_json_path(variables[root], jp)
            if not ok:
                raise KeyError(f"{{{{{name}}}}} : {res}")
            return res
        if accumulated_lists is not None and root in accumulated_lists:
            ok, res = extract_json_path_from_values(accumulated_lists[root], jp)
            if not ok:
                raise KeyError(f"{{{{{name}}}}} : {res}")
            return res
        raise KeyError(f"Variable inconnue ou non définie : {{{{{root}}}}} (pour {{{{{name}}}}})")
    raise KeyError(f"Variable inconnue ou non définie : {{{{{name}}}}}")


def substitute_url(text: str, variables: dict[str, str], list_vars: dict[str, str], accumulated_lists: dict[str, list[str]] | None, global_index: int, req_guid: str, req_ts: str) -> str:
    if not text:
        return ""
    matches = list(MARKER_RE.finditer(text))
    if not matches:
        return text
    out: list[str] = []
    last_idx = 0
    for match in matches:
        out.append(text[last_idx:match.start()])
        prefix = "".join(out)
        val = resolve_marker(match, variables, list_vars, accumulated_lists, global_index, req_guid, req_ts)
        if _is_in_query_value_context(prefix):
            out.append(urllib.parse.quote(val, safe=""))
        else:
            out.append(_escape_url_preserving_structure(val.strip() if not prefix.strip() else val))
        last_idx = match.end()
    out.append(text[last_idx:])
    return "".join(out)


def substitute_simple(text: str, variables: dict[str, str], list_vars: dict[str, str], accumulated_lists: dict[str, list[str]] | None, global_index: int, req_guid: str, req_ts: str, url_encode: bool = False) -> str:
    if not text:
        return ""

    def repl(match: re.Match[str]) -> str:
        val = resolve_marker(match, variables, list_vars, accumulated_lists, global_index, req_guid, req_ts)
        return urllib.parse.quote(val, safe="") if url_encode else val

    return MARKER_RE.sub(repl, text)


def substitute_json_body(text: str, variables: dict[str, str], list_vars: dict[str, str], accumulated_lists: dict[str, list[str]] | None, global_index: int, req_guid: str, req_ts: str) -> str:
    if not text:
        return ""
    out: list[str] = []
    last_scanned = 0
    in_string = False
    escaped = False

    for match in MARKER_RE.finditer(text):
        for i in range(last_scanned, match.start()):
            ch = text[i]
            if in_string:
                if escaped:
                    escaped = False
                elif ch == "\\":
                    escaped = True
                elif ch == "\"":
                    in_string = False
            elif ch == "\"":
                in_string = True
                escaped = False

        out.append(text[last_scanned:match.start()])
        val = resolve_marker(match, variables, list_vars, accumulated_lists, global_index, req_guid, req_ts)
        out.append(json.dumps(val, ensure_ascii=False)[1:-1] if in_string else val)
        last_scanned = match.end()

    out.append(text[last_scanned:])
    return "".join(out)


def build_final_url(raw_url: str, query_items: list[tuple[str, str]]) -> str:
    trimmed = raw_url.strip()
    if not query_items:
        return trimmed
    encoded_parts = [f"{urllib.parse.quote(k, safe='')}={urllib.parse.quote(v, safe='')}" for k, v in query_items]
    parsed = urllib.parse.urlsplit(trimmed)
    if parsed.scheme and parsed.netloc:
        combined_query = f"{parsed.query}&{'&'.join(encoded_parts)}" if parsed.query else "&".join(encoded_parts)
        return urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, parsed.path, combined_query, parsed.fragment))
    sep = "&" if "?" in trimmed else "?"
    return f"{trimmed}{sep}{'&'.join(encoded_parts)}"


def decode_response_bytes(raw_bytes: bytes, headers: list[tuple[str, str]]) -> str:
    encoding = ""
    for k, v in headers:
        if k.lower() == "content-encoding":
            encoding = v.lower()
    if "gzip" in encoding:
        try:
            raw_bytes = gzip.decompress(raw_bytes)
        except OSError:
            pass
    elif "deflate" in encoding:
        try:
            raw_bytes = zlib.decompress(raw_bytes)
        except zlib.error:
            pass
    return raw_bytes.decode("utf-8", errors="replace")


def parse_json_path(raw_path: str) -> list[tuple[str | None, int | None, bool]]:
    path = raw_path.strip()
    if path == "$":
        return []
    if path.startswith("$."):
        path = path[2:]
    elif path.startswith("$"):
        path = path[1:]
    if not path.strip():
        raise ValueError("Chemin JSONPath vide.")
    steps: list[tuple[str | None, int | None, bool]] = []
    pos = 0
    length = len(path)
    while pos < length:
        if path[pos] == ".":
            if pos == 0 or pos + 1 >= length or path[pos + 1] == ".":
                raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
            pos += 1
            continue
        if path[pos] == "[":
            while pos < length and path[pos] == "[":
                cursor = pos + 1
                while cursor < length and path[cursor].isspace():
                    cursor += 1
                if cursor < length and path[cursor] in ("\"", "'"):
                    q = path[cursor]
                    cursor += 1
                    key_chars: list[str] = []
                    closed_quote = False
                    while cursor < length:
                        ch = path[cursor]
                        cursor += 1
                        if ch == "\\" and cursor < length:
                            esc = path[cursor]
                            cursor += 1
                            key_chars.append({"\\": "\\", "\"": "\"", "'": "'", "n": "\n", "r": "\r", "t": "\t"}.get(esc, esc))
                            continue
                        if ch == q:
                            closed_quote = True
                            break
                        key_chars.append(ch)
                    if not closed_quote:
                        raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
                    while cursor < length and path[cursor].isspace():
                        cursor += 1
                    if cursor >= length or path[cursor] != "]":
                        raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
                    steps.append(("".join(key_chars), None, False))
                    pos = cursor + 1
                    continue
                close = path.find("]", pos + 1)
                if close < 0:
                    raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
                idx_str = path[pos + 1:close].strip()
                if idx_str in ("*", "", "++") or re.fullmatch(r"(?:[A-Za-z_]\w*\+\+|\+\+[A-Za-z_]\w*)", idx_str):
                    steps.append((None, None, True))
                else:
                    steps.append((None, int(idx_str), False))
                pos = close + 1
            if pos < length and path[pos] != ".":
                raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
            continue
        seg_start = pos
        while pos < length and path[pos] not in (".", "[", "]"):
            pos += 1
        if pos < length and path[pos] == "]":
            raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
        prop = path[seg_start:pos]
        if not prop:
            raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
        steps.append((prop, None, False))
    return steps


def format_json_value(current) -> str:
    if isinstance(current, str):
        return current
    if isinstance(current, bool):
        return "true" if current else "false"
    if current is None:
        return "null"
    if isinstance(current, (dict, list)):
        return json.dumps(current, ensure_ascii=False)
    return str(current)


def _unwrap_json_string(val):
    if not isinstance(val, str):
        return val
    trimmed = val.lstrip("\ufeff \t\r\n")
    if not trimmed.startswith(("{", "[")):
        return val
    try:
        return json.loads(trimmed)
    except Exception:
        return val


def _get_max_contiguous_prop_idx(steps: list[tuple[str | None, int | None, bool]], start_idx: int) -> int:
    end = start_idx
    while end + 1 < len(steps):
        nxt = steps[end + 1]
        if nxt[0] is None or nxt[1] is not None or nxt[2]:
            break
        curr = steps[end]
        if curr[1] is not None or curr[2]:
            break
        end += 1
    return end


def _looks_like_json_array(raw: str | None) -> bool:
    if not raw:
        return False
    return raw.lstrip("\ufeff \t\r\n").startswith("[")


def _extract_json_path_steps(body: str, steps: list[tuple[str | None, int | None, bool]], start_idx: int = 0) -> tuple[bool, str]:
    try:
        if not body or not body.strip():
            return False, "Le corps est vide (JSON attendu)."
        current = json.loads(body.lstrip("\ufeff"))
        i = start_idx
        while i < len(steps):
            prop, arr_idx, _ = steps[i]
            if prop is not None:
                current = _unwrap_json_string(current)
                if not isinstance(current, dict):
                    return False, f"Propriété JSON introuvable : {prop}"
                if prop in current:
                    current = current[prop]
                else:
                    matched_dotted = False
                    max_end = _get_max_contiguous_prop_idx(steps, i)
                    for end_idx in range(max_end, i, -1):
                        joined = ".".join(steps[k][0] or "" for k in range(i, end_idx + 1))
                        if joined in current:
                            current = current[joined]
                            i = end_idx
                            matched_dotted = True
                            break
                    if not matched_dotted:
                        return False, f"Propriété JSON introuvable : {prop}"
            if arr_idx is not None:
                current = _unwrap_json_string(current)
                if not isinstance(current, list):
                    return False, f"Index [{arr_idx}] inapplicable"
                resolved = len(current) + arr_idx if arr_idx < 0 else arr_idx
                if resolved < 0 or resolved >= len(current):
                    return False, f"Index [{arr_idx}] hors limites"
                current = current[resolved]
            i += 1
        return True, format_json_value(current)
    except Exception as exc:
        return False, str(exc)


def extract_json_path(body: str, raw_path: str) -> tuple[bool, str]:
    try:
        steps = parse_json_path(raw_path)
        if any(is_wild for _, _, is_wild in steps):
            return False, "Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]]."
        return _extract_json_path_steps(body, steps, 0)
    except Exception as exc:
        return False, str(exc)


def _extract_json_path_list_steps(body: str, steps: list[tuple[str | None, int | None, bool]], start_idx: int = 0) -> tuple[bool, list[str] | str]:
    try:
        if not body or not body.strip():
            return False, "Le corps est vide (JSON attendu)."
        current_elements = [json.loads(body.lstrip("\ufeff"))]
        has_wildcard = False
        i = start_idx
        while i < len(steps):
            prop, arr_idx, is_wildcard = steps[i]
            if prop is not None:
                exact_matches = []
                all_exact = True
                for raw_el in current_elements:
                    el = _unwrap_json_string(raw_el)
                    if not isinstance(el, dict) or prop not in el:
                        all_exact = False
                        continue
                    exact_matches.append(el[prop])

                if exact_matches and (all_exact or has_wildcard):
                    current_elements = exact_matches
                else:
                    matched_joined = False
                    max_end = _get_max_contiguous_prop_idx(steps, i)
                    for end_idx in range(max_end, i, -1):
                        joined = ".".join(steps[k][0] or "" for k in range(i, end_idx + 1))
                        j_matches = []
                        all_j = True
                        for raw_el in current_elements:
                            el = _unwrap_json_string(raw_el)
                            if not isinstance(el, dict) or joined not in el:
                                all_j = False
                                continue
                            j_matches.append(el[joined])
                        if j_matches and (all_j or has_wildcard):
                            current_elements = j_matches
                            i = end_idx
                            prop, arr_idx, is_wildcard = steps[i]
                            matched_joined = True
                            break
                    if not matched_joined:
                        if exact_matches:
                            current_elements = exact_matches
                        else:
                            return False, f"Propriété JSON introuvable : {prop}"

            if is_wildcard:
                was_wildcard = has_wildcard
                has_wildcard = True
                next_elements = []
                for raw_el in current_elements:
                    el = _unwrap_json_string(raw_el)
                    if not isinstance(el, list):
                        if not was_wildcard:
                            return False, "Le sélecteur [*] est inapplicable sur cet élément"
                        continue
                    next_elements.extend(el)
                if not next_elements:
                    return False, "Le tableau JSON ciblé est vide."
                current_elements = next_elements
            elif arr_idx is not None:
                next_elements = []
                for raw_el in current_elements:
                    el = _unwrap_json_string(raw_el)
                    if not isinstance(el, list):
                        if not has_wildcard:
                            return False, f"Index [{arr_idx}] inapplicable"
                        continue
                    resolved = len(el) + arr_idx if arr_idx < 0 else arr_idx
                    if resolved < 0 or resolved >= len(el):
                        if not has_wildcard:
                            return False, f"Index [{arr_idx}] hors limites"
                        continue
                    next_elements.append(el[resolved])
                if not next_elements:
                    return False, f"Aucune valeur trouvée pour l'index [{arr_idx}]"
                current_elements = next_elements
            i += 1

        if not has_wildcard and len(current_elements) == 1 and isinstance(current_elements[0], list):
            if not current_elements[0]:
                return False, "Le tableau JSON ciblé est vide."
            return True, [format_json_value(item) for item in current_elements[0]]

        if not current_elements:
            return False, "Aucune valeur extraite pour l'expression JSONPath."
        return True, [format_json_value(el) for el in current_elements]
    except Exception as exc:
        return False, str(exc)


def extract_json_path_list(body: str, raw_path: str) -> tuple[bool, list[str] | str]:
    try:
        steps = parse_json_path(raw_path)
        return _extract_json_path_list_steps(body, steps, 0)
    except Exception as exc:
        return False, str(exc)


def extract_json_path_from_values(source_values: list[str], raw_path: str) -> tuple[bool, str]:
    if not source_values:
        return False, "La liste source est vide."
    try:
        steps = parse_json_path(raw_path)
    except ValueError as exc:
        return False, str(exc)
    if any(w for _, _, w in steps):
        return False, "Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]]."
    if not steps:
        return True, (source_values[0] if len(source_values) == 1 else json.dumps(source_values, ensure_ascii=False))
    if steps[0][0] is None and steps[0][1] is not None and (len(source_values) > 1 or not _looks_like_json_array(source_values[0])):
        idx = steps[0][1]
        resolved = len(source_values) + idx if idx < 0 else idx
        if resolved < 0 or resolved >= len(source_values):
            return False, f"Index [{idx}] hors limites (taille : {len(source_values)})"
        chosen = source_values[resolved]
        if len(steps) == 1:
            return True, chosen
        return _extract_json_path_steps(chosen, steps, 1)
    last_err = "Aucune correspondance JSONPath dans la variable source."
    for item in source_values:
        ok, res = _extract_json_path_steps(item, steps, 0)
        if ok:
            return True, res
        last_err = res
    return False, last_err


def extract_json_path_list_from_values(source_values: list[str], raw_path: str) -> tuple[bool, list[str] | str]:
    if not source_values:
        return False, "La liste source est vide."
    try:
        steps = parse_json_path(raw_path)
    except ValueError as exc:
        return False, str(exc)
    if not steps:
        return True, list(source_values)
    if steps[0][0] is None and (steps[0][2] or steps[0][1] is not None) and (len(source_values) > 1 or not _looks_like_json_array(source_values[0])):
        if steps[0][2]:
            if len(steps) == 1:
                return True, list(source_values)
            collected: list[str] = []
            last_err: str | None = None
            for item in source_values:
                ok, res = _extract_json_path_list_steps(item, steps, 1)
                if ok and isinstance(res, list):
                    collected.extend(res)
                elif isinstance(res, str):
                    last_err = res
            return (True, collected) if collected else (False, last_err or "Aucune valeur extraite pour l'expression JSONPath.")
        idx = steps[0][1]
        assert idx is not None
        resolved = len(source_values) + idx if idx < 0 else idx
        if resolved < 0 or resolved >= len(source_values):
            return False, f"Index [{idx}] hors limites (taille : {len(source_values)})"
        chosen = source_values[resolved]
        if len(steps) == 1:
            return True, [chosen]
        return _extract_json_path_list_steps(chosen, steps, 1)
    aggregated: list[str] = []
    last_err_str: str | None = None
    for item in source_values:
        ok, res = _extract_json_path_list_steps(item, steps, 0)
        if ok and isinstance(res, list):
            aggregated.extend(res)
        elif isinstance(res, str):
            last_err_str = res
    return (True, aggregated) if aggregated else (False, last_err_str or "Aucune valeur extraite pour l'expression JSONPath.")


def extract_regex(text: str, pattern: str, group_idx: int) -> tuple[bool, str]:
    try:
        match = re.search(pattern, text)
    except re.error as exc:
        return False, f"Regex invalide : {exc}"
    if not match:
        return False, "Aucune correspondance regex"
    try:
        val = match.group(group_idx)
    except IndexError:
        return False, f"Groupe regex #{group_idx} inexistant"
    if val is None:
        return False, f"Le groupe regex #{group_idx} n'a rien capturé"
    return True, val


def extract_regex_list(text: str, pattern: str, group_idx: int) -> tuple[bool, list[str] | str]:
    try:
        compiled = re.compile(pattern)
    except re.error as exc:
        return False, f"Regex invalide : {exc}"
    values: list[str] = []
    for match in compiled.finditer(text):
        try:
            val = match.group(group_idx)
        except IndexError:
            return False, f"Groupe regex #{group_idx} inexistant"
        if val is not None:
            values.append(val)
    if not values:
        return False, "Aucune correspondance regex"
    return True, values


def _compile_condition_regex(pattern: str, ignore_case: bool):
    flags = re.IGNORECASE if ignore_case else 0
    return re.compile(pattern, flags)


def _parse_condition_list(raw: str) -> list[str]:
    return [part.strip() for part in re.split(r"[,;|\r\n]+", raw) if part.strip()]


def _try_parse_cond_number(raw: str) -> float | None:
    s = raw.strip().replace(",", ".")
    if not s:
        return None
    try:
        num = float(s)
        if num != num or num in (float("inf"), float("-inf")):
            return None
        return num
    except ValueError:
        return None


def _eval_condition_filter(cond: dict, value: str) -> bool:
    op = cond["op"]
    target = cond["value"]
    icase = cond.get("ignore_case", False)
    v_cmp = value.lower() if icase else value
    t_cmp = target.lower() if icase else target

    if op == "contains":
        return t_cmp in v_cmp
    if op == "not_contains":
        return t_cmp not in v_cmp
    if op == "starts_with":
        return v_cmp.startswith(t_cmp)
    if op == "not_starts_with":
        return not v_cmp.startswith(t_cmp)
    if op == "ends_with":
        return v_cmp.endswith(t_cmp)
    if op == "not_ends_with":
        return not v_cmp.endswith(t_cmp)
    if op == "equals":
        return v_cmp == t_cmp
    if op == "not_equals":
        return v_cmp != t_cmp
    if op == "in_list":
        items = _parse_condition_list(target)
        return any((v_cmp == (it.lower() if icase else it)) for it in items)
    if op == "not_in_list":
        items = _parse_condition_list(target)
        return not any((v_cmp == (it.lower() if icase else it)) for it in items)
    if op == "regex_match":
        return _compile_condition_regex(target, icase).search(value) is not None
    if op == "regex_not_match":
        return _compile_condition_regex(target, icase).search(value) is None
    if op == "is_empty":
        return not bool(value.strip())
    if op == "not_empty":
        return bool(value.strip())
    if op == "length_equals":
        return len(value) == int(target.strip())
    if op == "length_min":
        return len(value) >= int(target.strip())
    if op == "length_max":
        return len(value) <= int(target.strip())
    if op in ("number_equals", "number_not_equals", "greater_than", "greater_or_equal", "less_than", "less_or_equal"):
        v_num = _try_parse_cond_number(value)
        t_num = _try_parse_cond_number(target)
        if v_num is None or t_num is None:
            return False
        if op == "number_equals":
            return v_num == t_num
        if op == "number_not_equals":
            return v_num != t_num
        if op == "greater_than":
            return v_num > t_num
        if op == "greater_or_equal":
            return v_num >= t_num
        if op == "less_than":
            return v_num < t_num
        return v_num <= t_num
    return True


_REPL_TOKEN_RE = re.compile(r"\$\$|\$&|\$\{(\d+)\}|\$(\d+)|\\(\d+)")


def _apply_condition_extract_parts(pattern: str, regex_group: int, ignore_case: bool, value: str) -> tuple[bool, str]:
    match = _compile_condition_regex(pattern, ignore_case).search(value)
    if not match:
        return False, ""
    try:
        captured = match.group(regex_group)
    except IndexError:
        return False, ""
    return (True, captured) if captured is not None else (False, "")


def _apply_condition_replace_parts(pattern: str, template: str, ignore_case: bool, value: str) -> str:
    regex = _compile_condition_regex(pattern, ignore_case)

    def _repl(match: re.Match[str]) -> str:
        def _tok(tok: re.Match[str]) -> str:
            s = tok.group(0)
            if s == "$$":
                return "$"
            if s == "$&":
                return match.group(0)
            idx = int(tok.group(1) or tok.group(2) or tok.group(3))
            try:
                grp = match.group(idx)
                return grp if grp is not None else ""
            except IndexError:
                return s
        return _REPL_TOKEN_RE.sub(_tok, template)

    return regex.sub(_repl, value)


def _apply_condition_replace_text_parts(target: str, rep: str, ignore_case: bool, value: str) -> str:
    if not target:
        return value
    if ignore_case:
        return re.compile(re.escape(target), re.IGNORECASE).sub(lambda _: rep, value)
    return value.replace(target, rep)


def _apply_condition_between_parts(left: str, right: str, ignore_case: bool, value: str) -> tuple[bool, str]:
    hay = value.lower() if ignore_case else value
    start_idx = 0
    if left:
        left_cmp = left.lower() if ignore_case else left
        pos = hay.find(left_cmp)
        if pos < 0:
            return False, ""
        start_idx = pos + len(left)
    if right:
        right_cmp = right.lower() if ignore_case else right
        end_pos = hay.find(right_cmp, start_idx)
        if end_pos < 0:
            return False, ""
        return True, value[start_idx:end_pos]
    return True, value[start_idx:]


def _apply_condition_split_part_parts(sep: str, seg_idx: int, ignore_case: bool, value: str) -> tuple[bool, str]:
    if ignore_case:
        parts = re.split(re.escape(sep), value, flags=re.IGNORECASE)
    else:
        parts = value.split(sep)
    resolved = len(parts) + seg_idx if seg_idx < 0 else seg_idx
    if resolved < 0 or resolved >= len(parts):
        return False, ""
    return True, parts[resolved]


def _execute_action_branch(action: str, val: str, rep: str, grp: int, seg_idx: int, icase: bool, curr: str, out: list[str]) -> None:
    if action == "keep":
        out.append(curr)
    elif action == "exclude":
        return
    elif action == "set_value":
        out.append(val)
    elif action == "prepend_text":
        out.append(val + curr)
    elif action == "append_text":
        out.append(curr + val)
    elif action == "regex_extract":
        ok_ex, val_ex = _apply_condition_extract_parts(val, grp, icase, curr)
        if ok_ex:
            out.append(val_ex)
    elif action == "regex_extract_all":
        regex = _compile_condition_regex(val, icase)
        for m in regex.finditer(curr):
            try:
                captured = m.group(grp)
            except IndexError:
                captured = None
            if captured is not None:
                out.append(captured)
    elif action == "regex_replace":
        out.append(_apply_condition_replace_parts(val, rep, icase, curr))
    elif action == "replace_text":
        out.append(_apply_condition_replace_text_parts(val, rep, icase, curr))
    elif action == "between":
        ok_bt, val_bt = _apply_condition_between_parts(val, rep, icase, curr)
        if ok_bt:
            out.append(val_bt)
    elif action == "split_part":
        ok_sp, val_sp = _apply_condition_split_part_parts(val, seg_idx, icase, curr)
        if ok_sp:
            out.append(val_sp)
    elif action == "trim":
        out.append(curr.strip(val) if val else curr.strip())
    elif action == "to_lower":
        out.append(curr.lower())
    elif action == "to_upper":
        out.append(curr.upper())


def _apply_single_rule_batch(cond: dict, inputs: list[str]) -> list[str]:
    out: list[str] = []
    if cond.get("is_conditional"):
        then_b = cond.get("then_branch") or {"action": "keep"}
        else_b = cond.get("else_branch") or {"action": "exclude"}
        for curr in inputs:
            branch = then_b if _eval_condition_filter(cond, curr) else else_b
            _execute_action_branch(
                branch.get("action", "keep"),
                branch.get("value", ""),
                branch.get("replacement", ""),
                int(branch.get("regex_group", 1)),
                int(branch.get("segment_index", 0)),
                bool(branch.get("ignore_case", False)),
                curr,
                out,
            )
        return out

    op = cond["op"]
    for curr in inputs:
        if op in _TRANSFORM_OPS:
            _execute_action_branch(
                op,
                cond.get("value", ""),
                cond.get("replacement", ""),
                int(cond.get("regex_group", 1)),
                int(cond.get("segment_index", 0)),
                bool(cond.get("ignore_case", False)),
                curr,
                out,
            )
        elif _eval_condition_filter(cond, curr):
            out.append(curr)
    return out


_TRANSFORM_OPS = {"regex_extract", "regex_extract_all", "regex_replace", "replace_text", "prepend_text", "append_text", "set_value", "between", "split_part", "trim", "to_lower", "to_upper"}
_EXTRACT_OPS = {"regex_extract", "regex_extract_all"}
_NO_VAL_PREDICATE_OPS = {"is_empty", "not_empty"}
_NO_VAL_ACTION_OPS = {"keep", "exclude", "set_value", "trim", "to_lower", "to_upper"}


def _validate_branch_spec(action: str, val: str, rep: str, grp: int, icase: bool) -> str | None:
    if action == "between":
        if not val and not rep:
            return "Condition entre délimiteurs vide."
    elif action not in _NO_VAL_ACTION_OPS and not val:
        return "Action ou transformation vide."
    if action in ("regex_extract", "regex_extract_all", "regex_replace"):
        compiled_re = _compile_condition_regex(val, icase)
        if action in _EXTRACT_OPS and grp > compiled_re.groups:
            return f"Groupe #{grp} absent du motif Regex."
    return None


def _validate_predicate_spec(op: str, val: str, icase: bool) -> str | None:
    if op not in _NO_VAL_PREDICATE_OPS and not val:
        return "Condition d'extraction vide."
    if op in ("in_list", "not_in_list") and not _parse_condition_list(val):
        return "Liste de valeurs vide."
    if op in ("regex_match", "regex_not_match"):
        _compile_condition_regex(val, icase)
    elif op in ("length_equals", "length_min", "length_max"):
        if int(val.strip()) < 0:
            return f"Longueur invalide : {val}"
    elif op in ("number_equals", "number_not_equals", "greater_than", "greater_or_equal", "less_than", "less_or_equal"):
        if _try_parse_cond_number(val) is None:
            return f"Nombre invalide : {val}"
    return None


def apply_extraction_conditions(raw_values: list[str], ext: dict) -> tuple[bool, list[str] | str]:
    conditions = ext.get("conditions") or []
    dedup = bool(ext.get("deduplicate"))
    if not conditions and not dedup:
        return True, raw_values

    mode = ext.get("condition_mode", "all")
    try:
        for cond in conditions:
            if cond.get("is_conditional"):
                err = _validate_predicate_spec(cond["op"], cond["value"], bool(cond.get("ignore_case", False)))
                if err:
                    return False, err
                for branch_key in ("then_branch", "else_branch"):
                    br = cond.get(branch_key) or {"action": "keep"}
                    err = _validate_branch_spec(
                        br.get("action", "keep"),
                        br.get("value", ""),
                        br.get("replacement", ""),
                        int(br.get("regex_group", 1)),
                        bool(br.get("ignore_case", False)),
                    )
                    if err:
                        return False, err
            elif cond["op"] in _TRANSFORM_OPS:
                err = _validate_branch_spec(
                    cond["op"],
                    cond.get("value", ""),
                    cond.get("replacement", ""),
                    int(cond.get("regex_group", 1)),
                    bool(cond.get("ignore_case", False)),
                )
                if err:
                    return False, err
            else:
                err = _validate_predicate_spec(cond["op"], cond["value"], bool(cond.get("ignore_case", False)))
                if err:
                    return False, err
    except (re.error, ValueError) as exc:
        return False, f"Condition invalide : {exc}"

    out: list[str] = []
    seen: set[str] = set()
    for raw in raw_values:
        curr_items = [raw]
        if mode == "all":
            for cond in conditions:
                curr_items = _apply_single_rule_batch(cond, curr_items)
                if not curr_items:
                    break
        else:
            filters = [c for c in conditions if not c.get("is_conditional") and c["op"] not in _TRANSFORM_OPS]
            extracts = [c for c in conditions if not c.get("is_conditional") and c["op"] in _EXTRACT_OPS]
            transforms = [c for c in conditions if c.get("is_conditional") or (c["op"] in _TRANSFORM_OPS and c["op"] not in _EXTRACT_OPS)]
            if filters and not any(_eval_condition_filter(c, raw) for c in filters):
                continue
            if extracts:
                if not filters and len(extracts) > 1:
                    matched_any = False
                    for ec in extracts:
                        cand = _apply_single_rule_batch(ec, curr_items)
                        if cand:
                            curr_items = cand
                            matched_any = True
                            break
                    if not matched_any:
                        continue
                else:
                    for ec in extracts:
                        curr_items = _apply_single_rule_batch(ec, curr_items)
                        if not curr_items:
                            break
                    if not curr_items:
                        continue
            for tc in transforms:
                curr_items = _apply_single_rule_batch(tc, curr_items)
                if not curr_items:
                    break

        for val_out in curr_items:
            if dedup:
                if val_out in seen:
                    continue
                seen.add(val_out)
            out.append(val_out)

    if not out and raw_values:
        return False, f"Aucun élément ne satisfait les conditions ({len(raw_values)} brut(s) filtré(s))."
    return True, out


def resolve_variable_source_values(
    source_var_name: str,
    local_lists: dict[str, list[str]],
    local_extracted: dict[str, str],
    accumulated_lists: dict[str, list[str]],
    merged_vars: dict[str, str],
    list_vars: dict[str, str],
) -> list[str]:
    key = (source_var_name or "").strip()
    if not key:
        return []
    if key in local_lists:
        return list(local_lists[key])
    if key in local_extracted:
        return [local_extracted[key]]
    if key in list_vars:
        return [list_vars[key]]
    if key in accumulated_lists:
        return list(accumulated_lists[key])
    if key in merged_vars:
        return [merged_vars[key]]
    return []


def run_extraction(ext: dict, resp_body: str, resp_headers: list[tuple[str, str]], source_values: list[str] | None = None) -> tuple[bool, str]:
    if ext["source"] == "variable":
        src_name = ext.get("source_variable", "")
        if not src_name:
            return False, "Nom de variable source vide."
        if not source_values:
            return False, f"Variable source « {src_name} » vide ou introuvable."
        if ext["kind"] == "full_response":
            res = source_values[0] if len(source_values) == 1 else json.dumps(source_values, ensure_ascii=False)
        elif ext["kind"] == "jsonpath":
            ok, res = extract_json_path_from_values(source_values, ext["expression"])
            if not ok:
                return False, res
        else:
            ok = False
            res = "Aucune correspondance"
            for item in source_values:
                ok, res = extract_regex(item, ext["expression"], ext["regex_group"])
                if ok:
                    break
            if not ok:
                return False, res
    elif ext["source"] == "header":
        target_name = ext["header_name"].lower()
        matching = [v for k, v in resp_headers if k.lower() == target_name]
        if not matching:
            return False, f"En-tête « {ext['header_name']} » absent"
        ok = False
        res = "Aucune correspondance"
        for header_val in matching:
            ok, res = extract_regex(header_val, ext["expression"], ext["regex_group"])
            if ok:
                break
        if not ok:
            return False, res
    elif ext["kind"] == "full_response":
        res = resp_body
    elif ext["kind"] == "jsonpath":
        ok, res = extract_json_path(resp_body, ext["expression"])
        if not ok:
            return False, res
    else:
        ok, res = extract_regex(resp_body, ext["expression"], ext["regex_group"])
        if not ok:
            return False, res

    if not ext.get("conditions"):
        return True, res
    cond_ok, cond_res = apply_extraction_conditions([res], ext)
    if cond_ok and isinstance(cond_res, list) and cond_res:
        has_all = any(
            (not c.get("is_conditional") and c["op"] == "regex_extract_all")
            or (c.get("is_conditional") and (
                (c.get("then_branch") or {}).get("action") == "regex_extract_all"
                or (c.get("else_branch") or {}).get("action") == "regex_extract_all"
            ))
            for c in ext["conditions"]
        )
        return True, (", ".join(cond_res) if has_all and len(cond_res) > 1 else cond_res[0])
    return False, cond_res if isinstance(cond_res, str) else "La valeur extraite ne satisfait pas les conditions."


def run_list_extraction(ext: dict, resp_body: str, resp_headers: list[tuple[str, str]], source_values: list[str] | None = None) -> tuple[bool, list[str] | str]:
    if ext["source"] == "variable":
        src_name = ext.get("source_variable", "")
        if not src_name:
            return False, "Nom de variable source vide."
        if not source_values:
            return False, f"Variable source « {src_name} » vide ou introuvable."
        if ext["kind"] == "jsonpath":
            ok, res = extract_json_path_list_from_values(source_values, ext["expression"])
            if not ok or not isinstance(res, list):
                return False, res
            raw_values = res
        else:
            collected: list[str] = []
            last_err = "Aucune correspondance"
            for item in source_values:
                ok, res = extract_regex_list(item, ext["expression"], ext["regex_group"])
                if ok and isinstance(res, list):
                    collected.extend(res)
                elif isinstance(res, str):
                    last_err = res
            if not collected:
                return False, last_err
            raw_values = collected
    elif ext["source"] == "header":
        target_name = ext["header_name"].lower()
        matching = [v for k, v in resp_headers if k.lower() == target_name]
        if not matching:
            return False, f"En-tête « {ext['header_name']} » absent"
        collected = []
        last_err = "Aucune correspondance"
        for header_val in matching:
            ok, res = extract_regex_list(header_val, ext["expression"], ext["regex_group"])
            if ok and isinstance(res, list):
                collected.extend(res)
            elif isinstance(res, str):
                last_err = res
        if not collected:
            return False, last_err
        raw_values = collected
    elif ext["kind"] == "jsonpath":
        ok, res = extract_json_path_list(resp_body, ext["expression"])
        if not ok or not isinstance(res, list):
            return False, res
        raw_values = res
    else:
        ok, res = extract_regex_list(resp_body, ext["expression"], ext["regex_group"])
        if not ok or not isinstance(res, list):
            return False, res
        raw_values = res

    return apply_extraction_conditions(raw_values, ext)


def send_http(method: str, url: str, headers: list[tuple[str, str]], body_bytes: bytes | None, timeout_s: int) -> tuple[int, list[tuple[str, str]], str, str | None]:
    req = urllib.request.Request(url, data=body_bytes, method=method)
    for k, v in headers:
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=timeout_s) as resp:
            resp_headers = list(resp.headers.items())
            body_text = decode_response_bytes(resp.read(), resp_headers)
            return resp.status, resp_headers, body_text, None
    except urllib.error.HTTPError as err:
        resp_headers = list(err.headers.items()) if err.headers else []
        raw = err.read() if hasattr(err, "read") else b""
        body_text = decode_response_bytes(raw, resp_headers)
        return err.code, resp_headers, body_text, None
    except Exception as exc:
        return 0, [], "", str(exc)


_CACHED_IP: str | None = None


def resolve_current_ip() -> str:
    global _CACHED_IP
    if _CACHED_IP is not None:
        return _CACHED_IP

    for endpoint in ("https://api.ipify.org", "https://icanhazip.com"):
        try:
            req = urllib.request.Request(endpoint, headers={"User-Agent": "ApiTester-CLI/1.0"})
            with urllib.request.urlopen(req, timeout=2.5) as resp:
                candidate = resp.read().decode("utf-8", errors="replace").strip()
                ipaddress.ip_address(candidate)
                _CACHED_IP = candidate
                return _CACHED_IP
        except Exception:
            pass

    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.settimeout(0.5)
            sock.connect(("8.8.8.8", 80))
            local_ip = sock.getsockname()[0]
            if local_ip:
                ipaddress.ip_address(local_ip)
                _CACHED_IP = local_ip
                return _CACHED_IP
    except OSError:
        pass
    except ValueError:
        pass

    try:
        host_ip = socket.gethostbyname(socket.gethostname())
        if host_ip:
            ipaddress.ip_address(host_ip)
            _CACHED_IP = host_ip
            return _CACHED_IP
    except (OSError, ValueError):
        pass

    _CACHED_IP = "127.0.0.1"
    return _CACHED_IP


def resolve_return_identifier(name: str, force_scalar: bool, force_list: bool, scalars: dict[str, str], list_vars: dict[str, str], accumulated_lists: dict[str, list[str]], global_index: int, req_guid: str, req_ts: str):
    if name == "$index":
        return str(global_index)
    if name == "$guid":
        return req_guid
    if name == "$timestamp":
        return req_ts
    if name == "$ip":
        return resolve_current_ip()
    root, jp = split_var_path(name)
    if jp is not None:
        if force_list:
            if name in list_vars:
                return list_vars[name]
            if root in scalars:
                ok, res = extract_json_path_list(scalars[root], jp)
                if ok and isinstance(res, list):
                    return res
                raise KeyError(f"[[{name}]] : {res}")
            if root in accumulated_lists:
                ok, res = extract_json_path_list_from_values(accumulated_lists[root], jp)
                if ok and isinstance(res, list):
                    return res
                raise KeyError(f"[[{name}]] : {res}")
            raise KeyError(f"Variable ou liste inconnue : {root} (pour [[{name}]])")
        if root in scalars:
            if not force_scalar:
                try:
                    steps = parse_json_path(jp)
                    if any(w for _, _, w in steps):
                        ok, res = extract_json_path_list(scalars[root], jp)
                        if ok and isinstance(res, list):
                            return res
                        raise KeyError(f"{name} : {res}")
                except ValueError as exc:
                    raise KeyError(f"{name} : {exc}")
            ok, res = extract_json_path(scalars[root], jp)
            if ok:
                return res
            raise KeyError(f"{name} : {res}")
        if root in accumulated_lists:
            if not force_scalar:
                try:
                    steps = parse_json_path(jp)
                    if any(w for _, _, w in steps):
                        ok, res = extract_json_path_list_from_values(accumulated_lists[root], jp)
                        if ok and isinstance(res, list):
                            return res
                        raise KeyError(f"{name} : {res}")
                except ValueError as exc:
                    raise KeyError(f"{name} : {exc}")
            ok, res = extract_json_path_from_values(accumulated_lists[root], jp)
            if ok:
                return res
            raise KeyError(f"{name} : {res}")
        raise KeyError(f"Variable ou liste inconnue : {root} (pour {name})")
    if force_list:
        if name in list_vars:
            return list_vars[name]
        if name in accumulated_lists:
            return list(accumulated_lists[name])
        raise KeyError(f"Liste inconnue ou non définie : [[{name}]]")
    if force_scalar:
        if name in scalars:
            return scalars[name]
        if name == "ip":
            return resolve_current_ip()
        raise KeyError(f"Variable inconnue ou non définie : {{{{{name}}}}}")
    if name in list_vars:
        return list_vars[name]
    if name in scalars:
        return scalars[name]
    if name in accumulated_lists:
        return list(accumulated_lists[name])
    if name == "ip":
        return resolve_current_ip()
    raise KeyError(f"Variable ou liste inconnue : {name}")


def inline_return_str(val) -> str:
    if val is None:
        return ""
    if isinstance(val, bool):
        return "true" if val else "false"
    if isinstance(val, str):
        return val
    if isinstance(val, (dict, list)):
        return json.dumps(val, ensure_ascii=False)
    return str(val)


def eval_return_node(node: dict, scalars: dict[str, str], list_vars: dict[str, str], accumulated_lists: dict[str, list[str]], global_index: int, req_guid: str, req_ts: str):
    kind = node["type"]
    if kind == "null":
        return None
    if kind == "bool":
        return node["value"]
    if kind == "num":
        raw = node["raw"]
        return float(raw) if "." in raw else int(raw)
    if kind == "str":
        text = node["value"]
        if not node.get("interpolate"):
            return text

        def repl_ret(match: re.Match[str]) -> str:
            if match.group(2) is not None:
                v = resolve_return_identifier(match.group(2), False, True, scalars, list_vars, accumulated_lists, global_index, req_guid, req_ts)
            else:
                v = resolve_return_identifier(match.group(1), True, False, scalars, list_vars, accumulated_lists, global_index, req_guid, req_ts)
            return inline_return_str(v)

        return MARKER_RE.sub(repl_ret, text)
    if kind == "id":
        return resolve_return_identifier(
            node["name"],
            node.get("force_scalar", False),
            node.get("force_list", False),
            scalars,
            list_vars,
            accumulated_lists,
            global_index,
            req_guid,
            req_ts,
        )
    if kind == "arr":
        return [
            eval_return_node(item, scalars, list_vars, accumulated_lists, global_index, req_guid, req_ts)
            for item in node["items"]
        ]
    if kind == "obj":
        obj = {}
        for k, v_node in node["entries"]:
            obj[k] = eval_return_node(v_node, scalars, list_vars, accumulated_lists, global_index, req_guid, req_ts)
        return obj
    if kind == "concat":
        return "".join(
            inline_return_str(eval_return_node(part, scalars, list_vars, accumulated_lists, global_index, req_guid, req_ts))
            for part in node["parts"]
        )
    raise ValueError(f"Nœud de retour inconnu : {kind}")


def format_return_output(val) -> str:
    if val is None:
        return "null"
    if isinstance(val, str):
        return val
    if isinstance(val, bool):
        return "true" if val else "false"
    if isinstance(val, (int, float)):
        return json.dumps(val, ensure_ascii=False)
    return json.dumps(val, ensure_ascii=False, indent=2)


def print_return_output(formatted: str) -> None:
    if "\n" in formatted:
        print("  -> Retour :")
        print(formatted)
    else:
        print(f"  -> Retour : {formatted}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Exécute la chaîne de requêtes générée par ApiTester.")
    parser.add_argument("-v", "--verbose", action="store_true", help="Affiche les corps de réponse et variables extraites")
    args = parser.parse_args()

    accumulated_vars: dict[str, str] = {}
    accumulated_lists: dict[str, list[str]] = {}
    global_index = 0
    has_any_failure = False

    for step_idx, step in enumerate(STEPS):
        if step.get("return_error"):
            print(f"[ERREUR] Étape {step['rank']} ({step['name']}) : {step['return_error']}", file=sys.stderr)
            return 1

        if not step["url"] and not step.get("is_return_only"):
            print(f"[ERREUR] Étape {step['rank']} ({step['name']}) : URL vide.", file=sys.stderr)
            return 1

        scalar_candidates = {**accumulated_vars, **step["fixed_variables"]}
        iterations, empty_list_name, list_err = build_iterations(step["axes"], step["mode"], accumulated_lists, scalar_candidates)
        if list_err is not None:
            print(f"[ÉCHEC] Étape {step['rank']} ({step['name']}) : {list_err}", file=sys.stderr)
            has_any_failure = True
            if not step["continue_on_error"]:
                return 1
            continue
        if empty_list_name is not None:
            print(f"[IGNORÉE] Étape {step['rank']} ({step['name']}) : liste [[{empty_list_name}]] vide.")
            continue

        for iter_idx, axis_values in enumerate(iterations, start=1):
            global_index += 1
            req_guid = str(uuid.uuid4())
            req_ts = str(int(time.time()))

            merged_vars = dict(accumulated_vars)
            merged_vars.update(step["fixed_variables"])
            list_vars: dict[str, str] = {}
            for a_idx, axis in enumerate(step["axes"]):
                if axis["kind"] == "var":
                    merged_vars[axis["name"]] = axis_values[a_idx]
                elif axis["kind"] == "list":
                    list_vars[axis["name"]] = axis_values[a_idx]

            if step.get("is_return_only"):
                try:
                    ret_val = eval_return_node(
                        step["return_node"],
                        merged_vars,
                        list_vars,
                        accumulated_lists,
                        global_index,
                        req_guid,
                        req_ts,
                    )
                    formatted_ret = format_return_output(ret_val)
                    print(f"[OK] Étape {step['rank']} ({step['name']}) [{iter_idx}/{len(iterations)}] RETURN")
                    print_return_output(formatted_ret)
                except (KeyError, ValueError) as exc:
                    print(f"[ÉCHEC] Étape {step['rank']} ({step['name']}) [{iter_idx}/{len(iterations)}] RETURN : {exc}", file=sys.stderr)
                    has_any_failure = True
                    if not step["continue_on_error"]:
                        return 1
                    continue

                has_more = iter_idx < len(iterations) or step_idx < len(STEPS) - 1
                if has_more and step["delay_after_ms"] > 0:
                    time.sleep(step["delay_after_ms"] / 1000.0)
                continue

            try:
                final_base_url = substitute_url(step["url"], merged_vars, list_vars, accumulated_lists, global_index, req_guid, req_ts)
                query_items = []
                for qp in step["query_params"]:
                    if qp["axis_index"] is not None:
                        query_items.append((qp["key"], axis_values[qp["axis_index"]]))
                    else:
                        query_items.append((qp["key"], substitute_simple(qp["value"], merged_vars, list_vars, accumulated_lists, global_index, req_guid, req_ts)))
                final_url = build_final_url(final_base_url, query_items)
                final_headers = [
                    (k, substitute_simple(v, merged_vars, list_vars, accumulated_lists, global_index, req_guid, req_ts))
                    for k, v in step["headers"]
                ]

                body_bytes: bytes | None = None
                if step["body_type"] == "json":
                    body_bytes = substitute_json_body(step["body"], merged_vars, list_vars, accumulated_lists, global_index, req_guid, req_ts).encode("utf-8")
                elif step["body_type"] == "text":
                    body_bytes = substitute_simple(step["body"], merged_vars, list_vars, accumulated_lists, global_index, req_guid, req_ts).encode("utf-8")
                elif step["body_type"] == "form":
                    pairs = [
                        (k, substitute_simple(v, merged_vars, list_vars, accumulated_lists, global_index, req_guid, req_ts))
                        for k, v in step["form_fields"]
                    ]
                    body_bytes = urllib.parse.urlencode(pairs).encode("utf-8")
            except KeyError as exc:
                print(f"[ÉCHEC] Étape {step['rank']} ({step['name']}) [{iter_idx}/{len(iterations)}] : {exc}", file=sys.stderr)
                has_any_failure = True
                if not step["continue_on_error"]:
                    return 1
                continue

            t0 = time.monotonic()
            status, resp_headers, resp_body, net_err = send_http(
                step["method"], final_url, final_headers, body_bytes, step["timeout_seconds"]
            )
            elapsed_ms = int((time.monotonic() - t0) * 1000)

            http_failed = net_err is not None or status >= 400
            extraction_failed = False
            local_extracted: dict[str, str] = {}
            local_lists: dict[str, list[str]] = {}

            for ext in step["extractions"]:
                src_values = (
                    resolve_variable_source_values(
                        ext.get("source_variable", ""),
                        local_lists,
                        local_extracted,
                        accumulated_lists,
                        merged_vars,
                        list_vars,
                    )
                    if ext["source"] == "variable"
                    else None
                )
                if ext.get("is_list"):
                    ok, values_or_err = run_list_extraction(ext, resp_body, resp_headers, src_values)
                    if ok and isinstance(values_or_err, list):
                        accumulated_lists[ext["variable"]] = values_or_err
                        local_lists[ext["variable"]] = values_or_err
                        if args.verbose:
                            print(f"  -> [[{ext['variable']}]] = {len(values_or_err)} élément(s)")
                    else:
                        fallback_list = [ext["default"]] if ext["default"] else []
                        accumulated_lists[ext["variable"]] = fallback_list
                        local_lists[ext["variable"]] = fallback_list
                        if ext["required"]:
                            extraction_failed = True
                            print(f"  [EXTRACTION] [[{ext['variable']}]] : {values_or_err}", file=sys.stderr)
                    continue

                ok, value_or_err = run_extraction(ext, resp_body, resp_headers, src_values)
                if ok:
                    accumulated_vars[ext["variable"]] = value_or_err
                    local_extracted[ext["variable"]] = value_or_err
                    if args.verbose:
                        print(f"  -> {ext['variable']} = {value_or_err}")
                else:
                    accumulated_vars[ext["variable"]] = ext["default"]
                    local_extracted[ext["variable"]] = ext["default"]
                    if ext["required"]:
                        extraction_failed = True
                        print(f"  [EXTRACTION] {ext['variable']} : {value_or_err}", file=sys.stderr)

            step_failed = http_failed or extraction_failed
            formatted_ret: str | None = None
            return_err: str | None = None
            if not step_failed and step.get("return_node") is not None:
                return_vars = dict(merged_vars)
                return_vars.update(local_extracted)
                try:
                    ret_val = eval_return_node(
                        step["return_node"],
                        return_vars,
                        list_vars,
                        accumulated_lists,
                        global_index,
                        req_guid,
                        req_ts,
                    )
                    formatted_ret = format_return_output(ret_val)
                except (KeyError, ValueError) as exc:
                    step_failed = True
                    return_err = str(exc)

            status_label = "ÉCHEC" if step_failed else "OK"
            code_label = net_err if net_err is not None else f"HTTP {status}"
            print(f"[{status_label}] Étape {step['rank']} ({step['name']}) [{iter_idx}/{len(iterations)}] {step['method']} {final_url} -> {code_label} ({elapsed_ms} ms)")

            if return_err is not None:
                print(f"  [RETOUR] {return_err}", file=sys.stderr)
            elif formatted_ret is not None:
                print_return_output(formatted_ret)

            if args.verbose and resp_body:
                print(resp_body[:1000])

            if step_failed:
                has_any_failure = True
                if not step["continue_on_error"]:
                    return 1

            has_more = iter_idx < len(iterations) or step_idx < len(STEPS) - 1
            if has_more and step["delay_after_ms"] > 0:
                time.sleep(step["delay_after_ms"] / 1000.0)

    return 1 if has_any_failure else 0


if __name__ == "__main__":
    sys.exit(main())
""");
    }

    private static string FormatReturnNode(StepReturnNode? node)
    {
        if (node is null)
        {
            return "None";
        }

        switch (node)
        {
            case NullReturnNode:
                return "{\"type\": \"null\"}";
            case BooleanReturnNode b:
                return $"{{\"type\": \"bool\", \"value\": {FormatBool(b.Value)}}}";
            case NumberReturnNode n:
                return $"{{\"type\": \"num\", \"raw\": {EscapeString(n.RawText)}}}";
            case StringLiteralReturnNode s:
                return $"{{\"type\": \"str\", \"value\": {EscapeString(s.Value)}, \"interpolate\": {FormatBool(s.HasTemplateMarkers)}}}";
            case IdentifierReturnNode id:
                return $"{{\"type\": \"id\", \"name\": {EscapeString(id.Name)}, \"force_scalar\": {FormatBool(id.ForceScalar)}, \"force_list\": {FormatBool(id.ForceList)}}}";
            case ArrayReturnNode arr:
            {
                var sb = new StringBuilder("{\"type\": \"arr\", \"items\": [");
                for (var i = 0; i < arr.Items.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(FormatReturnNode(arr.Items[i]));
                }
                sb.Append("]}");
                return sb.ToString();
            }
            case ObjectReturnNode obj:
            {
                var sb = new StringBuilder("{\"type\": \"obj\", \"entries\": [");
                for (var i = 0; i < obj.Entries.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append('(');
                    sb.Append(EscapeString(obj.Entries[i].Key));
                    sb.Append(", ");
                    sb.Append(FormatReturnNode(obj.Entries[i].Value));
                    sb.Append(')');
                }
                sb.Append("]}");
                return sb.ToString();
            }
            case ConcatReturnNode concat:
            {
                var sb = new StringBuilder("{\"type\": \"concat\", \"parts\": [");
                for (var i = 0; i < concat.Parts.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(FormatReturnNode(concat.Parts[i]));
                }
                sb.Append("]}");
                return sb.ToString();
            }
            default:
                return "None";
        }
    }

    private static string FormatBool(bool value) => value ? "True" : "False";

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
