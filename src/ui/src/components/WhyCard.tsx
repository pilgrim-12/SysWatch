import type { Why } from '../api';
import { appLabel, bytes, dayTime, pct, time } from '../format';
import { t } from '../i18n';

const KIND: Record<Why['kinds'][number], { label: string; cls: string }> = {
  cpu: { label: t('процессор'), cls: 'cpu' },
  mem: { label: t('память'), cls: 'mem' },
  io: { label: t('диск'), cls: 'disk' },
};

/** Разбор одного отрезка: вердикт, подробности, виновники (кликабельны) и что запустилось рядом. */
export function WhyCard({ why, onApp, compact = false }: { why: Why; onApp: (name: string) => void; compact?: boolean }) {
  const main = why.kinds[0] ?? 'ok';
  return (
    <article className={`why why-${main}`}>
      <header>
        <h3>{dayTime(why.from)} — {time(why.to)}</h3>
        <div className="why-tags">
          {why.kinds.length === 0 && <span className="tag">{t('без перегрузки')}</span>}
          {why.kinds.map(k => <span key={k} className={`tag why-k ${KIND[k].cls}`}>{KIND[k].label}</span>)}
          {why.hasData && <span className="dim">{t('процессор до {0}, память до {1}', pct(why.peakCpu, 0), pct(why.peakMem, 0))}</span>}
        </div>
      </header>
      <p className="why-verdict">{why.verdict}</p>
      {!compact && why.details.length > 0 && <ul className="why-details">{why.details.map((d, i) => <li key={i}>{d}</li>)}</ul>}
      {why.culprits.length > 0 && (
        <div className="why-culprits">
          {why.culprits.map(c => (
            <button key={c.app} className="chip" onClick={() => onApp(c.app)} title={c.hint ?? undefined}>
              <b>{appLabel(c.app)}</b>
              <span>{c.cpu >= 1 ? pct(c.cpu, 0) : ''}{c.cpu >= 1 ? ' · ' : ''}{bytes(c.mem)}</span>
            </button>
          ))}
        </div>
      )}
      {!compact && why.started.length > 0 && (
        <p className="why-started dim">
          {t('Запустилось рядом: {0}', why.started.slice(0, 8).map(s => appLabel(s.app) + (s.hint ? ` (${s.hint})` : '')).join(', '))}
        </p>
      )}
    </article>
  );
}
