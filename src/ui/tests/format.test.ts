import { beforeAll, describe, expect, it, vi } from 'vitest';

// format.ts берёт язык при загрузке модуля — фиксируем русский до импорта.
let f: typeof import('../src/format');
beforeAll(async () => {
  vi.stubGlobal('localStorage', { getItem: () => 'ru', setItem: () => {} });
  vi.resetModules();
  f = await import('../src/format');
});

describe('bytes()', () => {
  it('scales by 1024 and rounds', () => {
    expect(f.bytes(0)).toBe('0 Б');
    expect(f.bytes(1536)).toBe('1,5 КБ');
    expect(f.bytes(150 * 1024 * 1024)).toBe('150 МБ');
    expect(f.bytes(12.66 * 1024 ** 3)).toBe('12,7 ГБ');
  });
  it('shows a dash for missing values', () => {
    expect(f.bytes(null)).toBe('—');
    expect(f.bytes(NaN)).toBe('—');
  });
});

describe('pct()', () => {
  it('formats and marks tiny values', () => {
    expect(f.pct(12.345)).toBe('12,3%');
    expect(f.pct(50, 0)).toBe('50%');
    expect(f.pct(0.04)).toBe('<0,1%');
    expect(f.pct(undefined)).toBe('—');
  });
});

describe('ago()', () => {
  it('describes elapsed time', () => {
    const now = Date.now() / 1000;
    expect(f.ago(null)).toBe('никогда');
    expect(f.ago(now - 5)).toBe('5 с назад');
    expect(f.ago(now - 7200)).toBe('2 ч назад');
    expect(f.ago(now - 86400 * 1.5)).toBe('вчера');
    expect(f.ago(now - 86400 * 4)).toBe('4 дн. назад');
  });
});

describe('plural()', () => {
  it('puts the number before the right word form', () => {
    expect(f.plural(2, 'файл', 'файла', 'файлов')).toBe('2 файла');
  });
});
