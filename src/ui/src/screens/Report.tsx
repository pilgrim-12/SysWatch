import { useMemo, useState } from 'react';
import { api, type ReportApp } from '../api';
import { useLoad } from '../hooks';
import { COLOR, appLabel, bytes, dayTime, pct, pluralWord } from '../format';
import { Plot } from '../components/Plot';
import { WhyCard } from '../components/WhyCard';
import { t } from '../i18n';

const PERIODS = [
  { days: 1, label: t('Сутки') },
  { days: 7, label: t('7 дней') },
];

export const RULE_NAMES: Record<string, string> = {
  unsigned_net: t('Программа без подписи выходит в сеть'),
  temp_exec: t('Запуск из временной папки или «Загрузок»'),
  new_program: t('Новая программа'),
  new_domain: t('Новый адрес в сети'),
  autostart: t('Новое в автозагрузке'),
  cpu: t('Долгая нагрузка на процессор'),
  memory: t('Память растёт без снижения'),
  exposed_port: t('Порт открыт в сеть'),
};

export function Report({ onApp }: { onApp: (name: string) => void }) {
  const [days, setDays] = useState(7);
  const report = useLoad(() => api.report(days), [days]);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);
  const [saveErr, setSaveErr] = useState<string | null>(null);
  const r = report.data;

  const series = useMemo(() => r ? [
    { label: t('Процессор'), color: COLOR.cpu, values: r.points.map(p => p.cpu), fill: true },
    { label: t('Память'), color: COLOR.mem, values: r.points.map(p => p.mem) },
  ] : [], [r]);

  const save = async () => {
    setSaving(true); setSaved(null); setSaveErr(null);
    try { const res = await api.reportExport(days); setSaved(res.path); }
    catch (e) { setSaveErr(t('Не удалось сохранить: {0}', String((e as Error)?.message ?? e))); }
    finally { setSaving(false); }
  };

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Отчёт')}</h1>
          <p>{t('Итоги за период: когда и почему компьютер тормозил, кто больше всего потреблял, что нового появилось. Объяснения строятся по правилам из данных агента — без интернета.')}</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label={t('Период')}>
            {PERIODS.map(p => <button key={p.days} aria-pressed={days === p.days} onClick={() => setDays(p.days)}>{p.label}</button>)}
          </div>
          <button className="btn" disabled={saving || !r} onClick={save}>{saving ? t('Сохраняю…') : t('Сохранить в HTML')}</button>
        </div>
      </div>

      {saved && <div className="note">{t('Отчёт сохранён и открыт:')} <code>{saved}</code></div>}
      {saveErr && <div className="note">{saveErr}</div>}
      {report.error && <div className="error">{t('Не удалось собрать отчёт: {0}', report.error)}</div>}
      {!r ? <div className="empty">{report.error ? '' : t('Собираю отчёт…')}</div> : (
        <>
          <div className="facts">
            <div className="fact"><b style={{ color: COLOR.cpu }}>{pct(r.avgCpu, 0)}</b><span>{t('процессор в среднем, пик {0}', pct(r.peakCpu, 0))}</span></div>
            <div className="fact"><b style={{ color: COLOR.mem }}>{pct(r.avgMem, 0)}</b><span>{t('память в среднем, пик {0}', pct(r.peakMem, 0))}</span></div>
            <div className="fact">
              <b style={{ color: r.episodes.length ? 'var(--warn)' : COLOR.ok }}>{r.episodes.length}</b>
              <span>{t('{0}, всего {1} мин', pluralWord(r.episodes.length, 'перегрузка', 'перегрузки', 'перегрузок'), r.slowMinutes)}</span>
            </div>
            <div className="fact"><b style={{ color: COLOR.net }}>{bytes(r.netRx)}</b><span>{t('получено из сети, отправлено {0}', bytes(r.netTx))}</span></div>
          </div>

          <section className="block" style={{ marginTop: 20 }}>
            <h2>{t('Загрузка')} <small>{t('{0}, данные агента есть за {1} ч', days === 1 ? t('по 10 минут') : t('по часам'), Math.round(r.hours))}</small></h2>
            <div className="panel chart-box">
              {r.points.length > 1
                ? <Plot ts={r.points.map(p => p.ts)} series={series} height={180} yMax={100} yFormat={v => `${v}%`} />
                : <div className="empty">{t('Мало данных')}</div>}
            </div>
          </section>

          <section className="block">
            <h2>{t('Почему тормозило')} <small>{t('отрезки, когда процессор держался выше 75% или память — выше 90%')}</small></h2>
            {r.episodes.length === 0
              ? <div className="panel"><div className="empty">{t('Перегрузок за период не было.')}</div></div>
              : r.episodes.map(w => <WhyCard key={w.from} why={w} onApp={onApp} />)}
            <p className="hint-line">{t('Разобрать любой момент можно на «Таймлайне»: выбери минуту на графике и нажми «Почему тормозило?».')}</p>
          </section>

          <section className="block">
            <h2>{t('Кто больше всего потреблял')}</h2>
            <div className="report-tops">
              <Top title={t('Процессор')} rows={r.topCpu} value={a => pct(a.cpu)} color={COLOR.cpu} onApp={onApp} />
              <Top title={t('Память, пик')} rows={r.topMem} value={a => bytes(a.mem)} color={COLOR.mem} onApp={onApp} />
              <Top title={t('Чтение и запись')} rows={r.topIo} value={a => bytes(a.io)} color={COLOR.disk} onApp={onApp} />
              <Top title={t('Сеть')} rows={r.topNet} value={a => bytes(a.net)} color={COLOR.net} onApp={onApp} />
            </div>
          </section>

          <section className="block">
            <h2>{t('Безопасность')}</h2>
            <div className="report-tops">
              <div className="panel">
                <table className="table">
                  <thead><tr><th style={{ textAlign: 'left' }}>{t('Что найдено')}</th><th>{t('Сколько')}</th></tr></thead>
                  <tbody>
                    {r.anomalies.length === 0 && <tr><td className="dim" colSpan={2}>{t('Аномалий не найдено')}</td></tr>}
                    {r.anomalies.map(a => <tr key={a.rule} onClick={() => { location.hash = 'anomalies'; }}><td style={{ textAlign: 'left' }}>{RULE_NAMES[a.rule] ?? a.rule}</td><td>{a.count}</td></tr>)}
                  </tbody>
                </table>
              </div>
              <div className="panel" style={{ padding: '12px 14px' }}>
                <div className="sub-h">{t('Впервые запускались')}</div>
                {r.newPrograms.length === 0 ? <p className="dim">{t('Ничего нового.')}</p> : (
                  <div className="why-culprits">
                    {r.newPrograms.map(p => <button key={p} className="chip" onClick={() => onApp(p)}><b>{appLabel(p)}</b></button>)}
                  </div>
                )}
                {r.notable.length > 0 && (
                  <>
                    <div className="sub-h" style={{ marginTop: 14 }}>{t('Важное')}</div>
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
              <h2>{t('Диск {0}', r.systemDrive.root)} <small>{t('свободно {0} из {1}', bytes(r.systemDrive.free), bytes(r.systemDrive.total))}</small></h2>
              <div className="panel" style={{ padding: '12px 14px' }}>
                {r.diskGrowth?.since == null
                  ? <p className="dim" style={{ margin: 0 }}>{t('Для сравнения нужен снимок диска недельной давности — он появится сам, SysWatch сканирует диск раз в сутки.')}</p>
                  : (
                    <>
                      <p style={{ marginTop: 0 }}>{r.diskGrowth.driveDelta >= 0 ? t('С {0} занято больше на', dayTime(r.diskGrowth.since)) : t('С {0} занято меньше на', dayTime(r.diskGrowth.since))} <b>{bytes(Math.abs(r.diskGrowth.driveDelta))}</b>.</p>
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
          {rows.length === 0 && <tr><td className="dim" colSpan={2}>{t('нет данных')}</td></tr>}
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
