import type { NumberBase } from '../types/apiTester';

export interface ParseNumberResult {
  ok: boolean;
  value: bigint;
  error?: string;
}

export function getBasePrefix(base: NumberBase): string {
  switch (base) {
    case 16:
      return '0x';
    case 2:
      return '0b';
    case 8:
      return '0o';
    default:
      return '';
  }
}

export function getBasePlaceholder(base: NumberBase, kind: 'start' | 'end' | 'min' | 'max'): string {
  if (base === 16) {
    return kind === 'start' || kind === 'min' ? '0' : 'FF';
  }
  if (base === 2) {
    return kind === 'start' || kind === 'min' ? '0000' : '1111';
  }
  if (base === 8) {
    return kind === 'start' || kind === 'min' ? '0' : '77';
  }
  return kind === 'start' || kind === 'min' ? '1' : kind === 'max' ? '100' : '10';
}

export function tryParseNumber(
  rawText: string,
  base: NumberBase,
  fieldLabel = 'Valeur'
): ParseNumberResult {
  const trimmed = (rawText ?? '').trim();
  if (!trimmed) {
    return { ok: false, value: 0n, error: `${fieldLabel} : champ vide.` };
  }

  let negative = false;
  let rest = trimmed;
  if (rest.startsWith('-')) {
    negative = true;
    rest = rest.slice(1).trim();
  } else if (rest.startsWith('+')) {
    rest = rest.slice(1).trim();
  }

  rest = rest.replace(/[_\s]/g, '');

  if (base === 16 && (rest.startsWith('0x') || rest.startsWith('0X'))) {
    rest = rest.slice(2);
  } else if (base === 2 && (rest.startsWith('0b') || rest.startsWith('0B'))) {
    rest = rest.slice(2);
  } else if (base === 8 && (rest.startsWith('0o') || rest.startsWith('0O'))) {
    rest = rest.slice(2);
  }

  if (!rest) {
    return { ok: false, value: 0n, error: `${fieldLabel} : valeur incomplète.` };
  }

  let value: bigint;
  try {
    if (base === 10) {
      if (!/^\d+$/.test(rest)) {
        return { ok: false, value: 0n, error: `${fieldLabel} : décimal invalide (« ${rawText} »).` };
      }
      value = BigInt(rest);
    } else if (base === 16) {
      if (!/^[0-9a-fA-F]+$/.test(rest)) {
        return { ok: false, value: 0n, error: `${fieldLabel} : hexadécimal invalide (« ${rawText} »).` };
      }
      value = BigInt(`0x${rest}`);
    } else if (base === 2) {
      if (!/^[01]+$/.test(rest)) {
        return { ok: false, value: 0n, error: `${fieldLabel} : binaire invalide (« ${rawText} »).` };
      }
      value = BigInt(`0b${rest}`);
    } else {
      if (!/^[0-7]+$/.test(rest)) {
        return { ok: false, value: 0n, error: `${fieldLabel} : octal invalide (« ${rawText} »).` };
      }
      value = BigInt(`0o${rest}`);
    }
  } catch {
    return { ok: false, value: 0n, error: `${fieldLabel} : format numérique invalide.` };
  }

  return { ok: true, value: negative ? -value : value };
}

export function formatNumber(
  value: bigint,
  base: NumberBase,
  width = 0,
  uppercaseHex = true,
  usePrefix = false
): string {
  const negative = value < 0n;
  const magnitude = negative ? -value : value;

  let digits = magnitude.toString(base);
  if (base === 16 && uppercaseHex) {
    digits = digits.toUpperCase();
  }

  if (width > 0 && digits.length < width) {
    digits = digits.padStart(width, '0');
  }

  let prefix = '';
  if (usePrefix) {
    if (base === 16) {
      prefix = uppercaseHex ? '0X' : '0x';
    } else if (base === 2) {
      prefix = '0b';
    } else if (base === 8) {
      prefix = '0o';
    }
  }

  return `${negative ? '-' : ''}${prefix}${digits}`;
}

export function tryComputeBigRangeCount(
  start: bigint,
  end: bigint,
  step: bigint,
  hardCap = 10000
): { ok: boolean; count: number; error?: string } {
  if (step === 0n) {
    return { ok: false, count: 0, error: 'Le pas ne peut pas être 0.' };
  }
  if (start <= end && step < 0n) {
    return { ok: false, count: 0, error: 'Le pas doit être positif lorsque Début <= Fin.' };
  }
  if (start > end && step > 0n) {
    return { ok: false, count: 0, error: 'Le pas doit être négatif lorsque Début > Fin.' };
  }

  const diff = step > 0n ? end - start : start - end;
  const absStep = step > 0n ? step : -step;
  const countBig = diff / absStep + 1n;

  if (hardCap > 0 && countBig > BigInt(hardCap + 1)) {
    return {
      ok: true,
      count: hardCap + 1,
    };
  }

  const countNum = Number(countBig);
  return {
    ok: true,
    count: Number.isFinite(countNum) ? countNum : hardCap + 1,
  };
}
