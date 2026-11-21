import type {
  AuthConfig,
  KeyValueItem,
  RequestModel,
  ResponseModel,
  StatusCategory,
} from '../types/apiTester';

export function isSensitiveName(name?: string | null): boolean {
  if (!name) return false;
  return /token|key|secret|password|auth/i.test(name);
}

export function maskIfSensitive(name?: string | null, value?: string | null): string {
  return isSensitiveName(name) ? '***' : (value ?? '');
}

export function normalizeRequestUrl(rawUrl: string): string {
  const trimmed = (rawUrl ?? '').trim();
  if (!trimmed) return '';
  if (/^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//.test(trimmed)) {
    return trimmed;
  }
  if (/^(localhost|127\.0\.0\.1|\[::1\])([:/?#]|$)/i.test(trimmed)) {
    return `http://${trimmed}`;
  }
  return `https://${trimmed}`;
}

function buildQuerySegments(
  queryItems: KeyValueItem[],
  auth: AuthConfig,
  maskAllSensitive: boolean
): string[] {
  const parts: string[] = [];

  for (const param of queryItems) {
    if (!param.isEnabled || !param.key.trim()) continue;
    const key = param.key.trim();
    const encodedKey = encodeURIComponent(key);
    const encodedValue =
      maskAllSensitive && isSensitiveName(key)
        ? '***'
        : encodeURIComponent(param.value ?? '');
    parts.push(`${encodedKey}=${encodedValue}`);
  }

  if (auth.type === 'ApiKey' && !auth.apiKeyInHeader && auth.apiKeyName.trim()) {
    const key = auth.apiKeyName.trim();
    const encodedKey = encodeURIComponent(key);
    const encodedValue =
      maskAllSensitive || isSensitiveName(key)
        ? '***'
        : encodeURIComponent(auth.apiKeyValue ?? '');
    parts.push(`${encodedKey}=${encodedValue}`);
  }

  return parts;
}

export function buildFinalUrl(
  rawUrl: string,
  queryItems: KeyValueItem[],
  auth: AuthConfig,
  maskAllSensitive = false
): string {
  const trimmed = (rawUrl ?? '').trim();
  const extraParts = buildQuerySegments(queryItems, auth, maskAllSensitive);

  try {
    const parsed = new URL(trimmed);
    const existing = parsed.search.startsWith('?') ? parsed.search.slice(1) : '';
    const segments: string[] = [];
    if (existing) {
      for (const seg of existing.split('&')) {
        if (!seg) continue;
        const eqIdx = seg.indexOf('=');
        if (eqIdx < 0) {
          segments.push(seg);
          continue;
        }
        const encodedKey = seg.slice(0, eqIdx);
        let decodedKey = encodedKey;
        try {
          decodedKey = decodeURIComponent(encodedKey.replace(/\+/g, ' '));
        } catch {
          // ignore
        }
        segments.push(maskAllSensitive && isSensitiveName(decodedKey) ? `${encodedKey}=***` : seg);
      }
    }
    segments.push(...extraParts);
    parsed.search = segments.length > 0 ? `?${segments.join('&')}` : '';
    return parsed.toString();
  } catch {
    if (extraParts.length === 0) return trimmed;
    const sep = trimmed.includes('?') ? '&' : '?';
    return `${trimmed}${sep}${extraParts.join('&')}`;
  }
}

export function determineStatusCategory(response: ResponseModel): StatusCategory {
  if (response.errorMessage && response.errorMessage.trim().length > 0) {
    return 'NetworkError';
  }
  if (response.statusCode >= 200 && response.statusCode < 300) return 'Success';
  if (response.statusCode >= 300 && response.statusCode < 400) return 'Redirect';
  if (response.statusCode >= 400 && response.statusCode < 500) return 'ClientError';
  if (response.statusCode >= 500 && response.statusCode < 600) return 'ServerError';
  return 'None';
}

export function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '0 B';
  const kb = 1024;
  const mb = kb * 1024;
  const gb = mb * 1024;
  if (bytes < kb) return `${Math.round(bytes)} B`;
  if (bytes < mb) return `${(bytes / kb).toFixed(2).replace(/\.?0+$/, '')} KB`;
  if (bytes < gb) return `${(bytes / mb).toFixed(2).replace(/\.?0+$/, '')} MB`;
  return `${(bytes / gb).toFixed(2).replace(/\.?0+$/, '')} GB`;
}

export function truncateForDisplay(value: string | null | undefined, maxLength = 40): string {
  if (!value || maxLength <= 0) return '';
  return value.length > maxLength ? `${value.slice(0, maxLength)}…` : value;
}

export async function sendHttpRequest(
  request: RequestModel,
  signal?: AbortSignal
): Promise<ResponseModel> {
  try {
    const resp = await fetch('/api/http', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        method: request.method,
        url: request.url,
        queryParams: request.queryParams.map((q) => ({
          key: q.key,
          value: q.value,
          isEnabled: q.isEnabled,
        })),
        headers: request.headers.map((h) => ({
          key: h.key,
          value: h.value,
          isEnabled: h.isEnabled,
        })),
        bodyType: request.bodyType,
        body: request.body,
        formFields: request.formFields.map((f) => ({
          key: f.key,
          value: f.value,
          isEnabled: f.isEnabled,
        })),
        auth: request.auth,
        timeoutSeconds: request.timeoutSeconds,
      }),
      signal,
    });

    if (!resp.ok) {
      return {
        statusCode: 0,
        reasonPhrase: '',
        isSuccess: false,
        durationMs: 0,
        sizeBytes: 0,
        contentType: '',
        headers: [],
        body: '',
        errorMessage: `Erreur proxy HTTP ${resp.status}.`,
      };
    }

    const data = (await resp.json()) as {
      statusCode: number;
      reasonPhrase: string;
      isSuccess: boolean;
      durationMs: number;
      sizeBytes: number;
      contentType: string;
      headers: { key: string; value: string; isEnabled: boolean }[];
      body: string;
      errorMessage: string | null;
    };

    return {
      statusCode: data.statusCode ?? 0,
      reasonPhrase: data.reasonPhrase ?? '',
      isSuccess: Boolean(data.isSuccess),
      durationMs: data.durationMs ?? 0,
      sizeBytes: data.sizeBytes ?? 0,
      contentType: data.contentType ?? '',
      headers: (data.headers ?? []).map((h, idx) => ({
        id: `rh_${idx}`,
        key: h.key,
        value: h.value,
        isEnabled: h.isEnabled ?? true,
      })),
      body: data.body ?? '',
      errorMessage: data.errorMessage ?? null,
    };
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      return {
        statusCode: 0,
        reasonPhrase: '',
        isSuccess: false,
        durationMs: 0,
        sizeBytes: 0,
        contentType: '',
        headers: [],
        body: '',
        errorMessage: 'Requête annulée.',
      };
    }

    return {
      statusCode: 0,
      reasonPhrase: '',
      isSuccess: false,
      durationMs: 0,
      sizeBytes: 0,
      contentType: '',
      headers: [],
      body: '',
      errorMessage: err instanceof Error ? err.message : String(err),
    };
  }
}

export async function resolvePublicIp(signal?: AbortSignal): Promise<string> {
  try {
    const resp = await fetch('/api/ip', { signal });
    if (resp.ok) {
      const json = (await resp.json()) as { ip?: string };
      if (json.ip) return json.ip;
    }
  } catch {
    // Fallback below
  }
  return '127.0.0.1';
}
