import { useMemo, useState } from 'react';
import { api, type AppDetail as Detail } from '../api';
import { useLoad } from '../hooks';
import { COLOR, RANGES, appLabel, bytes, dayTime, pct } from '../format';
import { Plot } from '../components/Plot';
import { t } from '../i18n';

function Signature({ d }: { d: Detail }) {
  switch (d.signature) {
    case 'signed': return <span className="tag good" title={t('Цифровая подпись exe-файла')}>{t('подписан: {0}', d.signer)}</span>;
    case 'system': return <span className="tag good">{t('системный файл Windows')}</span>;
    case 'unsigned': return <span className="tag bad" title={t('У exe-файла нет цифровой подписи')}>{t('без подписи')}</span>;
    default: return <span className="tag">{t('подпись не проверена')}</span>;
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
      cpu: [{ label: t('Процессор'), color: COLOR.cpu, values: s.cpu, fill: true }],
      mem: [{ label: t('Память'), color: COLOR.mem, values: s.mem, fill: true }],
      net: [
        { label: t('Получено'), color: COLOR.net, values: s.netRx, fill: true },
        { label: t('Отправлено'), color: COLOR.netTx, values: s.netTx },
      ],
      io: [{ label: t('Диск'), color: COLOR.disk, values: s.io, fill: true }],
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
      <button className="back" onClick={onBack}>{t('Назад')}</button>

      <div className="app-head">
        <div className="app-icon">{appLabel(name).slice(0, 1).toUpperCase()}</div>
        <div style={{ minWidth: 0 }}>
          <h1>{appLabel(name)}</h1>
          <div className="path">{d?.path ?? (app.loading ? t('Загружаю…') : t('Путь к файлу неизвестен'))}</div>
          {fw.data && <div style={{ marginTop: 6 }}><span className="tag bad">{t('выход в сеть запрещён')}</span></div>}
          {d && <div style={{ marginTop: 8, display: 'flex', gap: 8 }}><Signature d={d} /><span className="tag">{t('{0} запущено, {1} за период', live, d.processes.length)}</span></div>}
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label={t('Период')}>
            {RANGES.map(r => (
              <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>
            ))}
          </div>
        </div>
      </div>

      <div className="actions">
        {d?.path && <button className="btn" onClick={() => act(() => api.reveal(d.path!))}>{t('Показать файл')}</button>}
        {d?.path && (fw.data
          ? <button className="btn" onClick={() => act(() => api.unblock(d.path!)).then(fw.reload)}>{t('Разрешить сеть')}</button>
          : <button className="btn" onClick={() => act(() => api.block(d.path!)).then(fw.reload)} title={t('Правило Брандмауэра Windows: программа не сможет ни подключаться, ни принимать подключения')}>{t('Запретить сеть')}</button>)}
        <button className="btn" disabled={!live} onClick={() => act(() => api.priority(name, 'low'))} title={t('Программа будет уступать процессор всем остальным')}>{t('Понизить приоритет')}</button>
        <button className="btn" disabled={!live} onClick={() => act(() => api.priority(name, 'normal'))}>{t('Обычный приоритет')}</button>
        {confirmKill ? (
          <span className="confirm">
            <span>{t('Завершить все процессы {0}? Несохранённые данные пропадут.', name)}</span>
            <button className="btn danger" onClick={() => { setConfirmKill(false); act(() => api.kill(name)); }}>{t('Завершить')}</button>
            <button className="btn" onClick={() => setConfirmKill(false)}>{t('Отмена')}</button>
          </span>
        ) : (
          <button className="btn" disabled={!live} onClick={() => setConfirmKill(true)}>{t('Завершить…')}</button>
        )}
        {msg && <span className={msg.ok ? 'act-ok' : 'act-bad'}>{msg.text}</span>}
      </div>

      {app.error && <div className="error">{t('Не удалось загрузить программу: {0}', app.error)}</div>}

      {d && charts && d.series.ts.length > 1 ? (
        <div className="charts-4">
          <div className="panel chart-box">
            <h3>{t('Процессор')} <b style={{ color: COLOR.cpu }}>{t('в среднем {0}', pct(avg(d.series.cpu)))}</b></h3>
            <Plot ts={d.series.ts} series={charts.cpu} height={150} yFormat={v => pct(v, 0)} />
          </div>
          <div className="panel chart-box">
            <h3>{t('Память')} <b style={{ color: COLOR.mem }}>{t('максимум {0}', bytes(Math.max(...d.series.mem)))}</b></h3>
            <Plot ts={d.series.ts} series={charts.mem} height={150} yFormat={v => bytes(v, 0)} />
          </div>
          <div className="panel chart-box">
            <h3>{t('Сеть')} <b style={{ color: COLOR.net }}>{t('{0} получено, {1} отправлено', bytes(sum(d.series.netRx)), bytes(sum(d.series.netTx)))}</b></h3>
            <Plot ts={d.series.ts} series={charts.net} height={150} yFormat={v => bytes(v, 0)} />
          </div>
          <div className="panel chart-box">
            <h3 title={t('Сколько данных программа прочитала с дисков и записала на них. Это объём работы диска, а не занятое место.')}>{t('Чтение и запись данных')} <b style={{ color: COLOR.disk }}>{t('{0} прочитано и записано', bytes(sum(d.series.io)))}</b></h3>
            <Plot ts={d.series.ts} series={charts.io} height={150} yFormat={v => bytes(v, 0)} />
          </div>
        </div>
      ) : d && <div className="empty">{t('За этот период программа не была активна.')}</div>}

      {d && (
        <div className="grid-2-even">
          <section className="block">
            <h2>{t('Куда подключалась')} <small>{d.domains.length}</small></h2>
            <div className="panel scroll">
              {!d.domains.length ? <div className="empty">{t('Сетевых подключений не было.')}</div> : (
                <table className="table">
                  <thead><tr><th>{t('Адрес')}</th><th>{t('Получено')}</th><th>{t('Отправлено')}</th><th>{t('Подключений')}</th></tr></thead>
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
            <h2>{t('Процессы')} <small>{t('кто запустил и с какими параметрами')}</small></h2>
            <div className="panel scroll">
              <table className="table">
                <thead><tr><th>PID</th><th style={{ textAlign: 'left' }}>{t('Запустил')}</th><th>{t('Старт')}</th><th>{t('Статус')}</th></tr></thead>
                <tbody>
                  {d.processes.map(p => (
                    <tr key={`${p.pid}-${p.started}`} title={p.cmdline ?? ''} style={{ cursor: 'default' }}>
                      <td>
                        {p.pid}
                        {p.cmdline && <div className="cmd">{p.cmdline}</div>}
                      </td>
                      <td className="dim" style={{ textAlign: 'left', verticalAlign: 'top' }}>{p.parent ? appLabel(p.parent) : '—'}</td>
                      <td className="dim" style={{ verticalAlign: 'top' }}>{dayTime(p.started)}</td>
                      <td style={{ verticalAlign: 'top' }}>{p.exited ? <span className="tag">{t('завершён')}</span> : <span className="tag good">{t('работает')}</span>}</td>
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
