import { useState } from 'react';
import type { Crash, CrashKind, CrashList } from '../api';
import { appLabel, dayTime } from '../format';

export const CRASH_KINDS: Record<CrashKind, { label: string; cls: string }> = {
  bsod: { label: 'Синий экран', cls: 'bad' },
  power: { label: 'Внезапное выключение', cls: 'bad' },
  gpu: { label: 'Видеодрайвер', cls: 'warn-tag' },
  hardware: { label: 'Оборудование', cls: 'warn-tag' },
  disk: { label: 'Диск', cls: 'bad' },
  app: { label: 'Падение программы', cls: '' },
  hang: { label: 'Зависание программы', cls: '' },
  update: { label: 'Обновление Windows', cls: '' },
};

/** Список сбоев с фильтром по видам. only — показать только эти виды (например, для экрана видеокарты). */
export function CrashView({ data, only, onApp }: { data: CrashList | null; only?: CrashKind[]; onApp?: (name: string) => void }) {
  const [filter, setFilter] = useState<CrashKind | null>(null);
  if (!data) return <div className="empty">Читаю журналы Windows…</div>;
  const items = data.items.filter(c => (!only || only.includes(c.kind)) && (!filter || c.kind === filter));
  const kinds = (Object.keys(CRASH_KINDS) as CrashKind[]).filter(k => (data.counts[k] ?? 0) > 0 && (!only || only.includes(k)));

  return (
    <>
      {kinds.length > 1 && (
        <div className="why-culprits" style={{ marginBottom: 12 }}>
          <button className="chip" aria-pressed={!filter} onClick={() => setFilter(null)}><b>Все</b></button>
          {kinds.map(k => (
            <button key={k} className="chip" aria-pressed={filter === k} onClick={() => setFilter(filter === k ? null : k)}>
              <b>{CRASH_KINDS[k].label}</b><span>{data.counts[k]}</span>
            </button>
          ))}
        </div>
      )}
      <div className="panel">
        {items.length === 0 ? <div className="empty">Сбоев за {data.days} дн. не найдено.</div> : items.slice(0, 300).map((c: Crash, i) => (
          <div key={i} className="crash-row">
            <span className="hist-time">{dayTime(c.ts)}</span>
            <div className="crash-main">
              <div>
                <span className={`tag ${CRASH_KINDS[c.kind]?.cls ?? ''}`}>{CRASH_KINDS[c.kind]?.label ?? c.kind}</span>
                <b>{c.title}</b>
                {c.app && (onApp
                  ? <button className="link" onClick={() => onApp(c.app!)}>{appLabel(c.app)}</button>
                  : <span className="dim"> {c.app}</span>)}
              </div>
              {c.detail && <div className="dim">{c.detail}</div>}
            </div>
            <span className="hist-src" title={`${c.source}, событие ${c.eventId}`}>{c.source} · {c.eventId}</span>
          </div>
        ))}
      </div>
    </>
  );
}
