import { useEffect, useMemo, useState } from 'react';
import { Plot } from './Plot';
import { COLOR, bytes, pct, plural, rate } from '../format';
import type { Live } from '../api';
import { t } from '../i18n';

export interface LiveHistory {
  ts: number[];
  cpu: number[];
  mem: number[];
  disk: number[];
  net: number[];
  /** Загрузка каждой видеокарты по имени, выровнена с ts. */
  gpu: Record<string, number[]>;
}

export const HISTORY = 120; // 2 минуты по 1 секунде

export function pushLive(h: LiveHistory, l: Live): LiveHistory {
  const cut = <T,>(a: T[], v: T) => [...a, v].slice(-HISTORY);
  return {
    ts: cut(h.ts, l.ts),
    cpu: cut(h.cpu, l.cpu),
    mem: cut(h.mem, (l.memUsed / l.memTotal) * 100),
    disk: cut(h.disk, l.diskRead + l.diskWrite),
    net: cut(h.net, l.netRx + l.netTx),
    gpu: Object.fromEntries((l.gpus ?? []).map(g => [g.name, [
      ...Array(Math.max(0, Math.min(HISTORY - 1, h.ts.length) - (h.gpu[g.name]?.length ?? 0))).fill(0),
      ...(h.gpu[g.name] ?? []),
      g.util,
    ].slice(-HISTORY)])),
  };
}

export const emptyHistory = (): LiveHistory => ({ ts: [], cpu: [], mem: [], disk: [], net: [], gpu: {} });

interface ScopeProps {
  name: string;
  value: string;
  sub: string;
  color: string;
  ts: number[];
  values: number[];
  yMax?: number;
  /** Несколько линий вместо одной: видеокарты. */
  multi?: { label: string; color: string; values: number[] }[];
  href?: string;
}

/** Низкое или узкое окно — графики в полосе ниже (те же условия, что в styles.css). */
const SMALL = '(max-height: 820px), (max-width: 1250px)';
function useSmall() {
  const [small, setSmall] = useState(() => typeof matchMedia === 'function' && matchMedia(SMALL).matches);
  useEffect(() => {
    if (typeof matchMedia !== 'function') return;
    const m = matchMedia(SMALL);
    const on = () => setSmall(m.matches);
    m.addEventListener('change', on);
    return () => m.removeEventListener('change', on);
  }, []);
  return small;
}

function Scope({ name, value, sub, color, ts, values, yMax, multi, href }: ScopeProps) {
  const small = useSmall();
  const series = useMemo(
    () => multi?.length ? multi.map((m, i) => ({ ...m, fill: i === 0 })) : [{ label: name, color, values, fill: true }],
    [name, color, values, multi]);
  const Tag = href ? 'a' : 'div';
  return (
    <Tag className={`scope${href ? ' link-scope' : ''}`} {...(href ? { href, title: t('Открыть раздел «Видеокарта»') } : {})}>
      <div className="scope-read">
        <div>
          <div className="scope-name">{name}</div>
          <div className="scope-sub">{sub}</div>
        </div>
        <span className="scope-value" style={{ color }}>{value}</span>
      </div>
      <div className="scope-plot">
        {ts.length > 1 && <Plot ts={ts} series={series} height={small ? 54 : 76} axes={false} glow yMax={yMax} />}
      </div>
    </Tag>
  );
}

export const GPU_COLORS = [COLOR.gpu, '#f5a3d0', '#9cc4ff', COLOR.gpu2];
/** «NVIDIA GeForce RTX 4060 Laptop GPU» → «RTX 4060», «Intel(R) UHD Graphics 620» → «Intel UHD 620». */
export function shortGpu(name: string) {
  const intel = /intel/i.test(name);
  const n = name
    .replace(/\((R|TM)\)/gi, ' ')
    .replace(/\b(NVIDIA|GeForce|AMD|Intel|Laptop|GPU|Graphics|Corporation)\b/gi, ' ')
    .replace(/\s+/g, ' ').trim();
  const s = n ? (intel ? `Intel ${n}` : n) : name.trim();
  return s.length > 22 ? s.slice(0, 21) + '…' : s;
}

/** Верхняя полоса: живые графики за последние 2 минуты; видеокарта — если Windows отдаёт её счётчики. */
export function Scopes({ live, hist }: { live: Live | null; hist: LiveHistory }) {
  const peak = (a: number[]) => Math.max(0, ...a);
  const gpus = live?.gpus ?? [];
  const gpuMulti = useMemo(() => gpus.map((g, i) => ({ label: g.name, color: GPU_COLORS[i % GPU_COLORS.length], values: hist.gpu[g.name] ?? [] })),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [hist]);
  const top = gpus.reduce((m, g) => Math.max(m, g.util), 0);
  return (
    <div className={`scopes${gpus.length ? ' five' : ''}`}>
      <Scope name={t('Процессор')} color={COLOR.cpu} ts={hist.ts} values={hist.cpu} yMax={100}
        value={live ? pct(live.cpu, 0) : '—'}
        sub={live ? plural(live.apps.reduce((s, a) => s + a.count, 0), 'процесс', 'процесса', 'процессов') : ''} />
      <Scope name={t('Память')} color={COLOR.mem} ts={hist.ts} values={hist.mem} yMax={100}
        value={live ? pct((live.memUsed / live.memTotal) * 100, 0) : '—'}
        sub={live ? t('{0} из {1}', bytes(live.memUsed), bytes(live.memTotal)) : ''} />
      <Scope name={t('Диск')} color={COLOR.disk} ts={hist.ts} values={hist.disk}
        value={live ? rate(live.diskRead + live.diskWrite) : '—'}
        sub={live ? t('пик за 2 мин {0}', rate(peak(hist.disk))) : ''} />
      <Scope name={t('Сеть')} color={COLOR.net} ts={hist.ts} values={hist.net}
        value={live ? rate(live.netRx + live.netTx) : '—'}
        sub={live ? `↓ ${rate(live.netRx)}  ↑ ${rate(live.netTx)}` : ''} />
      {gpus.length > 0 && (
        <Scope name={gpus.length > 1 ? t('Видеокарты') : t('Видеокарта')} color={COLOR.gpu} ts={hist.ts} values={[]} yMax={100} multi={gpuMulti} href="#gpu"
          value={pct(top, 0)}
          sub={gpus.length > 1
            ? gpus.map(g => `${shortGpu(g.name)} ${Math.round(g.util)}%`).join(' · ')
            : t('{0} · память {1}', shortGpu(gpus[0].name), bytes(gpus[0].memUsed))} />
      )}
    </div>
  );
}
