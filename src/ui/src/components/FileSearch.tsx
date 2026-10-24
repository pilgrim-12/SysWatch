import { useEffect, useMemo, useRef, useState } from 'react';
import { api, type SearchHit, type SearchQuery, type SearchState } from '../api';
import { bytes, dayTime } from '../format';
import { locale, t } from '../i18n';
import { describePath } from '../pathLabel';

const KINDS = [
  { id: '', label: () => t('Любые файлы') },
  { id: 'video', label: () => t('Видео') },
  { id: 'image', label: () => t('Фото и картинки') },
  { id: 'audio', label: () => t('Музыка и звук') },
  { id: 'doc', label: () => t('Документы') },
  { id: 'archive', label: () => t('Архивы и образы дисков') },
  { id: 'installer', label: () => t('Установщики и программы') },
  { id: 'disk', label: () => t('Диски виртуальных машин') },
  { id: 'code', label: () => t('Исходный код') },
];
const SIZES = [0, 10 << 20, 100 << 20, 1 << 30, 5 * (1 << 30)];
const DAYS = [0, 1, 7, 30, 365];

type Sort = 'size' | 'date' | 'name';

const folderOf = (p: string) => p.slice(0, p.lastIndexOf('\\')) || p;

/**
 * Поиск файлов: имя или маска, тип, размер, дата. Найденное — списком; «На карте» открывает
 * папку на карте диска и подсвечивает здания и плитки, где лежат файлы.
 */
