import { useMemo, useState } from 'react';
import { api } from '../api';
import { useLoad, useSort } from '../hooks';
import { RANGES, appLabel, bytes, dayTime } from '../format';

type Key = 'domain' | 'rx' | 'tx' | 'apps' | 'firstSeen';

export function Network({ range, setRange, onApp }: { range: number; setRange: (m: number) => void; onApp: (n: string) => void }) {
  const net = useLoad(() => api.network(range), [range], 60_000);
  const [q, setQ] = useState('');
  const { sort, thProps } = useSort<Key>('rx');
  const d = net.data;

  const match = (s: string) => !q || s.toLowerCase().includes(q.toLowerCase());

  const flows = useMemo(() => (d?.flows ?? []).filter(f => match(f.app) || match(f.domain)).slice(0, 30), [d, q]); // eslint-disable-line react-hooks/exhaustive-deps
  const maxFlow = Math.max(1, ...flows.map(f => f.rx + f.tx));

  const domains = useMemo(() => sort(
    (d?.domains ?? []).filter(r => match(r.domain) || r.apps.some(match)),
    (r, k) => (k === 'domain' ? r.domain : k === 'apps' ? r.apps.length : r[k]),
  ), [d, q, sort]); // eslint-disable-line react-hooks/exhaustive-deps

  const newCount = d?.domains.filter(x => x.newToday).length ?? 0;

  return (
    <>
      <div className="head">
        <div>
          <h1>Сеть</h1>
          <p>Какие программы с какими адресами обменивались данными. Домен берётся из DNS-запроса программы; если его нет, показан IP.</p>
        </div>
        <div className="head-tools">
          <input className="search" placeholder="Программа или домен" value={q} onChange={e => setQ(e.target.value)} />
          <div className="seg" role="group" aria-label="Период">
            {RANGES.map(r => (
              <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>
            ))}
          </div>
        </div>
      </div>

      {net.error && <div className="error">Не удалось загрузить сеть: {net.error}</div>}

      {d && (
        <div className="facts">
          <div className="fact"><b style={{ color: 'var(--net)' }}>{bytes(d.totals.rx)}</b><span>получено</span></div>
          <div className="fact"><b>{bytes(d.totals.tx)}</b><span>отправлено</span></div>
          <div className="fact"><b>{d.totals.domains}</b><span>адресов{newCount ? `, новых сегодня: ${newCount}` : ''}</span></div>
          <div className="fact"><b>{d.totals.apps}</b><span>программ выходило в сеть</span></div>
        </div>
      )}

      <section className="block">
        <h2>Потоки <small>программа и адрес; светлая часть полосы — получено, тёмная — отправлено</small></h2>
        <div className="panel">
          {!flows.length ? <div className="empty">{net.loading ? 'Загружаю…' : 'Сетевой активности за период нет.'}</div> : flows.map(f => (
            <div className="flow-row" key={f.app + f.domain}>
              <span className="flow-app" title={f.app} onClick={() => onApp(f.app)}>{appLabel(f.app)}</span>
              <div className="flow-track" title={`получено ${bytes(f.rx)}, отправлено ${bytes(f.tx)}, подключений ${f.connects}`}>
                <div className="flow-rx" style={{ width: `${(f.rx / maxFlow) * 100}%` }} />
                <div className="flow-tx" style={{ left: `${(f.rx / maxFlow) * 100}%`, width: `${Math.max(0.3, (f.tx / maxFlow) * 100)}%` }} />
              </div>
              <span className="flow-dom" title={f.domain}>
                {f.domain} {!f.known && <em>IP</em>} <em>{bytes(f.rx + f.tx)}</em>
              </span>
            </div>
          ))}
        </div>
      </section>

      <section className="block">
        <h2>Адреса <small>{domains.length}</small></h2>
        <div className="panel scroll">
          <table className="table">
            <thead>
              <tr>
                <th {...thProps('domain')}>Адрес</th>
                <th {...thProps('apps')} style={{ textAlign: 'left' }}>Программы</th>
                <th {...thProps('rx')}>Получено</th>
                <th {...thProps('tx')}>Отправлено</th>
                <th {...thProps('firstSeen')}>Впервые</th>
              </tr>
            </thead>
            <tbody>
              {domains.map(r => (
                <tr key={r.domain} onClick={() => onApp(r.apps[0])}>
                  <td><span className="name">{r.domain} {r.newToday && <span className="tag new">новый</span>}</span></td>
                  <td className="dim" style={{ textAlign: 'left' }}>{r.apps.map(appLabel).join(', ')}</td>
                  <td>{bytes(r.rx)}</td>
                  <td className="dim">{bytes(r.tx)}</td>
                  <td className="dim">{dayTime(r.firstSeen)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}
