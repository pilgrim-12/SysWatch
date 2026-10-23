import { useMemo, useState } from 'react';
import { api, type ReportApp } from '../api';
import { useLoad } from '../hooks';
import { COLOR, appLabel, bytes, dayTime, pct, plural } from '../format';
import { Plot } from '../components/Plot';
import { WhyCard } from '../components/WhyCard';

const PERIODS = [
  { days: 1, label: 'Сутки' },
  { days: 7, label: '7 дней' },
];

export const RULE_NAMES: Record<string, string> = {
  unsigned_net: 'Программа без подписи выходит в сеть',
  temp_exec: 'Запуск из временной папки или «Загрузок»',
  new_program: 'Новая программа',
  new_domain: 'Новый адрес в сети',
  autostart: 'Новое в автозагрузке',
  cpu: 'Долгая нагрузка на процессор',
  memory: 'Память растёт без снижения',
  exposed_port: 'Порт открыт в сеть',
};

export function Report({ onApp }: { onApp: (name: string) => void }) {
  const [days, setDays] = useState(7);
  const report = useLoad(() => api.report(days), [days]);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);
  const r = report.data;

  const series = useMemo(() => r ? [
    { label: 'Процессор', color: COLOR.cpu, values: r.points.map(p => p.cpu), fill: true },
    { label: 'Память', color: COLOR.mem, values: r.points.map(p => p.mem) },
  ] : [], [r]);

  const save = async () => {
    setSaving(true); setSaved(null);
    try { const res = await api.reportExport(days); setSaved(res.path); }
    catch (e) { setSaved(`Не удалось сохранить: ${String((e as Error)?.message ?? e)}`); }
    finally { setSaving(false); }
  };

  return (
    <>
      <div className="head">
        <div>
          <h1>Отчёт</h1>
          <p>Итоги за период: когда и почему компьютер тормозил, кто больше всего потреблял, что нового появилось. Объяснения строятся по правилам из данных агента — без интернета.</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label="Период">
            {PERIODS.map(p => <button key={p.days} aria-pressed={days === p.days} onClick={() => setDays(p.days)}>{p.label}</button>)}
          </div>
          <button className="btn" disabled={saving || !r} onClick={save}>{saving ? 'Сохраняю…' : 'Сохранить в HTML'}</button>
        </div>
      </div>

      {saved && <div className="note">{saved.startsWith('Не удалось') ? saved : <>Отчёт сохранён и открыт: <code>{saved}</code></>}</div>}
      {report.error && <div className="error">Не удалось собрать отчёт: {report.error}</div>}
      {!r ? <div className="empty">{report.error ? '' : 'Собираю отчёт…'}</div> : (
        <>
          <div className="facts">
            <div className="fact"><b style={{ color: COLOR.cpu }}>{pct(r.avgCpu, 0)}</b><span>процессор в среднем, пик {pct(r.peakCpu, 0)}</span></div>
            <div className="fact"><b style={{ color: COLOR.mem }}>{pct(r.avgMem, 0)}</b><span>память в среднем, пик {pct(r.peakMem, 0)}</span></div>
            <div className="fact">
              <b style={{ color: r.episodes.length ? 'var(--warn)' : COLOR.ok }}>{r.episodes.length}</b>
              <span>{plural(r.episodes.length, 'перегрузка', 'перегрузки', 'перегрузок')}, всего {r.slowMinutes} мин</span>
            </div>
            <div className="fact"><b style={{ color: COLOR.net }}>{bytes(r.netRx)}</b><span>получено из сети, отправлено {bytes(r.netTx)}</span></div>
          </div>

          <section className="block" style={{ marginTop: 20 }}>
            <h2>Загрузка <small>{days === 1 ? 'по 10 минут' : 'по часам'}, данные агента есть за {Math.round(r.hours)} ч</small></h2>
            <div className="panel chart-box">
              {r.points.length > 1
                ? <Plot ts={r.points.map(p => p.ts)} series={series} height={180} yMax={100} yFormat={v => `${v}%`} />
                : <div className="empty">Мало данных</div>}
            </div>
          </section>

          <section className="block">
            <h2>Почему тормозило <small>отрезки, когда процессор держался выше 75% или память — выше 90%</small></h2>
            {r.episodes.length === 0
              ? <div className="panel"><div className="empty">Перегрузок за период не было.</div></div>
              : r.episodes.map(w => <WhyCard key={w.from} why={w} onApp={onApp} />)}
            <p className="hint-line">Разобрать любой момент можно на «Таймлайне»: выбери минуту на графике и нажми «Почему тормозило?».</p>
          </section>

          <section className="block">
            <h2>Кто больше всего потреблял</h2>
            <div className="report-tops">
              <Top title="Процессор" rows={r.topCpu} value={a => pct(a.cpu)} color={COLOR.cpu} onApp={onApp} />
              <Top title="Память, пик" rows={r.topMem} value={a => bytes(a.mem)} color={COLOR.mem} onApp={onApp} />
              <Top title="Чтение и запись" rows={r.topIo} value={a => bytes(a.io)} color={COLOR.disk} onApp={onApp} />
              <Top title="Сеть" rows={r.topNet} value={a => bytes(a.net)} color={COLOR.net} onApp={onApp} />
            </div>
          </section>

          <section className="block">
            <h2>Безопасность</h2>
            <div className="report-tops">
              <div className="panel">
                <table className="table">
                  <thead><tr><th style={{ textAlign: 'left' }}>Что найдено</th><th>Сколько</th></tr></thead>
                  <tbody>
                    {r.anomalies.length === 0 && <tr><td className="dim" colSpan={2}>Аномалий не найдено</td></tr>}
                    {r.anomalies.map(a => <tr key={a.rule} onClick={() => { location.hash = 'anomalies'; }}><td style={{ textAlign: 'left' }}>{RULE_NAMES[a.rule] ?? a.rule}</td><td>{a.count}</td></tr>)}
                  </tbody>
                </table>
              </div>
              <div className="panel" style={{ padding: '12px 14px' }}>
                <div className="sub-h">Впервые запускались</div>
                {r.newPrograms.length === 0 ? <p className="dim">Ничего нового.</p> : (
                  <div className="why-culprits">
                    {r.newPrograms.map(p => <button key={p} className="chip" onClick={() => onApp(p)}><b>{appLabel(p)}</b></button>)}
                  </div>
                )}
                {r.notable.length > 0 && (
                  <>
                    <div className="sub-h" style={{ marginTop: 14 }}>Важное</div>
                    <ul className="plain">
                      {r.notable.map(a => <li key={a.id}><span className={a.severity === 'high' ? 'act-bad' : ''}>{a.title}</span> — {appLabel(a.app)} <span className="dim">{dayTime(a.ts)}</span></li>)}
                    </ul>
                  </>
                )}
              </div>
            </div>
          </section>

          {r.systemDrive && (
            <section className="block">
              <h2>Диск {r.systemDrive.root} <small>свободно {bytes(r.systemDrive.free)} из {bytes(r.systemDrive.total)}</small></h2>
              <div className="panel" style={{ padding: '12px 14px' }}>
                {r.diskGrowth?.since == null
                  ? <p className="dim" style={{ margin: 0 }}>Для сравнения нужен снимок диска недельной давности — он появится сам, SysWatch сканирует диск раз в сутки.</p>
                  : (
                    <>
                      <p style={{ marginTop: 0 }}>С {dayTime(r.diskGrowth.since)} занято {r.diskGrowth.driveDelta >= 0 ? 'больше' : 'меньше'} на <b>{bytes(Math.abs(r.diskGrowth.driveDelta))}</b>.</p>
                      {r.diskGrowth.items.filter(i => i.delta > 0).slice(0, 6).map(i => (
                        <div key={i.path} className="grow-row" style={{ cursor: 'default' }}>
                          <span className="grow-path" title={i.path}>{i.path}</span>
                          <span style={{ color: COLOR.disk }}>+{bytes(i.delta)}</span>
                        </div>
                      ))}
                    </>
                  )}
              </div>
            </section>
          )}
        </>
      )}
    </>
  );
}

function Top({ title, rows, value, color, onApp }: {
  title: string; rows: ReportApp[]; value: (a: ReportApp) => string; color: string; onApp: (name: string) => void;
}) {
  return (
    <div className="panel">
      <table className="table">
        <thead><tr><th style={{ textAlign: 'left' }}>{title}</th><th /></tr></thead>
        <tbody>
          {rows.length === 0 && <tr><td className="dim" colSpan={2}>нет данных</td></tr>}
          {rows.slice(0, 6).map(a => (
            <tr key={a.name} onClick={() => onApp(a.name)}>
              <td><span className="name">{appLabel(a.name)}</span></td>
              <td style={{ color }}>{value(a)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
