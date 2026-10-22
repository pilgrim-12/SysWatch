import { useEffect, useMemo, useRef, useState } from 'react';
import { api, type CleanupCategory, type DiskState, type DiskTree, type Growth } from '../api';
import { COLOR, ago, bytes, dayTime } from '../format';

const GROUPS: { key: CleanupCategory['group']; title: string; note: string }[] = [
  { key: 'system', title: 'Система', note: 'Безопасно удалять: всё это Windows и программы создадут заново при необходимости.' },
  { key: 'dev', title: 'Разработка', note: 'Кэши пакетов и старые сборки. Удаление безопасно, но следующая сборка будет дольше: пакеты скачаются заново.' },
  { key: 'review', title: 'Проверить вручную', note: 'Ничего не отмечено заранее: решать тебе.' },
];

const MODE: Record<CleanupCategory['mode'], string> = { delete: 'навсегда', recycle: 'в Корзину', none: 'вручную' };

function signed(n: number | null | undefined) {
  if (n == null || n === 0) return '';
  return (n > 0 ? '+' : '−') + bytes(Math.abs(n));
}

export function Disk() {
  const [state, setState] = useState<DiskState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [path, setPath] = useState<string | null>(null);
  const [tree, setTree] = useState<DiskTree | null>(null);
  const [growth, setGrowth] = useState<Growth | null>(null);
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [excluded, setExcluded] = useState<Set<string>>(new Set());
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [confirm, setConfirm] = useState(false);
  const prevStatus = useRef<string | null>(null);
  const prevCleanup = useRef<number | null>(null);

  // Состояние: раз в секунду, пока идёт работа, иначе раз в 15 секунд.
  useEffect(() => {
    let alive = true;
    let timer: number;
    const tick = async () => {
      try {
        const s = await api.disk();
        if (!alive) return;
        setState(s);
        setError(null);
        timer = window.setTimeout(tick, s.status === 'idle' ? 15_000 : 1000);
      } catch (e) {
        if (!alive) return;
        setError(String((e as Error).message ?? e));
        timer = window.setTimeout(tick, 5000);
      }
    };
    tick();
    return () => { alive = false; clearTimeout(timer); };
  }, []);

  const snapId = state?.snapshot?.id ?? null;

  // Дерево и рост перечитываем, когда появился новый снимок или сменилась папка.
  useEffect(() => {
    if (snapId == null) return;
    api.diskTree(path).then(setTree).catch(() => {});
  }, [snapId, path]);
  useEffect(() => {
    if (snapId == null) return;
    api.diskGrowth().then(setGrowth).catch(() => {});
  }, [snapId]);

  // Новый результат поиска мусора — отметки по умолчанию.
  useEffect(() => {
    const c = state?.cleanup;
    if (!c || c.scannedAt === prevCleanup.current) return;
    prevCleanup.current = c.scannedAt;
    setChecked(new Set(c.categories.filter(x => x.defaultOn && x.size > 0).map(x => x.id)));
    setExcluded(new Set());
    setConfirm(false);
  }, [state?.cleanup]);

  useEffect(() => { prevStatus.current = state?.status ?? null; }, [state?.status]);

  const busy = state != null && state.status !== 'idle';
  const cats = state?.cleanup?.categories ?? [];

  const selected = useMemo(() => {
    let forever = 0, recycle = 0;
    for (const c of cats) {
      if (!checked.has(c.id) || c.mode === 'none') continue;
      const size = c.items.filter(i => !excluded.has(i.path)).reduce((s, i) => s + i.size, 0);
      if (c.mode === 'delete') forever += size; else recycle += size;
    }
    return { forever, recycle, total: forever + recycle };
  }, [cats, checked, excluded]);

  const toggle = (set: Set<string>, key: string, setter: (s: Set<string>) => void) => {
    const n = new Set(set);
    if (n.has(key)) n.delete(key); else n.add(key);
    setter(n);
  };

  const run = async (fn: () => Promise<void>) => {
    try { await fn(); setState(await api.disk()); } catch (e) { setError(String((e as Error).message ?? e)); }
  };

  const clean = () => run(async () => {
    setConfirm(false);
    await api.diskClean([...checked], [...excluded]);
  });

  const drive = state?.drive;
  const used = drive ? drive.total - drive.free : 0;
  const snap = state?.snapshot;
  const crumbs = useMemo(() => {
    const root = drive?.root ?? 'C:\\';
    const p = tree?.path ?? root;
    const parts = p.slice(root.length).split('\\').filter(Boolean);
    return [{ label: root.replace('\\', ''), path: root }, ...parts.map((part, i) => ({ label: part, path: root + parts.slice(0, i + 1).join('\\') }))];
  }, [tree, drive]);

  return (
    <>
      <div className="head">
        <div>
          <h1>Диск</h1>
          <p>Куда уходит место и что можно безопасно удалить. Раз в сутки SysWatch сам делает снимок диска, когда компьютер не занят.</p>
        </div>
        <div className="head-tools">
          {busy ? (
            <>
              <span className="progress-text" title={state?.progress ?? ''}>{state?.progress ?? 'Работаю…'}</span>
              <button className="btn" onClick={() => run(api.diskCancel)}>Остановить</button>
            </>
          ) : (
            <button className="btn primary" onClick={() => run(api.diskScan)}>Сканировать диск</button>
          )}
        </div>
      </div>

      {(error || state?.error) && <div className="error">{error ?? state?.error}</div>}

      {drive && (
        <section className="block">
          <div className="drive-bar" role="img" aria-label={`Занято ${bytes(used)} из ${bytes(drive.total)}`}>
            {snap ? (
              <>
                <i style={{ width: `${(snap.scanned / drive.total) * 100}%`, background: COLOR.disk }} title="Файлы и папки" />
                <i style={{ width: `${(Math.min(snap.hidden, used) / drive.total) * 100}%`, background: '#8a5a52' }} title="Скрыто системой" />
              </>
            ) : (
              <i style={{ width: `${(used / drive.total) * 100}%`, background: COLOR.disk }} />
            )}
          </div>
          <div className="facts">
            <div className="fact"><b>{bytes(drive.free)}</b><span>свободно на {drive.root.replace('\\', '')} из {bytes(drive.total)}</span></div>
            <div className="fact">
              <b style={{ color: growth && growth.driveDelta > 0 ? 'var(--warn)' : undefined }}>{growth?.since ? signed(growth.driveDelta) || '0' : '—'}</b>
              <span>{growth?.since ? `занятое место с ${dayTime(growth.since)}` : 'изменение появится после второго снимка'}</span>
            </div>
            <div className="fact"><b>{snap?.shadow != null ? bytes(snap.shadow) : '—'}</b><span>точки восстановления Windows</span></div>
            <div className="fact"><b>{snap ? bytes(snap.hidden) : '—'}</b><span>занято, но не видно в папках</span></div>
          </div>
        </section>
      )}

      {!snap ? (
        <div className="panel empty">
          Снимка диска ещё нет. Нажми «Сканировать диск»: обход занимает 1–3 минуты и идёт с низким приоритетом, работе не мешает.
        </div>
      ) : (
        <div className="grid-2">
          <section className="block">
            <h2>Что занимает место <small>снимок {ago(snap.ts)}, {snap.files.toLocaleString('ru-RU')} файлов</small></h2>
            <div className="panel">
              <nav className="crumbs" aria-label="Путь">
                {crumbs.map((c, i) => (
                  <span key={c.path}>
                    {i > 0 && <span className="sep">\</span>}
                    <button className="link" onClick={() => setPath(c.path)} disabled={i === crumbs.length - 1}>{c.label}</button>
                  </span>
                ))}
                {tree?.delta ? <span className="delta" style={{ marginLeft: 'auto' }}>{signed(tree.delta)}</span> : null}
              </nav>
              {!tree ? <div className="empty">Загружаю…</div> : (
                <div className="tree">
                  {tree.items.map(it => (
                    <div key={it.path} className={`tree-row ${it.isDir ? 'dir' : 'file'}`}
                      onClick={() => it.isDir && setPath(it.path)} title={it.path}>
                      <span className="tree-name">{it.isDir ? '▸ ' : ''}{it.name}</span>
                      <span className="tree-bar"><i style={{ width: `${tree.size ? (it.size / tree.size) * 100 : 0}%` }} /></span>
                      <span className="tree-size">{bytes(it.size)}</span>
                      <span className={`delta ${it.delta && it.delta > 0 ? 'up' : ''}`}>{signed(it.delta)}</span>
                    </div>
                  ))}
                  {tree.other > 0 && (
                    <div className="tree-row other">
                      <span className="tree-name">Остальное — мелкие файлы и папки до 100 МБ</span>
                      <span className="tree-bar"><i style={{ width: `${tree.size ? (tree.other / tree.size) * 100 : 0}%` }} /></span>
                      <span className="tree-size">{bytes(tree.other)}</span>
                      <span />
                    </div>
                  )}
                </div>
              )}
            </div>
          </section>

          <section className="block">
            <h2>Что выросло <small>{growth?.since ? `с ${dayTime(growth.since)}` : ''}</small></h2>
            <div className="panel">
              {!growth?.since ? (
                <div className="empty">Сравнивать пока не с чем. Завтра здесь появятся папки, которые выросли за сутки, а через неделю — за неделю.</div>
              ) : !growth.items.length ? (
                <div className="empty">Ни одна папка не выросла больше чем на 300 МБ.</div>
              ) : growth.items.map(g => (
                <div key={g.path} className="grow-row" onClick={() => setPath(g.path.slice(0, g.path.lastIndexOf('\\')) || null)} title="Показать в дереве">
                  <span className="grow-path">{g.path}{g.isNew && <span className="tag new">новая</span>}</span>
                  <span className="delta up">{signed(g.delta)}</span>
                  <span className="dim">{bytes(g.size)}</span>
                </div>
              ))}
            </div>
          </section>
        </div>
      )}

      <section className="block">
        <h2>
          Можно очистить
          <small>{state?.cleanup ? `проверено ${ago(state.cleanup.scannedAt)}` : 'появится после сканирования'}</small>
          {state?.cleanup && !busy && (
            <button className="link" style={{ marginLeft: 'auto' }} onClick={() => run(api.diskCleanupScan)}>Пересчитать</button>
          )}
        </h2>

        {state?.lastClean && (
          <div className="note ok">
            Освобождено {bytes(state.lastClean.freed)}
            {state.lastClean.recycled > 0 && `, в Корзину отправлено файлов: ${state.lastClean.recycled.toLocaleString('ru-RU')}`}
            {state.lastClean.skipped > 0 && `. Пропущено занятых файлов: ${state.lastClean.skipped.toLocaleString('ru-RU')} — их держат открытые программы`}.
          </div>
        )}

        {GROUPS.map(g => {
          const list = cats.filter(c => c.group === g.key);
          if (!list.length) return null;
          return (
            <div key={g.key} className="panel clean-group">
              <div className="clean-group-head">
                <h3>{g.title}</h3>
                <span className="dim">{g.note}</span>
              </div>
              {list.map(c => {
                const isOpen = open.has(c.id);
                const can = c.mode !== 'none' && c.size > 0;
                return (
                  <div key={c.id} className="clean-row">
                    <div className="clean-main">
                      <input type="checkbox" aria-label={c.title} disabled={!can || busy}
                        checked={checked.has(c.id)} onChange={() => toggle(checked, c.id, setChecked)} />
                      <div className="clean-text">
                        <div className="clean-title">
                          {c.title}
                          {c.mode !== 'none' && <span className="tag">{MODE[c.mode]}</span>}
                        </div>
                        <div className="dim">{c.description}</div>
                        {c.hint && <div className="hint">{c.hint}</div>}
                      </div>
                      <div className="clean-size">
                        <b>{c.size > 0 ? bytes(c.size) : '—'}</b>
                        {c.items.length > 0 && (
                          <button className="link" onClick={() => toggle(open, c.id, setOpen)}>
                            {isOpen ? 'Скрыть' : c.itemsSelectable ? `Выбрать (${c.items.length})` : 'Где лежит'}
                          </button>
                        )}
                      </div>
                    </div>
                    {isOpen && (
                      <div className="clean-items">
                        {c.items.map(i => (
                          <label key={i.path} className="clean-item">
                            {c.itemsSelectable && (
                              <input type="checkbox" disabled={busy} checked={!excluded.has(i.path)}
                                onChange={() => toggle(excluded, i.path, setExcluded)} />
                            )}
                            <span className="clean-path" title={i.path}>{i.path}</span>
                            <span className="dim">{i.modified ? dayTime(i.modified) : ''}</span>
                            <span>{bytes(i.size)}</span>
                          </label>
                        ))}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          );
        })}

        {state?.cleanup && (
          <div className="clean-bar">
            <span>
              Выбрано <b>{bytes(selected.total)}</b>
              {selected.forever > 0 && <span className="dim">: навсегда {bytes(selected.forever)}</span>}
              {selected.recycle > 0 && <span className="dim">{selected.forever > 0 ? ',' : ':'} в Корзину {bytes(selected.recycle)}</span>}
            </span>
            {confirm ? (
              <span className="confirm">
                <span>Удалить выбранное? Удалённое навсегда вернуть нельзя.</span>
                <button className="btn danger" onClick={clean}>Да, очистить</button>
                <button className="btn" onClick={() => setConfirm(false)}>Отмена</button>
              </span>
            ) : (
              <button className="btn primary" disabled={busy || selected.total === 0} onClick={() => setConfirm(true)}>Очистить выбранное</button>
            )}
          </div>
        )}
      </section>
    </>
  );
}
