import { lang, locale, t, tp } from './i18n';

// Язык меняется только с перезагрузкой окна, поэтому форматтеры можно создать один раз при загрузке.
const UNITS = [t('Б'), t('КБ'), t('МБ'), t('ГБ'), t('ТБ')];
const num = (v: number, d: number) => v.toLocaleString(locale(), { minimumFractionDigits: d, maximumFractionDigits: d, useGrouping: false });

export function bytes(n: number | null | undefined, digits = 1): string {
  if (n == null || !isFinite(n)) return '—';
  let i = 0;
  let v = Math.abs(n);
  while (v >= 1024 && i < UNITS.length - 1) { v /= 1024; i++; }
  const d = i === 0 ? 0 : v >= 100 ? 0 : digits;
  return `${num(v, d)} ${UNITS[i]}`;
}

export const rate = (n: number | null | undefined) => (n == null ? '—' : t('{0}/с', bytes(n)));

export function pct(n: number | null | undefined, digits = 1): string {
  if (n == null || !isFinite(n)) return '—';
  if (n > 0 && n < 0.1) return `<${num(0.1, 1)}%`;
  return `${num(n, digits)}%`;
}

const timeFmt = new Intl.DateTimeFormat(locale(), { hour: '2-digit', minute: '2-digit' });
const timeSecFmt = new Intl.DateTimeFormat(locale(), { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const dayFmt = new Intl.DateTimeFormat(locale(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

export const time = (ts: number) => timeFmt.format(ts * 1000);
export const timeSec = (ts: number) => timeSecFmt.format(ts * 1000);
export const dayTime = (ts: number) => dayFmt.format(ts * 1000);

export function ago(ts: number | null | undefined): string {
  if (ts == null) return t('никогда');
  const s = Math.max(0, Math.floor(Date.now() / 1000 - ts));
  if (s < 60) return t('{0} с назад', s);
  if (s < 3600) return t('{0} мин назад', Math.floor(s / 60));
  if (s < 86400) return t('{0} ч назад', Math.floor(s / 3600));
  const d = Math.floor(s / 86400);
  return d === 1 ? t('вчера') : t('{0} дн. назад', d);
}

/** Имя программы без .exe — короче в таблицах. */
export const appLabel = (name: string) => name.replace(/\.exe$/i, '');

export const RANGES = [
  { minutes: 60, label: t('Час') },
  { minutes: 360, label: t('6 часов') },
  { minutes: 1440, label: t('Сутки') },
  { minutes: 10080, label: t('Неделя') },
] as const;

/** Цвет ресурса одинаков на всех экранах: это часть языка интерфейса. */
export const COLOR = {
  cpu: '#F2B544',
  mem: '#A78BFA',
  disk: '#F27C6B',
  net: '#4FC3D9',
  netTx: '#2E8FA3',
  gpu: '#7EE0A1',
  gpu2: '#3E9E68',
  ok: '#6BCF8E',
  warn: '#F27C6B',
} as const;

/** Число со словом в нужной форме на текущем языке: plural(5, 'домен', 'домена', 'доменов') → «5 доменов» / «5 domains». */
export function plural(n: number, one: string, few: string, many: string): string {
  return `${n} ${tp(n, one, few, many)}`;
}

/** Только слово в нужной форме, без числа: для подписей под крупной цифрой. */
export const pluralWord = (n: number, one: string, few: string, many: string) => tp(n, one, few, many);

const regionNames = (() => {
  try { return new Intl.DisplayNames([lang()], { type: 'region' }); } catch { return null; }
})();

/** Название страны по ISO-коду; LAN — локальная сеть. */
export function countryName(code: string | null | undefined): string {
  if (!code) return '';
  if (code === 'LAN') return t('локальная сеть');
  if (code === 'ZZ') return '';
  try { return regionNames?.of(code) ?? code; } catch { return code; }
}
