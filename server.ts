import express from 'express';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT || 3000);
const HOST = '0.0.0.0';

interface KeyValueInput {
  key: string;
  value: string;
  isEnabled: boolean;
}

interface AuthInput {
  type: 'None' | 'Bearer' | 'Basic' | 'ApiKey';
  token?: string;
  username?: string;
  password?: string;
  apiKeyName?: string;
  apiKeyValue?: string;
  apiKeyInHeader?: boolean;
}

interface HttpProxyRequest {
  method: string;
  url: string;
  queryParams?: KeyValueInput[];
  headers?: KeyValueInput[];
  bodyType?: 'None' | 'Json' | 'Text' | 'FormUrlEncoded';
  body?: string;
  formFields?: KeyValueInput[];
  auth?: AuthInput;
  timeoutSeconds?: number;
}

let cachedIp: { value: string; expiresAt: number } | null = null;

async function resolvePublicIp(): Promise<string> {
  const now = Date.now();
  if (cachedIp && cachedIp.expiresAt > now) {
    return cachedIp.value;
  }

  for (const endpoint of ['https://api.ipify.org', 'https://icanhazip.com']) {
    try {
      const resp = await fetch(endpoint, {
        headers: { 'User-Agent': 'ApiTester/1.0' },
        signal: AbortSignal.timeout(2500),
      });
      if (resp.ok) {
        const candidate = (await resp.text()).trim();
        if (net.isIP(candidate) !== 0) {
          cachedIp = { value: candidate, expiresAt: now + 5 * 60 * 1000 };
          return candidate;
        }
      }
    } catch {
      // Try next provider
    }
  }

  try {
    const ifaces = os.networkInterfaces();
    for (const infos of Object.values(ifaces)) {
      if (!infos) continue;
      for (const info of infos) {
        if (info && info.family === 'IPv4' && !info.internal && info.address) {
          cachedIp = { value: info.address, expiresAt: now + 5 * 60 * 1000 };
          return info.address;
        }
      }
    }
  } catch {
    // Fallback below
  }

  return '127.0.0.1';
}

function buildFinalUrl(payload: HttpProxyRequest): { url?: URL; error?: string } {
  const rawUrl = (payload.url ?? '').trim();
  if (!rawUrl) {
    return { error: 'URL cannot be empty.' };
  }

  let parsed: URL;
  try {
    parsed = new URL(rawUrl);
  } catch {
    return { error: 'Invalid URL. Please provide an absolute HTTP or HTTPS URL.' };
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return { error: 'Invalid URL. Please provide an absolute HTTP or HTTPS URL.' };
  }

  const queryParts: string[] = [];
  const existing = parsed.search.startsWith('?') ? parsed.search.slice(1) : '';
  if (existing) {
    queryParts.push(existing);
  }

  for (const param of payload.queryParams ?? []) {
    if (!param.isEnabled || !param.key || !param.key.trim()) {
      continue;
    }
    queryParts.push(`${encodeURIComponent(param.key.trim())}=${encodeURIComponent(param.value ?? '')}`);
  }

  if (
    payload.auth?.type === 'ApiKey' &&
    payload.auth.apiKeyInHeader === false &&
    payload.auth.apiKeyName &&
    payload.auth.apiKeyName.trim()
  ) {
    queryParts.push(
      `${encodeURIComponent(payload.auth.apiKeyName.trim())}=${encodeURIComponent(payload.auth.apiKeyValue ?? '')}`
    );
  }

  parsed.search = queryParts.length > 0 ? `?${queryParts.join('&')}` : '';
  return { url: parsed };
}

function hasHeaderCaseInsensitive(headersMap: Map<string, { name: string; value: string }>, key: string): string | undefined {
  const lower = key.toLowerCase();
  for (const [k, v] of headersMap.entries()) {
    if (k === lower) return v.name;
  }
  return undefined;
}

