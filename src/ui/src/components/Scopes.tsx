import { useMemo } from 'react';
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
  };
}

export const emptyHistory = (): LiveHistory => ({ ts: [], cpu: [], mem: [], disk: [], net: [] });

interface ScopeProps {
  name: string;
  value: string;
  sub: string;
  color: string;
  ts: number[];
  values: number[];
  yMax?: number;
}

function Scope({ name, value, sub, color, ts, values, yMax }: ScopeProps) {
  const series = useMemo(() => [{ label: name, color, values, fill: true }], [name, color, values]);
  return (
    <div className="scope">
      <div className="scope-read">
        <div>
          <div className="scope-name">{name}</div>
          <div className="scope-sub">{sub}</div>
        </div>
        <span className="scope-value" style={{ color }}>{value}</span>
      </div>
      <div className="scope-plot">
        {ts.length > 1 && <Plot ts={ts} series={series} height={76} axes={false} glow yMax={yMax} />}
      </div>
    </div>
  );
}

/** Верхняя полоса: четыре живых графика за последние 2 минуты. */
export function Scopes({ live, hist }: { live: Live | null; hist: LiveHistory }) {
  const peak = (a: number[]) => Math.max(0, ...a);
  return (
    <div className="scopes">
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
    </div>
  );
}
