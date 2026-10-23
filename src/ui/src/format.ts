const UNITS = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];

export function bytes(n: number | null | undefined, digits = 1): string {
  if (n == null || !isFinite(n)) return '—';
  let i = 0;
  let v = Math.abs(n);
  while (v >= 1024 && i < UNITS.length - 1) { v /= 1024; i++; }
  const d = i === 0 ? 0 : v >= 100 ? 0 : digits;
  return `${v.toFixed(d).replace('.', ',')} ${UNITS[i]}`;
}

export const rate = (n: number | null | undefined) => (n == null ? '—' : `${bytes(n)}/с`);

export function pct(n: number | null | undefined, digits = 1): string {
  if (n == null || !isFinite(n)) return '—';
  if (n > 0 && n < 0.1) return '<0,1%';
  return `${n.toFixed(digits).replace('.', ',')}%`;
}

const timeFmt = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });
const timeSecFmt = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const dayFmt = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

export const time = (ts: number) => timeFmt.format(ts * 1000);
export const timeSec = (ts: number) => timeSecFmt.format(ts * 1000);
export const dayTime = (ts: number) => dayFmt.format(ts * 1000);

export function ago(ts: number | null | undefined): string {
  if (ts == null) return 'никогда';
  const s = Math.max(0, Math.floor(Date.now() / 1000 - ts));
  if (s < 60) return `${s} с назад`;
  if (s < 3600) return `${Math.floor(s / 60)} мин назад`;
  if (s < 86400) return `${Math.floor(s / 3600)} ч назад`;
  const d = Math.floor(s / 86400);
  return d === 1 ? 'вчера' : `${d} дн. назад`;
}

/** Имя программы без .exe — короче в таблицах. */
export const appLabel = (name: string) => name.replace(/\.exe$/i, '');

export const RANGES = [
  { minutes: 60, label: 'Час' },
  { minutes: 360, label: '6 часов' },
  { minutes: 1440, label: 'Сутки' },
  { minutes: 10080, label: 'Неделя' },
] as const;

/** Цвет ресурса одинаков на всех экранах: это часть языка интерфейса. */
export const COLOR = {
  cpu: '#F2B544',
  mem: '#A78BFA',
  disk: '#F27C6B',
  net: '#4FC3D9',
  netTx: '#2E8FA3',
  ok: '#6BCF8E',
  warn: '#F27C6B',
} as const;

/** Русское склонение: plural(5, 'домен', 'домена', 'доменов'). */
export function plural(n: number, one: string, few: string, many: string): string {
  const a = Math.abs(n) % 100, b = a % 10;
  const w = a > 10 && a < 20 ? many : b === 1 ? one : b >= 2 && b <= 4 ? few : many;
  return `${n} ${w}`;
}

const regionNames = (() => {
  try { return new Intl.DisplayNames(['ru'], { type: 'region' }); } catch { return null; }
})();

/** Название страны по ISO-коду; LAN — локальная сеть. */
export function countryName(code: string | null | undefined): string {
  if (!code) return '';
  if (code === 'LAN') return 'локальная сеть';
  if (code === 'ZZ') return '';
  try { return regionNames?.of(code) ?? code; } catch { return code; }
}
