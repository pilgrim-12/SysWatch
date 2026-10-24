import { useEffect, useMemo, useState } from 'react';
import { api, type RecycleView } from '../api';
import { ago, bytes, dayTime } from '../format';
import { locale, t } from '../i18n';

type Confirm = null | 'empty' | 'delete';

/** Корзина Windows: что в ней лежит, откуда удалено, восстановить или удалить навсегда. */
export function RecycleBin({ onChanged }: { onChanged?: () => void }) {
  const [view, setView] = useState<RecycleView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState('');
  const [open, setOpen] = useState(false);
  const [confirm, setConfirm] = useState<Confirm>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null);
  const [limit, setLimit] = useState(100);

  const load = () => api.recycle().then(v => { setView(v); setError(null); }).catch(e => setError(String((e as Error).message ?? e)));
  useEffect(() => { load(); }, []);

  const items = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (view?.items ?? []).filter(i => !f || i.original.toLowerCase().includes(f));
  }, [view, filter]);
  const selSize = (view?.items ?? []).filter(i => sel.has(i.id)).reduce((s, i) => s + i.size, 0);
  const allOn = items.length > 0 && items.every(i => sel.has(i.id));

  const act = async (fn: () => Promise<{ ok: boolean; message: string }>) => {
    setBusy(true); setConfirm(null);
    try {
      const r = await fn();
      setNote({ ok: r.ok, text: r.message });
      setSel(new Set());
      await load();
      onChanged?.();
    } catch (e) { setNote({ ok: false, text: String((e as Error).message ?? e) }); }
    finally { setBusy(false); }
  };

  const toggle = (id: string) => setSel(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const toggleAll = () => setSel(allOn ? new Set() : new Set(items.map(i => i.id)));

  const count = view?.count ?? 0;
  return (
    <div className="panel rbin">
      <div className="rbin-top">
        <div className="rbin-sum">
          <b>{view ? bytes(view.total) : '—'}</b>
          <span className="dim">{view ? (count ? t('в Корзине объектов: {0}', count.toLocaleString(locale())) : t('Корзина пуста')) : t('Загружаю…')}</span>
        </div>
        {count > 0 && <button className="link" onClick={() => setOpen(o => !o)}>{open ? t('Скрыть') : t('Что внутри')}</button>}
        <span style={{ marginLeft: 'auto' }} />
        {confirm === 'empty' ? (
          <span className="confirm">
            <span>{t('Удалить всё из Корзины навсегда? Вернуть будет нельзя.')}</span>
            <button className="btn danger" disabled={busy} onClick={() => act(api.recycleEmpty)}>{t('Да, очистить')}</button>
            <button className="btn" onClick={() => setConfirm(null)}>{t('Отмена')}</button>
          </span>
        ) : (
          <button className="btn" disabled={busy || count === 0} onClick={() => setConfirm('empty')}>{t('Очистить Корзину')}</button>
        )}
      </div>
      {error && <div className="error">{error}</div>}
      {note && <div className={`note ${note.ok ? 'ok' : ''}`}>{note.text}</div>}

      {open && count > 0 && (
        <>
          <div className="rbin-tools">
            <label className="fsearch-check"><input type="checkbox" checked={allOn} onChange={toggleAll} /> {t('все')}</label>
            <input className="search" type="search" value={filter} placeholder={t('Фильтр: имя или папка')} aria-label={t('Фильтр: имя или папка')}
              onChange={e => setFilter(e.target.value)} />
            {sel.size > 0 && (
              <>
                <span className="dim">{t('Выбрано {0} на {1}', sel.size, bytes(selSize))}</span>
                <button className="btn" disabled={busy} onClick={() => act(() => api.recycleRestore([...sel]))}>{t('Восстановить')}</button>
                {confirm === 'delete' ? (
                  <span className="confirm">
                    <button className="btn danger" disabled={busy} onClick={() => act(() => api.recycleDelete([...sel]))}>{t('Да, удалить навсегда')}</button>
                    <button className="btn" onClick={() => setConfirm(null)}>{t('Отмена')}</button>
                  </span>
                ) : (
                  <button className="btn" disabled={busy} onClick={() => setConfirm('delete')}>{t('Удалить навсегда')}</button>
                )}
              </>
            )}
          </div>
          <div className="rbin-list">
            {items.slice(0, limit).map(i => (
              <label key={i.id} className={`rbin-row${sel.has(i.id) ? ' on' : ''}`}>
                <input type="checkbox" checked={sel.has(i.id)} onChange={() => toggle(i.id)} />
                <span className="fsearch-name" title={i.original}>
                  <b>{i.isDir ? '▸ ' : ''}{i.name}</b>
                  <span className="path-sub">{t('было в {0}', i.original.slice(0, i.original.lastIndexOf('\\')) || i.drive)}</span>
                </span>
                <span className="fsearch-size">{bytes(i.size)}</span>
                <span className="dim" title={i.deleted ? dayTime(i.deleted) : ''}>{i.deleted ? t('удалено {0}', ago(i.deleted)) : ''}</span>
              </label>
            ))}
            {items.length > limit && <button className="link fsearch-more" onClick={() => setLimit(l => l + 500)}>{t('Показать ещё {0}', items.length - limit)}</button>}
          </div>
        </>
      )}
    </div>
  );
}
