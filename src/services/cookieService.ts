import { createId, type CookiePairItem } from '../types/apiTester';

export function parseCookieHeader(rawHeader: string): CookiePairItem[] {
  const trimmed = (rawHeader ?? '').trim();
  if (!trimmed) return [];

  const items: CookiePairItem[] = [];
  for (const part of trimmed.split(';')) {
    const seg = part.trim();
    if (!seg) continue;
    const eqIdx = seg.indexOf('=');
    if (eqIdx < 0) {
      items.push({
        id: createId('ck'),
        name: seg,
        value: '',
        isEnabled: true,
      });
    } else {
      items.push({
        id: createId('ck'),
        name: seg.slice(0, eqIdx).trim(),
        value: seg.slice(eqIdx + 1).trim(),
        isEnabled: true,
      });
    }
  }
  return items;
}

export function serializeCookies(items: CookiePairItem[]): string {
  return items
    .filter((i) => i.isEnabled && i.name.trim().length > 0)
    .map((i) => `${i.name.trim()}=${i.value ?? ''}`)
    .join('; ');
}
