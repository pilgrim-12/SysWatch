import { useMemo, useState } from 'react';
import { api, type AppDetail as Detail } from '../api';
import { useLoad } from '../hooks';
import { COLOR, RANGES, appLabel, bytes, dayTime, pct } from '../format';
import { Plot } from '../components/Plot';

function Signature({ d }: { d: Detail }) {
  switch (d.signature) {
    case 'signed': return <span className="tag good" title="Цифровая подпись exe-файла">подписан: {d.signer}</span>;
    case 'system': return <span className="tag good">системный файл Windows</span>;
    case 'unsigned': return <span className="tag bad" title="У exe-файла нет цифровой подписи">без подписи</span>;
    default: return <span className="tag">подпись не проверена</span>;
  }
}

const sum = (a: number[]) => a.reduce((s, v) => s + v, 0);
const avg = (a: number[]) => (a.length ? sum(a) / a.length : 0);

export function AppDetail({ name, range, setRange, onBack }: { name: string; range: number; setRange: (m: number) => void; onBack: () => void }) {
  const app = useLoad(() => api.app(name, range), [name, range], 60_000);
  const d = app.data;

  const charts = useMemo(() => {
    if (!d) return null;
    const s = d.series;
    return {
      cpu: [{ label: 'Процессор', color: COLOR.cpu, values: s.cpu, fill: true }],
      mem: [{ label: 'Память', color: COLOR.mem, values: s.mem, fill: true }],
      net: [
        { label: 'Получено', color: COLOR.net, values: s.netRx, fill: true },
        { label: 'Отправлено', color: COLOR.netTx, values: s.netTx },
      ],
      io: [{ label: 'Диск', color: COLOR.disk, values: s.io, fill: true }],
    };
  }, [d]);

  const live = d?.processes.filter(p => p.exited == null).length ?? 0;
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const [confirmKill, setConfirmKill] = useState(false);
  const fw = useLoad(() => (d?.path ? api.isBlocked(d.path) : Promise.resolve(false)), [d?.path]);
  const act = async (fn: () => Promise<{ ok: boolean; message: string }>) => {
    try { const r = await fn(); setMsg({ ok: r.ok, text: r.message }); app.reload(); }
    catch (e) { setMsg({ ok: false, text: String((e as Error).message ?? e) }); }
  };

  return (
    <>
      <button className="back" onClick={onBack}>Назад</button>

      <div className="app-head">
        <div className="app-icon">{appLabel(name).slice(0, 1).toUpperCase()}</div>
        <div style={{ minWidth: 0 }}>
          <h1>{appLabel(name)}</h1>
          <div className="path">{d?.path ?? (app.loading ? 'Загружаю…' : 'Путь к файлу неизвестен')}</div>
          {fw.data && <div style={{ marginTop: 6 }}><span className="tag bad">выход в сеть запрещён</span></div>}
          {d && <div style={{ marginTop: 8, display: 'flex', gap: 8 }}><Signature d={d} /><span className="tag">{live} запущено, {d.processes.length} за период</span></div>}
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label="Период">
            {RANGES.map(r => (
              <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>
            ))}
          </div>
        </div>
      </div>

      <div className="actions">
        {d?.path && <button className="btn" onClick={() => act(() => api.reveal(d.path!))}>Показать файл</button>}
        {d?.path && (fw.data
          ? <button className="btn" onClick={() => act(() => api.unblock(d.path!)).then(fw.reload)}>Разрешить сеть</button>
          : <button className="btn" onClick={() => act(() => api.block(d.path!)).then(fw.reload)} title="Правило Брандмауэра Windows: программа не сможет ни подключаться, ни принимать подключения">Запретить сеть</button>)}
        <button className="btn" disabled={!live} onClick={() => act(() => api.priority(name, 'low'))} title="Программа будет уступать процессор всем остальным">Понизить приоритет</button>
        <button className="btn" disabled={!live} onClick={() => act(() => api.priority(name, 'normal'))}>Обычный приоритет</button>
        {confirmKill ? (
          <span className="confirm">
            <span>Завершить все процессы {name}? Несохранённые данные пропадут.</span>
            <button className="btn danger" onClick={() => { setConfirmKill(false); act(() => api.kill(name)); }}>Завершить</button>
            <button className="btn" onClick={() => setConfirmKill(false)}>Отмена</button>
          </span>
        ) : (
          <button className="btn" disabled={!live} onClick={() => setConfirmKill(true)}>Завершить…</button>
        )}
        {msg && <span className={msg.ok ? 'act-ok' : 'act-bad'}>{msg.text}</span>}
      </div>

      {app.error && <div className="error">Не удалось загрузить программу: {app.error}</div>}

      {d && charts && d.series.ts.length > 1 ? (
        <div className="charts-4">
          <div className="panel chart-box">
            <h3>Процессор <b style={{ color: COLOR.cpu }}>в среднем {pct(avg(d.series.cpu))}</b></h3>
            <Plot ts={d.series.ts} series={charts.cpu} height={150} yFormat={v => pct(v, 0)} />
          </div>
          <div className="panel chart-box">
            <h3>Память <b style={{ color: COLOR.mem }}>максимум {bytes(Math.max(...d.series.mem))}</b></h3>
            <Plot ts={d.series.ts} series={charts.mem} height={150} yFormat={v => bytes(v, 0)} />
          </div>
          <div className="panel chart-box">
            <h3>Сеть, за минуту <b style={{ color: COLOR.net }}>{bytes(sum(d.series.netRx))} получено, {bytes(sum(d.series.netTx))} отправлено</b></h3>
            <Plot ts={d.series.ts} series={charts.net} height={150} yFormat={v => bytes(v, 0)} />
          </div>
          <div className="panel chart-box">
            <h3>Диск, за минуту <b style={{ color: COLOR.disk }}>{bytes(sum(d.series.io))} всего</b></h3>
            <Plot ts={d.series.ts} series={charts.io} height={150} yFormat={v => bytes(v, 0)} />
          </div>
        </div>
      ) : d && <div className="empty">За этот период программа не была активна.</div>}

      {d && (
        <div className="grid-2-even">
          <section className="block">
            <h2>Куда подключалась <small>{d.domains.length}</small></h2>
            <div className="panel scroll">
              {!d.domains.length ? <div className="empty">Сетевых подключений не было.</div> : (
                <table className="table">
                  <thead><tr><th>Адрес</th><th>Получено</th><th>Отправлено</th><th>Подключений</th></tr></thead>
                  <tbody>
                    {d.domains.map(x => (
                      <tr key={x.domain}><td>{x.domain}</td><td>{bytes(x.rx)}</td><td className="dim">{bytes(x.tx)}</td><td className="dim">{x.connects}</td></tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </section>

          <section className="block">
            <h2>Процессы <small>кто запустил и с какими параметрами</small></h2>
            <div className="panel scroll">
              <table className="table">
                <thead><tr><th>PID</th><th style={{ textAlign: 'left' }}>Запустил</th><th>Старт</th><th>Статус</th></tr></thead>
                <tbody>
                  {d.processes.map(p => (
                    <tr key={`${p.pid}-${p.started}`} title={p.cmdline ?? ''} style={{ cursor: 'default' }}>
                      <td>
                        {p.pid}
                        {p.cmdline && <div className="cmd">{p.cmdline}</div>}
                      </td>
                      <td className="dim" style={{ textAlign: 'left', verticalAlign: 'top' }}>{p.parent ? appLabel(p.parent) : '—'}</td>
                      <td className="dim" style={{ verticalAlign: 'top' }}>{dayTime(p.started)}</td>
                      <td style={{ verticalAlign: 'top' }}>{p.exited ? <span className="tag">завершён</span> : <span className="tag good">работает</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </div>
      )}
    </>
  );
}
