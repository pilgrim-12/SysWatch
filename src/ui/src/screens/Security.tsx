import { useEffect, useMemo, useState } from 'react';
import { api, type SecCheck, type SecReport, type SecStatus } from '../api';
import { ago, dayTime } from '../format';
import { t } from '../i18n';
import { ScoreRing } from './Overview';
import { AskAi } from '../components/AskAi';

const STATUS: Record<SecStatus, { icon: string; label: () => string; cls: string }> = {
  bad: { icon: '✕', label: () => t('опасно'), cls: 'bad' },
  warn: { icon: '!', label: () => t('стоит поправить'), cls: 'warn' },
  ok: { icon: '✓', label: () => t('в порядке'), cls: 'ok' },
  info: { icon: 'i', label: () => t('для сведения'), cls: 'info' },
  skip: { icon: '–', label: () => t('не проверено'), cls: 'skip' },
};
const ORDER: Record<SecStatus, number> = { bad: 0, warn: 1, info: 2, ok: 3, skip: 4 };

const scoreColor = (s: number) => (s >= 85 ? 'var(--ok)' : s >= 65 ? 'var(--cpu)' : 'var(--warn)');
const grade = (s: number) => (s >= 85 ? t('Хорошо защищён') : s >= 65 ? t('Есть что поправить') : t('Защита слабая'));

/**
 * «Проверка безопасности»: настройки защиты Windows, обновления, учётные записи, сеть, программы.
 * Ничего не меняет сама — кнопки открывают нужную страницу настроек Windows или раздел SysWatch.
 */
export function Security() {
  const [report, setReport] = useState<SecReport | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [onlyIssues, setOnlyIssues] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  const run = async () => {
    setRunning(true); setError(null); setNote(null);
    try { setReport(await api.securityRun()); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setRunning(false); }
  };

  // Первый заход — проверка запускается сама: пустой экран с кнопкой никому не нужен.
  useEffect(() => {
    api.security()
      .then(r => { setReport(r); setLoaded(true); if (!r) run(); })
      .catch(() => setLoaded(true));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);


  const fix = async (c: SecCheck) => {
    if (!c.fix) return;
    if (c.fix.startsWith('#')) { location.hash = c.fix; return; }
    const r = await api.securityOpen(c.fix).catch(() => ({ ok: false, message: t('Не удалось открыть настройки.') }));
    if (!r.ok) setNote(r.message);
  };

  const groups = useMemo(() => {
    const m = new Map<string, SecCheck[]>();
    for (const c of report?.checks ?? []) {
      if (onlyIssues && c.status !== 'bad' && c.status !== 'warn') continue;
      if (!m.has(c.group)) m.set(c.group, []);
      m.get(c.group)!.push(c);
    }
    for (const list of m.values()) list.sort((a, b) => ORDER[a.status] - ORDER[b.status]);
    return [...m.entries()];
  }, [report, onlyIssues]);

  const counts = useMemo(() => {
    const n = { bad: 0, warn: 0, ok: 0, info: 0, skip: 0 } as Record<SecStatus, number>;
    for (const c of report?.checks ?? []) n[c.status]++;
    return n;
  }, [report]);

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Безопасность')}</h1>
          <p>{t('Проверка настроек защиты Windows, обновлений, учётных записей, сети и программ. Всё проверяется на этом компьютере и никуда не отправляется. SysWatch ничего не меняет сам — кнопки открывают нужные настройки.')}</p>
        </div>
        <div className="head-tools">
          {report && <span className="dim">{t('проверено {0}', ago(report.ts))}</span>}
          <button className="btn primary" onClick={run} disabled={running}>
            {running ? <><span className="spin" aria-hidden /> {t('Проверяю…')}</> : report ? t('Проверить снова') : t('Проверить')}
          </button>
        </div>
      </div>

      {error && <div className="error">{error}</div>}
      {note && <div className="note">{note}</div>}

      {!report ? (
        <div className="panel empty">
          {!loaded ? t('Загружаю…') : running ? t('Проверяю: около 10–20 секунд…') : t('Проверка ещё не запускалась. Нажмите «Проверить» — это займёт 10–20 секунд.')}
        </div>
      ) : (
        <>
          <div className="hero sec-hero">
            <div className="panel hero-score">
              <ScoreRing score={report.score} color={scoreColor(report.score)} />
              <div>
                <div className="hero-grade" style={{ color: scoreColor(report.score) }}>{grade(report.score)}</div>
                <div className="dim">{dayTime(report.ts)} · {t('{0} с', report.seconds)}</div>
                {report.prevScore != null && report.prevScore !== report.score && (
                  <div className={report.score > report.prevScore ? 'up-good' : 'down-bad'}>
                    {report.score > report.prevScore ? '▲' : '▼'} {t('было {0} ({1})', report.prevScore, report.prevTs ? dayTime(report.prevTs) : '')}
                  </div>
                )}
              </div>
            </div>
            <div className="panel sec-sum">
              {(['bad', 'warn', 'ok', 'info'] as SecStatus[]).map(s => (
                <div key={s} className={`sec-count ${STATUS[s].cls}`}><b>{counts[s]}</b><span>{STATUS[s].label()}</span></div>
              ))}
              {report.changes.length > 0 && (
                <div className="sec-changes">
                  <h3>{t('Изменилось с прошлой проверки')}</h3>
                  {report.changes.map(c => (
                    <div key={c.id}>
                      <span className={ORDER[c.to] < ORDER[c.from] ? 'down-bad' : 'up-good'}>{ORDER[c.to] < ORDER[c.from] ? '▼' : '▲'}</span>{' '}
                      {c.title}: {STATUS[c.from].label()} → {STATUS[c.to].label()}
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          <div className="sec-filter">
            <label className="fsearch-check">
              <input type="checkbox" checked={onlyIssues} onChange={e => setOnlyIssues(e.target.checked)} /> {t('Только то, что стоит поправить')}
            </label>
          </div>

          {groups.length === 0 && <div className="panel empty">{t('Проблем не найдено.')}</div>}
          {groups.map(([group, list]) => (
            <section key={group} className="block">
              <h2>{group}</h2>
              <div className="panel">
                {list.map(c => (
                  <div key={c.id} className={`sec-row ${STATUS[c.status].cls}`}>
                    <span className={`sec-icon ${STATUS[c.status].cls}`} title={STATUS[c.status].label()} aria-label={STATUS[c.status].label()}>{STATUS[c.status].icon}</span>
                    <div className="sec-text">
                      <div className="sec-title">{c.title}</div>
                      <div>{c.detail}</div>
                      {c.why && (c.status !== 'ok') && <div className="dim">{c.why}</div>}
                      {(c.status === 'bad' || c.status === 'warn' || c.status === 'info') && (
                        <div className="sec-ai"><AskAi title={`${c.title}: ${c.detail}`} details={[c.why ? t('Пояснение SysWatch: {0}', c.why) : '']} search={`Windows ${c.title} ${c.detail}`} /></div>
                      )}
                    </div>
                    {c.fix && c.status !== 'ok' && (
                      <button className="btn" onClick={() => fix(c)}>{c.fix.startsWith('#') ? t('Открыть') : t('Исправить')}</button>
                    )}
                  </div>
                ))}
              </div>
            </section>
          ))}
        </>
      )}
    </>
  );
}
