import { useEffect, useState } from 'react';
import { api, errorText, type UpdatesView } from '../api';
import { ago } from '../format';
import { t, tp } from '../i18n';

/** Обновления программ через winget: список, «Обновить» по одной или выбранные. */
export function Updates() {
  const [v, setV] = useState<UpdatesView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sel, setSel] = useState<Set<string>>(new Set());

  const load = (refresh = false) => api.updates(refresh).then(x => { setV(x); setError(null); }).catch(e => setError(errorText(e)));

  useEffect(() => { load(); }, []);
  // Пока winget проверяет или ставит — опрашиваем.
  const busy = !!v && (v.checking || v.job.running);
  useEffect(() => {
    if (!busy) return;
    const id = setInterval(() => load(), 3000);
    return () => clearInterval(id);
  }, [busy]);

  const install = async (ids: string[]) => {
    try { await api.installUpdates(ids); setSel(new Set()); load(); }
    catch (e) { setError(errorText(e)); }
  };
  const toggle = (id: string) => setSel(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });

  const items = v?.items ?? [];
  const job = v?.job;

  return (
    <>
      {error && <div className="error">{error}</div>}
      {v && !v.available && <div className="note">{v.error}</div>}
      {v?.error && v.available && <div className="note">{t('winget сообщил: {0}', v.error)}</div>}

      <div className="report-toolbar">
        <span className="dim" style={{ color: 'var(--muted)' }}>
          {!v || (v.checking && !v.checkedAt) ? t('Спрашиваю winget, для чего есть обновления… Это до полминуты.')
            : v.checking ? t('Проверяю заново…')
            : t('Найдено {0} {1} · проверено {2}', items.length, tp(items.length, 'обновление', 'обновления', 'обновлений'), ago(v.checkedAt))}
        </span>
        <span className="grow" />
        <button className="btn" disabled={busy} onClick={() => load(true)}>{t('Проверить снова')}</button>
        <button className="btn primary" disabled={busy || sel.size === 0} onClick={() => install([...sel])}>{t('Обновить выбранные ({0})', sel.size)}</button>
        <button className="btn" disabled={busy || items.length === 0} onClick={() => install(items.map(i => i.id))}>{t('Обновить всё')}</button>
      </div>

      {job && (job.running || job.done.length > 0 || job.failed.length > 0) && (
        <div className={`note ${!job.running && job.failed.length === 0 ? 'ok' : ''}`} role="status">
          {job.running && job.current && <div>{t('Устанавливаю {0}… Окно установщика может появиться на панели задач.', job.current)}</div>}
          {job.done.length > 0 && <div>{t('Обновлено: {0}', job.done.join(', '))}</div>}
          {job.failed.length > 0 && <div>{t('Не получилось: {0}', job.failed.join(', '))}</div>}
          {job.log && <pre className="log">{job.log}</pre>}
        </div>
      )}

      <div className="panel scroll" style={{ maxHeight: 'calc(100vh - 440px)', minHeight: 260 }}>
        {items.length === 0 ? (
          <div className="empty">{!v?.checkedAt ? t('Проверяю…') : t('Все программы, которые знает winget, обновлены.')}</div>
        ) : (
          <table className="table updates">
            <thead>
              <tr>
                <th style={{ width: 32 }} />
                <th>{t('Программа')}</th>
                <th>{t('Сейчас')}</th>
                <th>{t('Доступна')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {items.map(i => {
                const done = job?.done.includes(i.id), now = job?.current === i.id;
                return (
                  <tr key={i.id} style={{ cursor: 'default' }}>
                    <td><input type="checkbox" aria-label={i.name} checked={sel.has(i.id)} disabled={busy || done} onChange={() => toggle(i.id)} /></td>
                    <td>
                      <div className="name">{i.name}</div>
                      <div className="dim" style={{ fontSize: 12 }}>{i.id}{i.source ? ` · ${i.source}` : ''}</div>
                    </td>
                    <td className="dim">{i.version}</td>
                    <td><b style={{ color: 'var(--ok)' }}>{i.available}</b></td>
                    <td style={{ textAlign: 'right' }}>
                      {done ? <span className="tag good">{t('обновлено')}</span>
                        : now ? <span className="tag warn">{t('ставится…')}</span>
                        : <button className="btn" disabled={busy} onClick={() => install([i.id])}>{t('Обновить')}</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
      <p className="hint-line" style={{ marginTop: 10 }}>{t('Обновления ставит winget — встроенный в Windows менеджер программ. Ставится тихо, по одной. Программу перед обновлением лучше закрыть.')}</p>
    </>
  );
}
