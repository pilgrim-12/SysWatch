import { useEffect, useMemo, useState } from 'react';
import { api, type ReportApp, type ReportSchedule, type ReportTemplate } from '../api';
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

function WeekReport({ onApp }: { onApp: (name: string) => void }) {
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

  const save = async (format: 'html' | 'pdf') => {
    setSaving(true); setSaved(null); setSaveErr(null);
    try { const res = await api.reportExport('week', days, format); if (res.ok) setSaved(res.path); else setSaveErr(res.message); }
    catch (e) { setSaveErr(t('Не удалось сохранить: {0}', String((e as Error)?.message ?? e))); }
    finally { setSaving(false); }
  };

  return (
    <>
      <div className="report-toolbar">
        <div className="seg" role="group" aria-label={t('Период')}>
          {PERIODS.map(p => <button key={p.days} aria-pressed={days === p.days} onClick={() => setDays(p.days)}>{p.label}</button>)}
        </div>
        <span className="grow" />
        <button className="btn" disabled={saving || !r} onClick={() => save('pdf')}>{saving ? t('Сохраняю…') : t('Сохранить PDF')}</button>
        <button className="btn" disabled={saving || !r} onClick={() => save('html')}>{t('Сохранить HTML')}</button>
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

// ── Центр отчётов: шаблоны, предпросмотр, выгрузка, расписание ────────────────

const ICONS: Record<string, string> = { health: '♥', week: '◷', security: '⛨', network: '⇅', dev: '</>' };

export function Report({ onApp }: { onApp: (name: string) => void }) {
  const templates = useLoad(() => api.reportTemplates(), []);
  const [tpl, setTpl] = useState<string>(() => { try { return localStorage.getItem('syswatch.report') ?? 'health'; } catch { return 'health'; } });
  useEffect(() => { try { localStorage.setItem('syswatch.report', tpl); } catch { /* ничего */ } }, [tpl]);
  const list = templates.data ?? [];
  const current = list.find(x => x.id === tpl);

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Отчёты')}</h1>
          <p>{t('Готовые отчёты о компьютере: посмотреть здесь, сохранить в PDF или HTML, получать автоматически по расписанию. Всё собирается на этом компьютере, без интернета.')}</p>
        </div>
        <div className="head-tools">
          <button className="btn" onClick={() => api.reportFolder()}>{t('Папка отчётов')}</button>
        </div>
      </div>

      <div className="tpl-grid">
        {list.map(x => (
          <button key={x.id} className={`tpl-card ${tpl === x.id ? 'on' : ''}`} onClick={() => setTpl(x.id)}>
            <span className="tpl-ico">{ICONS[x.id] ?? '▤'}</span>
            <b>{x.title}</b>
            <span>{x.description}</span>
          </button>
        ))}
      </div>

      {tpl === 'week' ? <WeekReport onApp={onApp} /> : current && <DocReport tpl={current} />}

      <Schedules templates={list} />
    </>
  );
}

function DocReport({ tpl }: { tpl: ReportTemplate }) {
  const [days, setDays] = useState(tpl.defaultDays || 7);
  useEffect(() => setDays(tpl.defaultDays || 7), [tpl.id, tpl.defaultDays]);
  const doc = useLoad(() => api.reportDoc(tpl.id, days), [tpl.id, days]);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  useEffect(() => setMsg(null), [tpl.id]);

  const save = async (format: 'html' | 'pdf') => {
    setBusy(true); setMsg(null);
    try { const r = await api.reportExport(tpl.id, days, format); setMsg({ ok: r.ok, text: r.message }); }
    catch (e) { setMsg({ ok: false, text: String((e as Error)?.message ?? e) }); }
    finally { setBusy(false); }
  };

  return (
    <>
      <div className="report-toolbar">
        {tpl.hasPeriod && (
          <div className="seg" role="group" aria-label={t('Период')}>
            {[1, 7, 30].map(d => <button key={d} aria-pressed={days === d} onClick={() => setDays(d)}>{d === 1 ? t('Сутки') : t('{0} дней', d)}</button>)}
          </div>
        )}
        {doc.loading && <span className="dim">{t('Собираю отчёт…')}</span>}
        <span className="grow" />
        <button className="btn" disabled={busy || !doc.data} onClick={() => save('pdf')}>{busy ? t('Сохраняю…') : t('Сохранить PDF')}</button>
        <button className="btn" disabled={busy || !doc.data} onClick={() => save('html')}>{t('Сохранить HTML')}</button>
      </div>
      {msg && <div className={msg.ok ? 'note' : 'error'}>{msg.text}</div>}
      {doc.error && <div className="error">{t('Не удалось собрать отчёт: {0}', doc.error)}</div>}
      <div className="panel doc-frame">
        {doc.data
          ? <iframe title={doc.data.title} srcDoc={doc.data.html} sandbox="" />
          : <div className="empty">{t('Собираю отчёт…')}{tpl.id === 'dev' ? ' ' + t('Первый раз до минуты: считаются размеры папок.') : ''}</div>}
      </div>
    </>
  );
}

function Schedules({ templates }: { templates: ReportTemplate[] }) {
  const data = useLoad(() => api.reportSchedules(), []);
  const [saving, setSaving] = useState(false);
  const list = data.data ?? [];
  const get = (id: string): ReportSchedule => list.find(s => s.template === id) ?? { template: id, every: 'off', format: 'pdf', lastRun: 0 };

  const update = async (id: string, patch: Partial<ReportSchedule>) => {
    const next = templates.map(x => ({ ...get(x.id), ...(x.id === id ? patch : {}) }));
    setSaving(true);
    try { await api.saveReportSchedules(next); data.reload(); } finally { setSaving(false); }
  };

  return (
    <section className="block" style={{ marginTop: 28 }}>
      <h2>{t('По расписанию')} <small>{t('SysWatch сам сохранит отчёт в папку отчётов и покажет уведомление')}</small></h2>
      <div className="panel">
        <table className="table dev-table">
          <tbody>
            {templates.map(x => {
              const s = get(x.id);
              return (
                <tr key={x.id}>
                  <td className="dev-name"><b>{x.title}</b>{s.lastRun > 0 && <div className="dim">{t('последний: {0}', dayTime(s.lastRun))}</div>}</td>
                  <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                    <select className="select" disabled={saving} value={s.every} onChange={e => update(x.id, { every: e.target.value as ReportSchedule['every'] })}>
                      <option value="off">{t('выключено')}</option>
                      <option value="day">{t('каждый день')}</option>
                      <option value="week">{t('каждую неделю')}</option>
                    </select>{' '}
                    <select className="select" disabled={saving || s.every === 'off'} value={s.format} onChange={e => update(x.id, { format: e.target.value as ReportSchedule['format'] })}>
                      <option value="pdf">PDF</option>
                      <option value="html">HTML</option>
                    </select>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p className="hint-line">{t('Отчёты делаются днём, когда компьютер включён: если в нужное время он был выключен — отчёт появится после включения.')}</p>
    </section>
  );
}
