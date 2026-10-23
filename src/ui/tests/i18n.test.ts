import { afterEach, describe, expect, it } from 'vitest';
import { setLang, t, tp } from '../src/i18n';

afterEach(() => setLang('ru'));

describe('t()', () => {
  it('returns the Russian source and fills placeholders', () => {
    setLang('ru');
    expect(t('Сохранено: {0}', 'a.html')).toBe('Сохранено: a.html');
  });

  it('translates and keeps unknown placeholders', () => {
    setLang('en');
    expect(t('Сохранено: {0}', 'a.html')).toBe('Saved: a.html');
    expect(t('Сохранено: {0}')).toBe('Saved: {0}');
  });

  it('falls back to Russian for a missing key', () => {
    setLang('fr');
    expect(t('строки нет в словаре')).toBe('строки нет в словаре');
  });
});

describe('tp()', () => {
  it('picks Russian forms', () => {
    setLang('ru');
    expect([1, 3, 5, 21, 11].map(n => tp(n, 'файл', 'файла', 'файлов'))).toEqual(['файл', 'файла', 'файлов', 'файл', 'файлов']);
  });

  it('picks English one/other', () => {
    setLang('en');
    expect([1, 2, 5].map(n => tp(n, 'файл', 'файла', 'файлов'))).toEqual(['file', 'files', 'files']);
  });

  it('uses French rules (0 and 1 are singular)', () => {
    setLang('fr');
    const [one, other] = (tp(1, 'файл', 'файла', 'файлов') + '|' + tp(2, 'файл', 'файла', 'файлов')).split('|');
    expect(one).not.toBe(other);
    expect(tp(0, 'файл', 'файла', 'файлов')).toBe(one);
  });
});
