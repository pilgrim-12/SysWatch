import { useMemo, useState } from 'react';
import { api, type AgentParts, type Live } from '../api';
import { useLoad, useSort } from '../hooks';
import { COLOR, appLabel, bytes, pct, plural, rate } from '../format';
import { Plot } from '../components/Plot';

type Key = 'name' | 'cpu' | 'mem' | 'io';

function Bar({ value, max, color, children }: { value: number; max: number; color: string; children: React.ReactNode }) {
  const w = max > 0 ? Math.max(2, Math.min(100, (value / max) * 100)) : 0;
  return (
    <span className="cell-bar" style={{ color }}>
      <span style={{ color: 'var(--paper)' }}>{children}</span>
      <i style={{ width: `${w}%` }} />
    </span>
  );
}

const PARTS: { key: keyof AgentParts; label: string; color: string }[] = [
  { key: 'kernel', label: 'события ядра', color: '#4fc3d9' },
  { key: 'dns', label: 'DNS', color: '#a78bfa' },
  { key: 'sampler', label: 'опрос процессов', color: '#f2b544' },
  { key: 'writer', label: 'запись на диск', color: '#f27c6b' },
  { key: 'other', label: '.NET: JIT, GC, таймеры', color: '#5c718c' },
];

/** Из чего складывается расход агента: видно, что оптимизировать. */
function CostParts({ parts }: { parts: AgentParts }) {
  const total = PARTS.reduce((s, p) => s + parts[p.key], 0);
  if (total <= 0) return null;
  return (
    <div className="cost-parts">
      <div className="cost-bar">
        {PARTS.map(p => <i key={p.key} style={{ width: `${(parts[p.key] / total) * 100}%`, background: p.color }} title={`${p.label}: ${pct(parts[p.key], 2)}`} />)}
      </div>
      <div className="cost-legend">
        {PARTS.map(p => (
          <span key={p.key}><i style={{ background: p.color }} />{p.label} {pct(parts[p.key], 2)}</span>
        ))}
      </div>
    </div>
  );
}

export function Now({ live, onApp }: { live: Live | null; onApp: (name: string) => void }) {
  const [q, setQ] = useState('');
  const { sort, thProps } = useSort<Key>('cpu');
  const hour = useLoad(() => api.overview(60), [], 30_000);

  const rows = useMemo(() => {
    const apps = (live?.apps ?? []).filter(a => !q || a.name.toLowerCase().includes(q.toLowerCase()));
    return sort(apps, (a, k) => (k === 'name' ? a.name : k === 'io' ? a.ioRead + a.ioWrite : a[k])).slice(0, 40);
  }, [live, q, sort]);

  const maxCpu = Math.max(1, ...rows.map(r => r.cpu));
  const maxMem = Math.max(1, ...rows.map(r => r.mem));
  const maxIo = Math.max(1, ...rows.map(r => r.ioRead + r.ioWrite));

  const ov = hour.data;
  const topCpu = ov ? [...ov.apps].sort((a, b) => b.cpuPct - a.cpuPct).slice(0, 6) : [];
  const topNet = ov ? [...ov.apps].sort((a, b) => b.netRx + b.netTx - a.netRx - a.netTx).filter(a => a.netRx + a.netTx > 0).slice(0, 6) : [];
  const agentSeries = useMemo(
    () => (ov ? [{ label: 'Агент', color: COLOR.ok, values: ov.agentCpu, fill: true }] : []),
    [ov],
  );

  return (
    <>
      <div className="head">
        <div>
          <h1>Сейчас</h1>
          <p>Что прямо сейчас нагружает компьютер. Процессы одной программы сложены в одну строку.</p>
        </div>
        <div className="head-tools">
          <input className="search" placeholder="Найти программу" value={q} onChange={e => setQ(e.target.value)} />
        </div>
      </div>

      <div className="grid-2">
        <section className="block">
          <div className="panel scroll" style={{ maxHeight: 'calc(100vh - 330px)', minHeight: 300 }}>
            {!live ? <div className="empty">Жду первые данные…</div> : (
              <table className="table">
                <thead>
                  <tr>
                    <th {...thProps('name')}>Программа</th>
                    <th {...thProps('cpu')}>Процессор</th>
                    <th {...thProps('mem')}>Память</th>
                    <th {...thProps('io')}>Диск</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map(a => (
                    <tr key={a.name} onClick={() => onApp(a.name)}>
                      <td>
                        <span className="name" title={a.name}>
                          {appLabel(a.name)}
                          {a.count > 1 && <span className="count">{a.count}</span>}
                        </span>
                      </td>
                      <td><Bar value={a.cpu} max={maxCpu} color={COLOR.cpu}>{pct(a.cpu)}</Bar></td>
                      <td><Bar value={a.mem} max={maxMem} color={COLOR.mem}>{bytes(a.mem)}</Bar></td>
                      <td><Bar value={a.ioRead + a.ioWrite} max={maxIo} color={COLOR.disk}>{rate(a.ioRead + a.ioWrite)}</Bar></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </section>

        <div>
          <section className="block">
            <h2>Больше всех грузили процессор <small>за час, в среднем</small></h2>
            <div className="panel">
              {topCpu.length === 0 ? <div className="empty">Агент ещё не накопил историю.</div> : (
                <table className="table"><tbody>
                  {topCpu.map(a => (
                    <tr key={a.name} onClick={() => onApp(a.name)}>
                      <td><span className="name">{appLabel(a.name)}</span></td>
                      <td><Bar value={a.cpuPct} max={topCpu[0].cpuPct} color={COLOR.cpu}>{pct(a.cpuPct)}</Bar></td>
                    </tr>
                  ))}
                </tbody></table>
              )}
            </div>
          </section>

          <section className="block">
            <h2>Больше всех работали с сетью <small>за час</small></h2>
            <div className="panel">
              {topNet.length === 0 ? <div className="empty">Сетевой активности не было.</div> : (
                <table className="table"><tbody>
                  {topNet.map(a => (
                    <tr key={a.name} onClick={() => onApp(a.name)}>
                      <td><span className="name">{appLabel(a.name)}</span></td>
                      <td className="dim">{a.domains ? plural(a.domains, 'домен', 'домена', 'доменов') : ''}</td>
                      <td><Bar value={a.netRx + a.netTx} max={topNet[0].netRx + topNet[0].netTx} color={COLOR.net}>{bytes(a.netRx + a.netTx)}</Bar></td>
                    </tr>
                  ))}
                </tbody></table>
              )}
            </div>
          </section>

          <section className="block">
            <h2>Сколько стоит мониторинг <small>процессор агента за час</small></h2>
            <div className="panel chart-box">
              <h3>
                {live?.agent.running ? 'Агент работает' : 'Агент не пишет данные'}
                <b style={{ color: COLOR.ok }}>
                  {live?.agent.cpu != null ? pct(live.agent.cpu, 2) : '—'}, {bytes(live?.agent.ram)}
                </b>
              </h3>
              {ov && ov.minutes.length > 1
                ? <Plot ts={ov.minutes} series={agentSeries} height={90} yFormat={v => pct(v, 1)} />
                : <div className="empty">Нет данных</div>}
              {ov?.agentParts && <CostParts parts={ov.agentParts} />}
            </div>
          </section>
        </div>
      </div>
    </>
  );
}