export function FileSearch({ drives, onLocate }: {
  drives: string[];
  /** Показать на карте: файлы, которые отметить. */
  onLocate: (paths: string[]) => void;
}) {
  const [q, setQ] = useState<SearchQuery>({ text: '', root: 'all', kind: '', minSize: 0, days: 0, folders: false });
  const [state, setState] = useState<SearchState | null>(null);
  const [sort, setSort] = useState<Sort>('size');
  const [note, setNote] = useState<string | null>(null);
  const [showAll, setShowAll] = useState(false);
  const timer = useRef<number>();

  const poll = async () => {
    try {
      const s = await api.search();
      setState(s);
      if (s.running) timer.current = window.setTimeout(poll, 700);
    } catch { /* окно закрыто или приложение перезапускается */ }
  };
  useEffect(() => { poll(); return () => clearTimeout(timer.current); }, []);

  const canSearch = q.text.trim() !== '' || q.kind !== '' || q.minSize > 0 || q.days > 0;
  const start = async () => {
    if (!canSearch) return;
    clearTimeout(timer.current);
    setNote(null);
    setShowAll(false);
    await api.searchStart(q);
    poll();
  };
  const stop = async () => { await api.searchCancel(); poll(); };

  const hits = useMemo(() => {
    const h = [...(state?.hits ?? [])];
    if (sort === 'size') h.sort((a, b) => b.size - a.size);
    else if (sort === 'date') h.sort((a, b) => b.modified - a.modified);
    else h.sort((a, b) => a.name.localeCompare(b.name, locale()));
    return h;
  }, [state?.hits, sort]);
  const total = hits.reduce((s, h) => s + h.size, 0);
  const shown = showAll ? hits : hits.slice(0, 200);

  const reveal = async (h: SearchHit) => {
    const r = await api.revealPath(h.path);
    if (!r.ok) setNote(r.message);
  };

  const running = !!state?.running;
  const field = <K extends keyof SearchQuery>(k: K, v: SearchQuery[K]) => setQ(x => ({ ...x, [k]: v }));

  return (
    <div className="panel fsearch">
      <form className="fsearch-form" onSubmit={e => { e.preventDefault(); start(); }}>
        <input className="search fsearch-text" type="search" value={q.text} autoComplete="off" spellCheck={false}
          placeholder={t('Имя файла или маска: отчёт, *.mp4, IMG_*.jpg')} aria-label={t('Что искать')}
          onChange={e => field('text', e.target.value)} />
        <select className="select" value={q.root} onChange={e => field('root', e.target.value)} aria-label={t('Где искать')}>
          <option value="all">{t('Все диски')}</option>
          {drives.map(d => <option key={d} value={d}>{d.replace('\\', '')}</option>)}
        </select>
        <select className="select" value={q.kind} onChange={e => field('kind', e.target.value)} aria-label={t('Тип')}>
          {KINDS.map(k => <option key={k.id} value={k.id}>{k.label()}</option>)}
        </select>
        <select className="select" value={q.minSize} onChange={e => field('minSize', Number(e.target.value))} aria-label={t('Размер')}>
          {SIZES.map(s => <option key={s} value={s}>{s ? t('больше {0}', bytes(s, 0)) : t('любого размера')}</option>)}
        </select>
        <select className="select" value={q.days} onChange={e => field('days', Number(e.target.value))} aria-label={t('Дата изменения')}>
          {DAYS.map(d => <option key={d} value={d}>{
            d === 0 ? t('за всё время') : d === 1 ? t('за сутки') : d === 7 ? t('за неделю') : d === 30 ? t('за месяц') : t('за год')
          }</option>)}
        </select>
        <label className="fsearch-check" title={t('Искать и папки с таким именем (без фильтров типа и размера)')}>
          <input type="checkbox" checked={q.folders} onChange={e => field('folders', e.target.checked)} /> {t('и папки')}
        </label>
        {running
          ? <button type="button" className="btn" onClick={stop}>{t('Остановить')}</button>
          : <button type="submit" className="btn primary" disabled={!canSearch}>{t('Найти')}</button>}
      </form>

      {state && (state.running || state.query) && (
        <div className="fsearch-status" role="status" aria-live="polite">
          {running ? (
            <>
              <span className="spin" aria-hidden />
              {t('Ищу: просмотрено {0} папок и {1} файлов, найдено {2}', state.dirs.toLocaleString(locale()), state.files.toLocaleString(locale()), state.hits.length)}
              {state.current && <span className="fsearch-current" title={state.current}>{state.current}</span>}
            </>
          ) : (
            <>
              {state.hits.length
                ? t('Найдено {0} на {1} за {2} с', state.hits.length.toLocaleString(locale()), bytes(total), state.seconds.toFixed(1))
                : t('Ничего не найдено. Просмотрено файлов: {0}.', state.files.toLocaleString(locale()))}
              {state.truncated && <span className="dim">{t(' Показаны первые {0} — уточните запрос.', state.hits.length)}</span>}
            </>
          )}
          {state.error && <span className="warn-text"> {state.error}</span>}
          {hits.length > 1 && (
            <button className="link" style={{ marginLeft: 'auto' }} onClick={() => onLocate(hits.map(h => h.path))}>
              {t('Показать все на карте')}
            </button>
          )}
        </div>
      )}
      {note && <div className="note">{note}</div>}

      {hits.length > 0 && (
        <div className="fsearch-list">
          <div className="fsearch-row head">
            <button className="link" aria-pressed={sort === 'name'} onClick={() => setSort('name')}>{t('Имя и папка')}</button>
            <button className="link" aria-pressed={sort === 'size'} onClick={() => setSort('size')}>{t('Размер')}</button>
            <button className="link" aria-pressed={sort === 'date'} onClick={() => setSort('date')}>{t('Изменён')}</button>
            <span />
          </div>
          {shown.map(h => {
            const info = describePath(h.path);
            return (
              <div key={h.path} className="fsearch-row">
                <span className="fsearch-name" title={h.path}>
                  <b>{h.isDir ? '▸ ' : ''}{h.name}</b>
                  <span className="path-sub">{folderOf(h.path)}{info && <em className="tree-label"> · {info.label}</em>}</span>
                </span>
                <span className="fsearch-size">{h.isDir ? '—' : bytes(h.size)}</span>
                <span className="dim">{h.modified ? dayTime(h.modified) : ''}</span>
                <span className="fsearch-act">
                  <button className="link" onClick={() => onLocate([h.path])}>{t('На карте')}</button>
                  <button className="link" onClick={() => reveal(h)}>{t('В проводнике')}</button>
                </span>
              </div>
            );
          })}
          {hits.length > shown.length && (
            <button className="link fsearch-more" onClick={() => setShowAll(true)}>{t('Показать ещё {0}', hits.length - shown.length)}</button>
          )}
        </div>
      )}
    </div>
  );
}
