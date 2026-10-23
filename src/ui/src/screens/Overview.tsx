import { useEffect, useMemo, useState } from 'react';
import { api, errorText, type AiAnswer, type Finding, type HealthReport } from '../api';
import { useLoad } from '../hooks';
import { COLOR, ago, bytes, pct } from '../format';
import { Plot } from '../components/Plot';
import { t } from '../i18n';

const GRADE_COLOR: Record<HealthReport['gradeKey'], string> = {
  great: '#6BCF8E', good: '#4FC3D9', fair: '#F2B544', poor: '#F27C6B',
};

const SEVERITY: Record<Finding['severity'], { label: string; cls: string }> = {
  critical: { label: t('Важно'), cls: 'critical' },
  warn: { label: t('Внимание'), cls: 'warn' },
  info: { label: t('Совет'), cls: 'info' },
};

/** Кольцо с оценкой 0–100. */
function ScoreRing({ score, color }: { score: number; color: string }) {
  const r = 70, len = 2 * Math.PI * r;
  return (
    <svg className="score-ring" viewBox="0 0 180 180" width="180" height="180" role="img" aria-label={t('Оценка {0} из 100', score)}>
      <circle cx="90" cy="90" r={r} fill="none" stroke="var(--grid)" strokeWidth="14" />
      <circle cx="90" cy="90" r={r} fill="none" stroke={color} strokeWidth="14" strokeLinecap="round"
        strokeDasharray={`${(len * score) / 100} ${len}`} transform="rotate(-90 90 90)" style={{ transition: 'stroke-dasharray .6s ease' }} />
      <text x="90" y="98" textAnchor="middle" className="score-num" fill={color}>{score}</text>
      <text x="90" y="124" textAnchor="middle" className="score-of">{t('из 100')}</text>
    </svg>
  );
}

function uptime(s: number) {
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600);
  return d > 0 ? t('{0} дн. {1} ч', d, h) : t('{0} ч {1} мин', h, Math.floor((s % 3600) / 60));
}

