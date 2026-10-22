import { useMemo, useState } from 'react';
import { api, type SysEvent } from '../api';
import { useLoad } from '../hooks';
import { RANGES, appLabel, timeSec } from '../format';

const KINDS: { key: SysEvent['kind'] | 'all'; label: string }[] = [
  { key: 'all', label: 'Все' },
  { key: 'start', label: 'Запуски' },
  { key: 'stop', label: 'Завершения' },
  { key: 'dns', label: 'DNS-запросы' },
];

const KIND_LABEL: Record<SysEvent['kind'], string> = { start: 'запуск', stop: 'завершение', dns: 'DNS' };

export function Events({ range, setRange, onApp }: { range: number; setRange: (m: number) => void; onApp: (n: string) => void }) {
  const ev = useLoad(() => api.events(range), [range], 30_000);
  const [kind, setKind] = useState<(typeof KINDS)[number]['key']>('all');
  const [q, setQ] = useState('');

  // Одинаковые события подряд (та же программа, тот же тип и адрес) — одной строкой со счётчиком.
  const rows = useMemo(() => {
    const filtered = (ev.data ?? [])
      .filter(e => kind === 'all' || e.kind === kind)
      .filter(e => !q || e.app.toLowerCase().includes(q.toLowerCase()) || (e.detail ?? '').toLowerCase().includes(q.toLowerCase()));
    const out: (SysEvent & { count: number; first: number })[] = [];
    for (const e of filtered) {
      const last = out[out.length - 1];
      if (last && last.kind === e.kind && last.app === e.app && last.detail === e.detail && e.kind !== 'start') {
        last.count++;
        last.first = e.ts;
      } else {
        out.push({ ...e, count: 1, first: e.ts });
      }
      if (out.length >= 500) break;
    }
    return out;
  }, [ev.data, kind, q]);

  return (
    <>
      <div className="head">
        <div>
          <h1>События</h1>
          <p>Запуски и завершения процессов, DNS-запросы. Видны даже процессы, которые прожили меньше секунды.</p>
        </div>
        <div className="head-tools">
          <input className="search" placeholder="Программа или домен" value={q} onChange={e => setQ(e.target.value)} />
          <div className="seg" role="group" aria-label="Период">
            {RANGES.slice(0, 3).map(r => (
              <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>
            ))}
          </div>
        </div>
      </div>

      <div className="filters" style={{ marginBottom: 12 }}>
        <div className="seg" role="group" aria-label="Тип события">
          {KINDS.map(k => <button key={k.key} aria-pressed={kind === k.key} onClick={() => setKind(k.key)}>{k.label}</button>)}
        </div>
        <span className="dim" style={{ color: 'var(--muted)' }}>{rows.length === 500 ? 'показаны последние 500' : `${rows.length}`}</span>
      </div>

      {ev.error && <div className="error">Не удалось загрузить события: {ev.error}</div>}

      <div className="panel">
        {!rows.length ? <div className="empty">{ev.loading ? 'Загружаю…' : 'Событий нет.'}</div> : rows.map((e, i) => (
          <div className="ev" key={i}>
            <time title={e.count > 1 ? `${timeSec(e.first)}–${timeSec(e.ts)}` : undefined}>{timeSec(e.ts)}</time>
            <span className={`ev-kind ${e.kind}`}>{KIND_LABEL[e.kind]}</span>
            <span className="flow-app" title={`${e.app}, PID ${e.pid}`} onClick={() => onApp(e.app)}>{appLabel(e.app)}</span>
            <span className="detail" title={e.detail ?? ''}>
              {e.detail ?? ''}
              {e.count > 1 && <span className="repeat"> ×{e.count}, с {timeSec(e.first)}</span>}
            </span>
          </div>
        ))}
      </div>
    </>
  );
}
