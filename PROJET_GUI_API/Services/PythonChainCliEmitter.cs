using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        sb.AppendLine("import itertools");
        sb.AppendLine("import json");
        sb.AppendLine("import re");
        sb.AppendLine("import sys");
        sb.AppendLine("import time");
        sb.AppendLine("import urllib.error");
        sb.AppendLine("import urllib.parse");
        sb.AppendLine("import urllib.request");
        sb.AppendLine("import uuid");
        sb.AppendLine("import zlib");
        sb.AppendLine();
        sb.AppendLine(@"MARKER_RE = re.compile(r""(?:\{\{\s*(\$?(?!\d)\w+)\s*\}\}|\[\[\s*((?!\d)(?!\$)\w+)\s*\]\])"")");
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
                sb.AppendLine($"                \"required\": {FormatBool(ext.IsRequired)},");
                sb.AppendLine($"                \"default\": {EscapeString(ext.DefaultValueIfMissing)},");
                sb.AppendLine($"                \"is_list\": {FormatBool(ext.IsList)},");
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


def build_iterations(axes: list[dict], mode: str, accumulated_lists: dict[str, list[str]]) -> tuple[list[tuple[str, ...]], str | None]:
    if not axes:
        return [()], None
    axis_lists: list[list[str]] = []
    for a in axes:
        if a["kind"] == "list":
            lst = list(accumulated_lists.get(a["name"], []))
            if not lst:
                return [], a["name"]
            axis_lists.append(lst)
        elif "range" in a:
            lst = make_range(a["range"])
            if not lst:
                return [], None
            axis_lists.append(lst)
        else:
            lst = list(a["values"])
            if not lst:
                return [], None
            axis_lists.append(lst)
    if mode == "parallel":
        return list(zip(*axis_lists)), None
    return list(itertools.product(*axis_lists)), None


def resolve_marker(match: re.Match[str], variables: dict[str, str], list_vars: dict[str, str], global_index: int, req_guid: str, req_ts: str) -> str:
    list_name = match.group(2)
    if list_name is not None:
        if list_name in list_vars:
            return list_vars[list_name]
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
    raise KeyError(f"Variable inconnue ou non définie : {{{{{name}}}}}")


def substitute_simple(text: str, variables: dict[str, str], list_vars: dict[str, str], global_index: int, req_guid: str, req_ts: str, url_encode: bool = False) -> str:
    if not text:
        return ""

    def repl(match: re.Match[str]) -> str:
        val = resolve_marker(match, variables, list_vars, global_index, req_guid, req_ts)
        return urllib.parse.quote(val, safe="") if url_encode else val

    return MARKER_RE.sub(repl, text)


def substitute_json_body(text: str, variables: dict[str, str], list_vars: dict[str, str], global_index: int, req_guid: str, req_ts: str) -> str:
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
        val = resolve_marker(match, variables, list_vars, global_index, req_guid, req_ts)
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
    if path.startswith("$."):
        path = path[2:]
    elif path.startswith("$"):
        path = path[1:]
    if not path.strip():
        raise ValueError("Chemin JSONPath vide.")
    steps: list[tuple[str | None, int | None, bool]] = []
    for part in path.split("."):
        if not part:
            raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
        bracket = part.find("[")
        if bracket < 0:
            steps.append((part, None, False))
            continue
        prop = part[:bracket] if bracket > 0 else None
        pos = bracket
        first = True
        while pos < len(part):
            if part[pos] != "[":
                raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
            close = part.find("]", pos + 1)
            if close < 0:
                raise ValueError(f"Chemin JSONPath invalide : {raw_path}")
            idx_str = part[pos + 1:close].strip()
            if idx_str in ("*", "", "++") or re.fullmatch(r"(?:[A-Za-z_]\w*\+\+|\+\+[A-Za-z_]\w*)", idx_str):
                steps.append((prop if first else None, None, True))
            else:
                idx = int(idx_str)
                steps.append((prop if first else None, idx, False))
            first = False
            pos = close + 1
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


def extract_json_path(body: str, raw_path: str) -> tuple[bool, str]:
    try:
        steps = parse_json_path(raw_path)
        if any(is_wild for _, _, is_wild in steps):
            return False, "Le sélecteur [*] ou [] extrait plusieurs valeurs : utilise une extraction en liste [[...]]."
        current = json.loads(body.lstrip("\ufeff"))
        for prop, arr_idx, _ in steps:
            if prop is not None:
                if not isinstance(current, dict) or prop not in current:
                    return False, f"Propriété JSON introuvable : {prop}"
                current = current[prop]
            if arr_idx is not None:
                if not isinstance(current, list):
                    return False, f"Index [{arr_idx}] inapplicable"
                resolved = len(current) + arr_idx if arr_idx < 0 else arr_idx
                if resolved < 0 or resolved >= len(current):
                    return False, f"Index [{arr_idx}] hors limites"
                current = current[resolved]
        return True, format_json_value(current)
    except Exception as exc:
        return False, str(exc)


def extract_json_path_list(body: str, raw_path: str) -> tuple[bool, list[str] | str]:
    try:
        steps = parse_json_path(raw_path)
        current_elements = [json.loads(body.lstrip("\ufeff"))]
        has_wildcard = False
        for prop, arr_idx, is_wildcard in steps:
            if prop is not None:
                next_elements = []
                for el in current_elements:
                    if not isinstance(el, dict) or prop not in el:
                        if not has_wildcard:
                            return False, f"Propriété JSON introuvable : {prop}"
                        continue
                    next_elements.append(el[prop])
                if not next_elements:
                    return False, f"Propriété JSON introuvable : {prop}"
                current_elements = next_elements
            if is_wildcard:
                was_wildcard = has_wildcard
                has_wildcard = True
                next_elements = []
                for el in current_elements:
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
                for el in current_elements:
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

        if not has_wildcard and len(current_elements) == 1 and isinstance(current_elements[0], list):
            if not current_elements[0]:
                return False, "Le tableau JSON ciblé est vide."
            return True, [format_json_value(item) for item in current_elements[0]]

        if not current_elements:
            return False, "Aucune valeur extraite pour l'expression JSONPath."
        return True, [format_json_value(el) for el in current_elements]
    except Exception as exc:
        return False, str(exc)


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