export function Overview() {
  const health = useLoad(() => api.health(), [], 300_000);
  const h = health.data;
  const color = h ? GRADE_COLOR[h.gradeKey] : 'var(--muted)';

  const series = useMemo(() => h ? [
    { label: t('Процессор'), color: COLOR.cpu, values: h.points.map(p => p.cpu), fill: true },
    { label: t('Память'), color: COLOR.mem, values: h.points.map(p => p.mem) },
  ] : [], [h]);

  const critical = h?.findings.filter(f => f.severity === 'critical').length ?? 0;
  const warn = h?.findings.filter(f => f.severity === 'warn').length ?? 0;
  const go = (hash: string) => { location.hash = hash.replace(/^#/, ''); };

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Обзор')}</h1>
          <p>{t('Состояние компьютера одним взглядом: оценка, что не так и что с этим сделать.')}</p>
        </div>
        <div className="head-tools">
          {h && <span className="dim" style={{ fontSize: 13 }}>{t('проверено {0}', ago(h.ts))}</span>}
          <button className="btn" disabled={health.loading} onClick={() => api.health(true).then(() => health.reload())}>{t('Проверить заново')}</button>
        </div>
      </div>

      {health.error && <div className="error">{t('Не удалось оценить состояние: {0}', health.error)}</div>}
      {!h ? <div className="panel"><div className="empty">{t('Проверяю диски, журналы сбоев, аномалии и историю нагрузки…')}</div></div> : (
        <>
          <div className="hero">
            <div className="panel hero-score">
              <ScoreRing score={h.score} color={color} />
              <div>
                <div className="hero-grade" style={{ color }}>{h.grade}</div>
                <p className="hero-sum">
                  {h.findings.length === 0
                    ? t('Проблем не найдено. Так держать.')
                    : critical > 0
                      ? t('Найдено важных проблем: {0}, ещё {1} — на заметку.', critical, h.findings.length - critical)
                      : warn > 0
                        ? t('Важных проблем нет. Стоит обратить внимание: {0}.', warn)
                        : t('Важных проблем нет, есть пара советов.')}
                </p>
                {!h.facts.agentRunning && <div className="tag bad">{t('агент не работает')}</div>}
              </div>
            </div>

            <div className="hero-facts">
              <Fact value={pct(h.facts.cpuAvg, 0)} label={t('процессор за сутки в среднем')} color={COLOR.cpu} />
              <Fact value={pct(h.facts.memAvg, 0)} label={t('память в среднем, пик {0}', pct(h.facts.memPeak, 0))} color={COLOR.mem} />
              <Fact value={h.facts.driveTotal ? bytes(h.facts.driveFree) : '—'}
                label={h.facts.systemDrive ? t('свободно на диске {0} (всего {1})', h.facts.systemDrive.replace(/\\$/, ''), bytes(h.facts.driveTotal)) : t('свободно на системном диске')}
                color={h.facts.driveTotal && h.facts.driveFree / h.facts.driveTotal < 0.1 ? 'var(--warn)' : COLOR.disk} />
              <Fact value={uptime(h.facts.uptimeSeconds)} label={t('без перезагрузки')} />
              <Fact value={String(h.facts.crashes7d)} label={t('сбоев за неделю')} color={h.facts.crashes7d ? 'var(--warn)' : COLOR.ok} />
              <Fact value={String(h.facts.slowdowns7d)} label={t('перегрузок за неделю')} color={h.facts.slowdowns7d >= 3 ? 'var(--cpu)' : COLOR.ok} />
            </div>
          </div>

          <section className="block">
            <h2>{t('Что стоит сделать')} {h.findings.length > 0 && <small>{h.findings.length}</small>}</h2>
            {h.findings.length === 0 ? (
              <div className="panel all-good">
                <span className="all-good-ico">✓</span>
                <div><b>{t('Всё в порядке')}</b><p>{t('Места хватает, сбоев и перегрузок нет, подозрительного не найдено.')}</p></div>
              </div>
            ) : (
              <div className="findings">
                {h.findings.map(f => (
                  <article key={f.id} className={`finding ${SEVERITY[f.severity].cls}`}>
                    <div className="finding-body">
                      <span className={`finding-tag ${SEVERITY[f.severity].cls}`}>{SEVERITY[f.severity].label}</span>
                      <h3>{f.title}</h3>
                      <p>{f.detail}</p>
                    </div>
                    <button className="btn" onClick={() => go(f.action)}>{f.actionLabel} →</button>
                  </article>
                ))}
              </div>
            )}
          </section>

          <AiExplain />

          <section className="block">
            <h2>{t('Нагрузка за сутки')} <small>{t('по 10 минут')}</small></h2>
            <div className="panel chart-box">
              {h.points.filter(p => p.cpu != null).length > 1
                ? <Plot ts={h.points.map(p => p.ts)} series={series} height={160} yMax={100} yFormat={v => `${v}%`} />
                : <div className="empty">{t('История появится через несколько минут работы агента.')}</div>}
            </div>
          </section>

          <p className="hint-line">{t('Оценка: из 100 вычитаются баллы за каждую найденную проблему. Важные проблемы весят больше, советы — меньше.')}</p>
        </>
      )}
    </>
  );
}

function Fact({ value, label, color }: { value: string; label: string; color?: string }) {
  return (
    <div className="panel hero-fact">
      <b style={{ color }}>{value}</b>
      <span>{label}</span>
    </div>
  );
}

/** «Объяснить простыми словами» — только если пользователь сам включил локальный ИИ в настройках. */
function AiExplain() {
  const [enabled, setEnabled] = useState(false);
  const [busy, setBusy] = useState(false);
  const [answer, setAnswer] = useState<AiAnswer | null>(null);
  useEffect(() => { api.settings().then(s => setEnabled(s.ai.enabled)).catch(() => {}); }, []);
  if (!enabled) return null;

  const ask = async () => {
    setBusy(true); setAnswer(null);
    try { setAnswer(await api.aiExplain()); }
    catch (e) { setAnswer({ ok: false, text: errorText(e), model: null }); }
    finally { setBusy(false); }
  };

  return (
    <section className="block">
      <h2>{t('Простыми словами')} <small>{t('локальный ИИ, без интернета')}</small></h2>
      <div className="panel ai-card">
        {!answer && !busy && <p className="dim">{t('Модель на этом компьютере прочитает оценку и найденные проблемы и объяснит, что важнее всего.')}</p>}
        {busy && <p className="dim">{t('Модель думает… На слабом компьютере это до минуты.')}</p>}
        {answer && <div className={answer.ok ? 'ai-text' : 'error'}>{answer.text}</div>}
        {answer?.model && <div className="hint-line" style={{ margin: '8px 0 0' }}>{t('Модель: {0}', answer.model)}</div>}
        <div style={{ marginTop: 10 }}><button className="btn" disabled={busy} onClick={ask}>{answer ? t('Спросить ещё раз') : t('Объяснить')}</button></div>
      </div>
    </section>
  );
}
