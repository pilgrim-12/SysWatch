import { useEffect, useMemo, useState } from 'react';
import { api, type FolderListing } from '../api';
import { bytes, dayTime } from '../format';
import { locale, t } from '../i18n';
import { countUnder } from '../marks';

/**
 * Что лежит в папке прямо сейчас — каждый файл, а не только крупное из снимка.
 * Подпапки из снимка открываются на карте; мелкие (меньше 100 МБ) — прямо здесь.
 */
export function FolderFiles({ path, onOpen, marks = [] }: {
  path: string;
  /** Открыть папку на карте диска (она есть в снимке). */
  onOpen: (path: string) => void;
  marks?: readonly string[];
}) {
  const [sub, setSub] = useState<string | null>(null);
  const [list, setList] = useState<FolderListing | null>(null);
  const [filter, setFilter] = useState('');
  const [hidden, setHidden] = useState(false);
  const [limit, setLimit] = useState(150);
  const [note, setNote] = useState<string | null>(null);
  const here = sub ?? path;

  useEffect(() => { setSub(null); }, [path]);
  useEffect(() => {
    let alive = true;
    setList(null); setLimit(150); setNote(null);
    api.diskList(here).then(l => { if (alive) setList(l); }).catch(e => alive && setNote(String((e as Error).message ?? e)));
    return () => { alive = false; };
  }, [here]);

  const rows = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (list?.entries ?? []).filter(e => (hidden || !e.hidden) && (!f || e.name.toLowerCase().includes(f)));
  }, [list, filter, hidden]);
  const files = rows.filter(r => !r.isDir).length;
  const hiddenCount = (list?.entries ?? []).filter(e => e.hidden).length;

  const open = (p: string, inSnapshot: boolean) => { if (inSnapshot) onOpen(p); else setSub(p); };
  const up = () => {
    if (!sub) return;
    const parent = sub.slice(0, sub.lastIndexOf('\\'));
    setSub(parent.length > path.length ? parent : null);
  };
  const reveal = async (p: string) => { const r = await api.revealPath(p); if (!r.ok) setNote(r.message); };

  return (
    <div className="ffiles">
      <div className="ffiles-head">
        <h3>{t('Файлы в папке')}</h3>
        {sub && <button className="link" onClick={up}>{t('↑ на уровень выше')}</button>}
        <span className="ffiles-path" title={here}>{here}</span>
        <input className="search" type="search" value={filter} placeholder={t('Фильтр по имени')} aria-label={t('Фильтр по имени')}
          onChange={e => setFilter(e.target.value)} />
        {hiddenCount > 0 && (
          <label className="fsearch-check"><input type="checkbox" checked={hidden} onChange={e => setHidden(e.target.checked)} /> {t('скрытые ({0})', hiddenCount)}</label>
        )}
        <button className="link" onClick={() => reveal(here)}>{t('Открыть в проводнике')}</button>
      </div>
      {note && <div className="note">{note}</div>}
      {!list ? <div className="empty">{t('Загружаю…')}</div> : !list.exists ? (
        <div className="empty">{t('Папки больше нет: её удалили или переместили после снимка.')}</div>
      ) : !rows.length ? (
        <div className="empty">{filter ? t('Ничего не подходит под фильтр.') : t('Папка пуста.')}</div>
      ) : (
        <>
          <div className="dim ffiles-sum">
            {t('Файлов: {0} на {1}', files.toLocaleString(locale()), bytes(list.filesSize))}
            {list.truncated && t(' — показаны крупнейшие {0}', list.entries.length)}
          </div>
          <div className="ffiles-list">
            {rows.slice(0, limit).map(e => {
              const known = e.isDir && e.size >= 0;
              const found = marks.length ? countUnder(marks, e.path) : 0;
              return (
                <div key={e.path} className={`ffiles-row ${e.isDir ? 'dir' : 'file'}${e.hidden ? ' hid' : ''}${found ? ' marked' : ''}`}
                  onDoubleClick={() => (e.isDir ? open(e.path, known) : reveal(e.path))}>
                  <span className="ffiles-name" title={e.path}>
                    {e.isDir
                      ? <button className="link" onClick={() => open(e.path, known)}>▸ {e.name}</button>
                      : <span>{e.name}</span>}
                    {found > 0 && <span className="pin inline">{found}</span>}
                  </span>
                  <span className="ffiles-size">{e.isDir ? (known ? bytes(e.size) : t('< 100 МБ')) : bytes(e.size)}</span>
                  <span className="dim">{e.modified ? dayTime(e.modified) : ''}</span>
                  <button className="link" onClick={() => reveal(e.path)} title={t('Показать в проводнике')}>{t('Показать')}</button>
                </div>
              );
            })}
            {rows.length > limit && <button className="link fsearch-more" onClick={() => setLimit(l => l + 500)}>{t('Показать ещё {0}', rows.length - limit)}</button>}
          </div>
        </>
      )}
    </div>
  );
}
