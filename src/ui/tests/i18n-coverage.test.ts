/**
 * Все тексты интерфейса и программы переведены: ключи t('…'), tp(n,'…','…','…') в UI
 * и L.T/L.K("…"), L.P(n,"…","…","…") в C# есть в en/es/fr, плейсхолдеры {0} / {0:F0} совпадают,
 * в переводе нет кириллицы, у форм числа ровно две части («one|other»).
 */
import { describe, expect, it } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import en from '../src/i18n/en.json';
import es from '../src/i18n/es.json';
import fr from '../src/i18n/fr.json';

const ROOT = path.resolve(__dirname, '..', '..');
const CYR = /[А-Яа-яЁё]/;
const SQ = String.raw`'((?:[^'\\]|\\.)*)'`;
const DQ = String.raw`"((?:[^"\\]|\\.)*)"`;
const STR = `(?:${SQ}|${DQ})`;

function walk(dir: string, ext: string[]): string[] {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) return e.name === 'node_modules' || e.name === 'bin' || e.name === 'obj' ? [] : walk(p, ext);
    return ext.some(x => e.name.endsWith(x)) && !e.name.endsWith('.test.ts') ? [p] : [];
  });
}
const unq = (s: string) => s.replace(/\\(.)/g, (_, c: string) => (c === 'n' ? '\n' : c === 't' ? '\t' : c));

function collect() {
  const keys = new Map<string, string>(), plurals = new Map<string, string>();
  const add = (m: Map<string, string>, k: string, f: string) => { if (CYR.test(k) && !m.has(k)) m.set(k, path.basename(f)); };
  for (const f of walk(path.join(ROOT, 'ui', 'src'), ['.ts', '.tsx'])) {
    const s = fs.readFileSync(f, 'utf8');
    for (const m of s.matchAll(new RegExp(String.raw`\bt\(\s*` + STR, 'g'))) add(keys, unq(m[1] ?? m[2]), f);
    for (const m of s.matchAll(new RegExp(String.raw`\b(?:tp|plural|pluralWord)\([^;]*?` + STR + String.raw`\s*,\s*` + STR + String.raw`\s*,\s*` + STR, 'g')))
      add(plurals, [1, 3, 5].map(i => unq(m[i] ?? m[i + 1])).join('|'), f);
  }
  for (const f of walk(path.join(ROOT, 'SysWatch.App'), ['.cs'])) {
    const s = fs.readFileSync(f, 'utf8');
    for (const m of s.matchAll(new RegExp(String.raw`\bL\.[TK]\(\s*` + DQ, 'g'))) add(keys, unq(m[1]), f);
    for (const m of s.matchAll(new RegExp(String.raw`\bL\.P\(\s*[^,]+?,\s*` + DQ + String.raw`\s*,\s*` + DQ + String.raw`\s*,\s*` + DQ, 'g')))
      add(plurals, [m[1], m[2], m[3]].join('|'), f);
  }
  return { keys, plurals };
}

const ph = (s: string) => (s.match(/\{\d+(?::[^}]*)?\}/g) ?? []).sort().join(',');
const { keys, plurals } = collect();

describe('i18n coverage', () => {
  it('finds the source strings', () => {
    expect(keys.size).toBeGreaterThan(500);
  });

  for (const [code, dict] of Object.entries({ en, es, fr }) as [string, Record<string, string>][]) {
    it(`${code}: every string is translated`, () => {
      const missing = [...keys].filter(([k]) => !(k in dict)).map(([k, f]) => `${f}: ${k}`);
      const missingPlurals = [...plurals].filter(([k]) => !(k in dict)).map(([k, f]) => `${f}: ${k}`);
      expect([...missing, ...missingPlurals]).toEqual([]);
    });

    it(`${code}: placeholders match and no Cyrillic is left`, () => {
      const bad = [...keys.keys()].filter(k => k in dict && (ph(k) !== ph(dict[k]) || CYR.test(dict[k])))
        .map(k => `${k} -> ${dict[k]}`);
      expect(bad).toEqual([]);
    });

    it(`${code}: plural forms are "one|other"`, () => {
      const bad = [...plurals.keys()].filter(k => k in dict && dict[k].split('|').length !== 2);
      expect(bad).toEqual([]);
    });
  }

  it('en, es and fr have the same keys', () => {
    const k = (d: object) => Object.keys(d).sort();
    expect(k(es)).toEqual(k(en));
    expect(k(fr)).toEqual(k(en));
  });
});