def run_extraction(ext: dict, resp_body: str, resp_headers: list[tuple[str, str]]) -> tuple[bool, str]:
    if ext["source"] == "header":
        target_name = ext["header_name"].lower()
        matching = [v for k, v in resp_headers if k.lower() == target_name]
        if not matching:
            return False, f"En-tête « {ext['header_name']} » absent"
        last_err = "Aucune correspondance"
        for header_val in matching:
            ok, res = extract_regex(header_val, ext["expression"], ext["regex_group"])
            if ok:
                return True, res
            last_err = res
        return False, last_err
    if ext["kind"] == "jsonpath":
        return extract_json_path(resp_body, ext["expression"])
    return extract_regex(resp_body, ext["expression"], ext["regex_group"])


def run_list_extraction(ext: dict, resp_body: str, resp_headers: list[tuple[str, str]]) -> tuple[bool, list[str] | str]:
    if ext["source"] == "header":
        target_name = ext["header_name"].lower()
        matching = [v for k, v in resp_headers if k.lower() == target_name]
        if not matching:
            return False, f"En-tête « {ext['header_name']} » absent"
        collected: list[str] = []
        last_err = "Aucune correspondance"
        for header_val in matching:
            ok, res = extract_regex_list(header_val, ext["expression"], ext["regex_group"])
            if ok and isinstance(res, list):
                collected.extend(res)
            elif isinstance(res, str):
                last_err = res
        return (True, collected) if collected else (False, last_err)
    if ext["kind"] == "jsonpath":
        return extract_json_path_list(resp_body, ext["expression"])
    return extract_regex_list(resp_body, ext["expression"], ext["regex_group"])


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


def main() -> int:
    parser = argparse.ArgumentParser(description="Exécute la chaîne de requêtes générée par ApiTester.")
    parser.add_argument("-v", "--verbose", action="store_true", help="Affiche les corps de réponse et variables extraites")
    args = parser.parse_args()

    accumulated_vars: dict[str, str] = {}
    accumulated_lists: dict[str, list[str]] = {}
    global_index = 0
    has_any_failure = False

    for step_idx, step in enumerate(STEPS):
        if not step["url"]:
            print(f"[ERREUR] Étape {step['rank']} ({step['name']}) : URL vide.", file=sys.stderr)
            return 1

        iterations, empty_list_name = build_iterations(step["axes"], step["mode"], accumulated_lists)
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

            try:
                final_base_url = substitute_simple(step["url"], merged_vars, list_vars, global_index, req_guid, req_ts, url_encode=True)
                query_items = []
                for qp in step["query_params"]:
                    if qp["axis_index"] is not None:
                        query_items.append((qp["key"], axis_values[qp["axis_index"]]))
                    else:
                        query_items.append((qp["key"], substitute_simple(qp["value"], merged_vars, list_vars, global_index, req_guid, req_ts)))
                final_url = build_final_url(final_base_url, query_items)
                final_headers = [
                    (k, substitute_simple(v, merged_vars, list_vars, global_index, req_guid, req_ts))
                    for k, v in step["headers"]
                ]

                body_bytes: bytes | None = None
                if step["body_type"] == "json":
                    body_bytes = substitute_json_body(step["body"], merged_vars, list_vars, global_index, req_guid, req_ts).encode("utf-8")
                elif step["body_type"] == "text":
                    body_bytes = substitute_simple(step["body"], merged_vars, list_vars, global_index, req_guid, req_ts).encode("utf-8")
                elif step["body_type"] == "form":
                    pairs = [
                        (k, substitute_simple(v, merged_vars, list_vars, global_index, req_guid, req_ts))
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

            for ext in step["extractions"]:
                if ext.get("is_list"):
                    ok, values_or_err = run_list_extraction(ext, resp_body, resp_headers)
                    if ok and isinstance(values_or_err, list):
                        accumulated_lists[ext["variable"]] = values_or_err
                        if args.verbose:
                            print(f"  -> [[{ext['variable']}]] = {len(values_or_err)} élément(s)")
                    else:
                        accumulated_lists[ext["variable"]] = [ext["default"]] if ext["default"] else []
                        if ext["required"]:
                            extraction_failed = True
                            print(f"  [EXTRACTION] [[{ext['variable']}]] : {values_or_err}", file=sys.stderr)
                    continue

                ok, value_or_err = run_extraction(ext, resp_body, resp_headers)
                if ok:
                    accumulated_vars[ext["variable"]] = value_or_err
                    if args.verbose:
                        print(f"  -> {ext['variable']} = {value_or_err}")
                else:
                    accumulated_vars[ext["variable"]] = ext["default"]
                    if ext["required"]:
                        extraction_failed = True
                        print(f"  [EXTRACTION] {ext['variable']} : {value_or_err}", file=sys.stderr)

            step_failed = http_failed or extraction_failed
            status_label = "ÉCHEC" if step_failed else "OK"
            code_label = net_err if net_err is not None else f"HTTP {status}"
            print(f"[{status_label}] Étape {step['rank']} ({step['name']}) [{iter_idx}/{len(iterations)}] {step['method']} {final_url} -> {code_label} ({elapsed_ms} ms)")

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
