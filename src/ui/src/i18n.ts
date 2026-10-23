import en from './i18n/en.json';
import es from './i18n/es.json';
import fr from './i18n/fr.json';

/**
 * Перевод интерфейса. Ключ — сама русская строка (исходный язык), переводы — в i18n/{en,es,fr}.json.
 * Эти же файлы читает C#-часть для текстов, которые формирует программа. Чего нет в словаре — показывается по-русски.
 *
 *   t('Удалено файлов: {0}', n)            подстановки {0}, {1}…
 *   tp(n, 'файл', 'файла', 'файлов')       слово в нужной форме числа (без самого числа)
 */
export type Lang = 'ru' | 'en' | 'es' | 'fr';

export const LANGS: { code: Lang; label: string }[] = [
  { code: 'ru', label: 'Русский' },
  { code: 'en', label: 'English' },
  { code: 'es', label: 'Español' },
  { code: 'fr', label: 'Français' },
];

const DICTS: Record<Lang, Record<string, string>> = { ru: {}, en, es, fr };
const LOCALES: Record<Lang, string> = { ru: 'ru-RU', en: 'en-US', es: 'es-ES', fr: 'fr-FR' };

function detect(): Lang {
  try {
    const saved = localStorage.getItem('syswatch.lang') as Lang | null;
    if (saved && saved in DICTS) return saved;
  } catch { /* хранилище недоступно */ }
  const nav = (navigator.language || 'ru').slice(0, 2) as Lang;
  return nav in DICTS ? nav : 'en';
}

let current: Lang = detect();

export const lang = () => current;
/** Тег локали для Intl (даты, числа, названия стран). */
export const locale = () => LOCALES[current];

export function setLang(l: Lang) {
  current = l;
  try { localStorage.setItem('syswatch.lang', l); } catch { /* ничего */ }
}

const fill = (s: string, args: (string | number | null | undefined)[]) =>
  args.length ? s.replace(/\{(\d+)\}/g, (m, i) => (args[+i] == null ? m : String(args[+i]))) : s;

export function t(ru: string, ...args: (string | number | null | undefined)[]): string {
  const s = current === 'ru' ? ru : DICTS[current][ru] ?? ru;
  return fill(s, args);
}

const ruRules = new Intl.PluralRules('ru-RU');

/** Слово в форме, нужной для числа n. Ключ словаря — «one|few|many» по-русски, значение — «one|other». */
export function tp(n: number, one: string, few: string, many: string): string {
  if (current === 'ru') {
    const c = ruRules.select(n);
    return c === 'one' ? one : c === 'few' ? few : many;
  }
  const v = DICTS[current][`${one}|${few}|${many}`];
  if (!v) return many;
  const [o, other] = v.split('|');
  return new Intl.PluralRules(LOCALES[current]).select(n) === 'one' ? o : other ?? o;
}
