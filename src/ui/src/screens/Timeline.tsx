import { useEffect, useMemo, useState } from 'react';
import { api } from '../api';
import { useLoad } from '../hooks';
import { COLOR, RANGES, appLabel, bytes, dayTime, pct } from '../format';
import { Plot } from '../components/Plot';
import { Heatmap } from '../components/Heatmap';

type Metric = 'cpu' | 'mem' | 'net';

const METRICS: { key: Metric; label: string; color: string; fmt: (v: number) => string }[] = [
  { key: 'cpu', label: 'Процессор', color: COLOR.cpu, fmt: v => pct(v) },
  { key: 'mem', label: 'Память', color: COLOR.mem, fmt: v => bytes(v) },
  { key: 'net', label: 'Сеть', color: COLOR.net, fmt: v => `${bytes(v)} за минуту` },
];

export function Timeline({ range, setRange, onApp }: { range: number; setRange: (m: number) => void; onApp: (n: string) => void }) {
  const [metric, setMetric] = useState<Metric>('cpu');
  const [picked, setPicked] = useState<number | null>(null);
  const tl = useLoad(() => api.timeline(range), [range], 60_000);
  const m = METRICS.find(x => x.key === metric)!;

  const data = tl.data;
  useEffect(() => { setPicked(null); }, [range]);

  const sysSeries = useMemo(() => data ? [
    { label: 'Процессор', color: COLOR.cpu, values: data.system.cpu, fill: true },
    { label: 'Память', color: COLOR.mem, values: data.system.mem },
  ] : [], [data]);

  const rows = useMemo(() => {
    if (!data) return [];
    return data.apps
      .map(a => ({ name: a.name, values: a[metric], total: a[metric].reduce((s, v) => s + v, 0) }))
      .filter(r => r.total > 0)
      .sort((a, b) => b.total - a.total)
      .slice(0, 16);
  }, [data, metric]);

  const idx = data && picked != null ? data.minutes.indexOf(picked) : -1;
  const snapshot = useMemo(() => {
    if (!data || idx < 0) return null;
    return data.apps
      .map(a => ({ name: a.name, cpu: a.cpu[idx], mem: a.mem[idx], net: a.net[idx] }))
      .sort((a, b) => b.cpu - a.cpu)
      .slice(0, 8);
  }, [data, idx]);

  return (
    <>
      <div className="head">
        <div>
          <h1>Таймлайн</h1>
          <p>Кто и когда нагружал систему. Нажми на минуту на графике или на карте, чтобы увидеть снимок того момента.</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label="Период">
            {RANGES.map(r => (
              <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>
            ))}
          </div>
        </div>
      </div>

      {tl.error && <div className="error">Не удалось загрузить историю: {tl.error}</div>}

      <section className="block">
        <h2>Вся система <small>процессор и занятая память, % по минутам</small></h2>
        <div className="panel chart-box">
          {data && data.minutes.length > 1
            ? <Plot ts={data.minutes} series={sysSeries} height={170} yMax={100} yFormat={v => `${v}%`} onPick={setPicked} />
            : <div className="empty">{tl.loading ? 'Загружаю…' : 'За этот период агент ещё ничего не записал.'}</div>}
        </div>
      </section>

      <div className="grid-2">
        <section className="block">
          <h2>
            Программы по минутам
            <span className="seg" role="group" aria-label="Показатель" style={{ marginLeft: 'auto' }}>
              {METRICS.map(x => (
                <button key={x.key} aria-pressed={metric === x.key} onClick={() => setMetric(x.key)}>{x.label}</button>
              ))}
            </span>
          </h2>
          <div className="panel" style={{ padding: 12 }}>
            {data && rows.length
              ? <Heatmap rows={rows} minutes={data.minutes} color={m.color} selected={picked} format={m.fmt} onPick={setPicked} onApp={onApp} />
              : <div className="empty">Нет данных за период</div>}
          </div>
        </section>

        <section className="block">
          <h2>Снимок момента</h2>
          <div className="panel snapshot">
            {!snapshot || picked == null ? (
              <div className="empty">Выбери минуту на графике или на карте слева.</div>
            ) : (
              <>
                <h3>{dayTime(picked)} <span>
                  процессор {pct(data!.system.cpu[idx], 0)}, память {pct(data!.system.mem[idx], 0)}
                </span></h3>
                <table className="table">
                  <thead><tr><th>Программа</th><th>Процессор</th><th>Память</th><th>Сеть</th></tr></thead>
                  <tbody>
                    {snapshot.map(s => (
                      <tr key={s.name} onClick={() => onApp(s.name)}>
                        <td><span className="name">{appLabel(s.name)}</span></td>
                        <td style={{ color: COLOR.cpu }}>{pct(s.cpu)}</td>
                        <td>{bytes(s.mem)}</td>
                        <td className="dim">{s.net ? bytes(s.net) : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}
          </div>
        </section>
      </div>
    </>
  );
}