async function createApp() {
  const app = express();
  app.use(express.json({ limit: '25mb' }));

  app.get('/api/ip', async (_req, res) => {
    const ip = await resolvePublicIp();
    res.json({ ip });
  });

  app.post('/api/http', async (req, res) => {
    const payload = (req.body ?? {}) as HttpProxyRequest;
    const { url: finalUrl, error: urlError } = buildFinalUrl(payload);
    if (!finalUrl) {
      res.json({
        statusCode: 0,
        reasonPhrase: '',
        isSuccess: false,
        durationMs: 0,
        sizeBytes: 0,
        contentType: '',
        headers: [],
        body: '',
        errorMessage: urlError ?? 'Invalid URL.',
      });
      return;
    }

    const timeoutSeconds =
      typeof payload.timeoutSeconds === 'number' && payload.timeoutSeconds > 0
        ? Math.min(payload.timeoutSeconds, 3600)
        : 30;

    const method = (payload.method || 'GET').toUpperCase();
    const headerEntries = new Map<string, { name: string; value: string }>();

    for (const header of payload.headers ?? []) {
      if (!header.isEnabled || !header.key || !header.key.trim()) {
        continue;
      }
      const key = header.key.trim();
      const lower = key.toLowerCase();
      if (lower === 'content-length' || lower === 'host' || lower === 'connection') {
        continue;
      }
      headerEntries.set(lower, { name: key, value: header.value ?? '' });
    }

    const auth = payload.auth;
    if (auth) {
      if (auth.type === 'Bearer' && auth.token && auth.token.trim()) {
        headerEntries.set('authorization', {
          name: 'Authorization',
          value: `Bearer ${auth.token.trim()}`,
        });
      } else if (auth.type === 'Basic') {
        const creds = Buffer.from(`${auth.username ?? ''}:${auth.password ?? ''}`, 'utf8').toString('base64');
        headerEntries.set('authorization', {
          name: 'Authorization',
          value: `Basic ${creds}`,
        });
      } else if (auth.type === 'ApiKey' && auth.apiKeyInHeader !== false && auth.apiKeyName && auth.apiKeyName.trim()) {
        const keyName = auth.apiKeyName.trim();
        headerEntries.set(keyName.toLowerCase(), {
          name: keyName,
          value: auth.apiKeyValue ?? '',
        });
      }
    }

    let requestBody: string | undefined;
    const bodyType = payload.bodyType ?? 'None';
    if (method !== 'GET' && method !== 'HEAD') {
      if (bodyType === 'Json') {
        requestBody = payload.body ?? '';
        if (!hasHeaderCaseInsensitive(headerEntries, 'content-type')) {
          headerEntries.set('content-type', { name: 'Content-Type', value: 'application/json; charset=utf-8' });
        }
      } else if (bodyType === 'Text') {
        let textBody = payload.body ?? '';
        const ctHeader = headerEntries.get('content-type')?.value ?? '';
        if (ctHeader.toLowerCase().includes('multipart/form-data')) {
          textBody = textBody.replace(/\r\n/g, '\n').replace(/\r/g, '\n').replace(/\n/g, '\r\n');
        }
        requestBody = textBody;
        if (!hasHeaderCaseInsensitive(headerEntries, 'content-type')) {
          headerEntries.set('content-type', { name: 'Content-Type', value: 'text/plain; charset=utf-8' });
        }
      } else if (bodyType === 'FormUrlEncoded') {
        const params = new URLSearchParams();
        for (const field of payload.formFields ?? []) {
          if (!field.isEnabled || !field.key || !field.key.trim()) {
            continue;
          }
          params.append(field.key.trim(), field.value ?? '');
        }
        requestBody = params.toString();
        if (!hasHeaderCaseInsensitive(headerEntries, 'content-type')) {
          headerEntries.set('content-type', {
            name: 'Content-Type',
            value: 'application/x-www-form-urlencoded; charset=utf-8',
          });
        }
      }
    }

    const fetchHeaders = new Headers();
    for (const { name, value } of headerEntries.values()) {
      try {
        fetchHeaders.set(name, value);
      } catch {
        // Ignore invalid header lines gracefully
      }
    }

    const controller = new AbortController();
    let timedOut = false;
    let clientAborted = false;
    const timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, timeoutSeconds * 1000);

    const onClose = () => {
      if (!res.writableEnded) {
        clientAborted = true;
        controller.abort();
      }
    };
    req.on('close', onClose);

    const startedAt = performance.now();
    try {
      const response = await fetch(finalUrl.toString(), {
        method,
        headers: fetchHeaders,
        body: requestBody,
        redirect: 'follow',
        signal: controller.signal,
      });

      const arrayBuffer = await response.arrayBuffer();
      const durationMs = Math.max(0, Math.round(performance.now() - startedAt));
      const buffer = Buffer.from(arrayBuffer);
      const bodyText = buffer.toString('utf8');

      const respHeaders: KeyValueInput[] = [];
      response.headers.forEach((value, key) => {
        respHeaders.push({ key, value, isEnabled: true });
      });

      const contentType = response.headers.get('content-type') ?? '';
      const contentLengthHeader = response.headers.get('content-length');
      const parsedLength = contentLengthHeader ? Number.parseInt(contentLengthHeader, 10) : Number.NaN;
      const sizeBytes = Number.isFinite(parsedLength) && parsedLength >= 0 ? parsedLength : buffer.byteLength;

      res.json({
        statusCode: response.status,
        reasonPhrase: response.statusText || '',
        isSuccess: response.ok,
        durationMs,
        sizeBytes,
        contentType,
        headers: respHeaders,
        body: bodyText,
        errorMessage: null,
      });
    } catch (err) {
      const durationMs = Math.max(0, Math.round(performance.now() - startedAt));
      if (clientAborted) {
        if (!res.writableEnded) {
          res.json({
            statusCode: 0,
            reasonPhrase: '',
            isSuccess: false,
            durationMs,
            sizeBytes: 0,
            contentType: '',
            headers: [],
            body: '',
            errorMessage: 'The request was cancelled.',
          });
        }
        return;
      }

      if (timedOut) {
        res.json({
          statusCode: 0,
          reasonPhrase: '',
          isSuccess: false,
          durationMs,
          sizeBytes: 0,
          contentType: '',
          headers: [],
          body: '',
          errorMessage: `Request timed out after ${timeoutSeconds} seconds.`,
        });
        return;
      }

      const message = err instanceof Error ? err.message : String(err);
      const cause =
        err instanceof Error && 'cause' in err && err.cause instanceof Error
          ? ` (${err.cause.message})`
          : '';
      res.json({
        statusCode: 0,
        reasonPhrase: '',
        isSuccess: false,
        durationMs,
        sizeBytes: 0,
        contentType: '',
        headers: [],
        body: '',
        errorMessage: `Network error: ${message}${cause}`,
      });
    } finally {
      clearTimeout(timer);
      req.off('close', onClose);
    }
  });

  if (process.env.NODE_ENV !== 'production') {
    const { createServer: createViteServer } = await import('vite');
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: 'custom',
    });
    app.use(vite.middlewares);

    app.use(async (req, res, next) => {
      if (req.originalUrl.startsWith('/api/')) {
        next();
        return;
      }
      try {
        const htmlPath = path.resolve(__dirname, 'index.html');
        let html = fs.readFileSync(htmlPath, 'utf-8');
        html = await vite.transformIndexHtml(req.originalUrl, html);
        res.status(200).set({ 'Content-Type': 'text/html' }).end(html);
      } catch (e) {
        vite.ssrFixStacktrace(e as Error);
        next(e);
      }
    });
  } else {
    const distPath = path.resolve(__dirname, 'dist');
    app.use(express.static(distPath));
    app.use((_req, res) => {
      res.sendFile(path.resolve(distPath, 'index.html'));
    });
  }

  app.listen(PORT, HOST, () => {
    console.log(`ApiTester server running at http://${HOST}:${PORT}`);
  });
}

createApp();
