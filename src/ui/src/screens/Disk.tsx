import { useEffect, useMemo, useRef, useState } from 'react';
import { api, type CleanupCategory, type DiskState, type DiskTree, type Growth, type OpProgress } from '../api';
import { COLOR, ago, bytes, dayTime } from '../format';
import { Treemap } from '../components/Treemap';
import { locale, t } from '../i18n';

const GROUPS: { key: CleanupCategory['group']; title: string; note: string }[] = [
  { key: 'system', title: t('Система'), note: t('Безопасно удалять: всё это Windows и программы создадут заново при необходимости.') },
  { key: 'dev', title: t('Разработка'), note: t('Кэши пакетов и старые сборки. Удаление безопасно, но следующая сборка будет дольше: пакеты скачаются заново.') },
  { key: 'review', title: t('Проверить вручную'), note: t('Ничего не отмечено заранее: решать тебе.') },
];

const MODE: Record<CleanupCategory['mode'], string> = { delete: t('навсегда'), recycle: t('в Корзину'), none: t('вручную') };

function signed(n: number | null | undefined) {
  if (n == null || n === 0) return '';
  return (n > 0 ? '+' : '−') + bytes(Math.abs(n));
}

function clock(sec: number) {
  const s = Math.max(0, Math.round(sec));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
}

/** Доля обхода: сколько байт уже посчитано из занятых на диске. */
export function opFraction(op: OpProgress | null) {
  if (!op || op.phase !== 'disk' || op.target <= 0) return null;
  return Math.min(0.99, op.bytes / op.target);
}

function OpPanel({ op, root, onStop }: { op: OpProgress; root: string; onStop: () => void }) {
  const elapsed = Date.now() / 1000 - op.started;
  const frac = opFraction(op);
  const eta = frac && frac > 0.03 ? (elapsed / frac) * (1 - frac) : null;
  const drive = root.replace('\\', '');
  const title =
    op.phase === 'clean' ? t('Удаляю выбранное')
    : op.phase === 'cleanup' ? t('Шаг {0} из {1}: ищу, что можно очистить', op.step, op.steps)
    : op.phase === 'disk' ? (op.steps > 1 ? t('Шаг {0} из {1}: обхожу диск {2}', op.step, op.steps, drive) : t('Обхожу диск {0}', drive))
    : t('Начинаю');

  return (
    <div className="panel op-panel" role="status" aria-live="polite">
      <div className="op-top">
        <div>
          <h3>{title}</h3>
          <div className="dim">
            {op.phase === 'disk' && <>{t('{0} из {1}, {2} файлов, {3} папок, ', bytes(op.bytes), bytes(op.target), op.files.toLocaleString(locale()), op.dirs.toLocaleString(locale()))}</>}
            {t('прошло {0}', clock(elapsed))}{eta != null && <>{t(', осталось около {0}', clock(eta))}</>}
          </div>
        </div>
        <b className="op-pct">{frac != null ? `${Math.floor(frac * 100)}%` : ''}</b>
        <button className="btn" onClick={onStop}>{t('Остановить')}</button>
      </div>
      <div className={`op-bar ${frac == null ? 'indeterminate' : ''}`}>
        <i style={frac != null ? { width: `${frac * 100}%` } : undefined} />
      </div>
      {op.current && <div className="op-current" title={op.current}>{op.current}</div>}
      <div className="dim op-note">{t('Идёт с низким приоритетом и не мешает работе. Можно закрыть окно — сканирование продолжится.')}</div>
    </div>
  );
}

export function Disk() {
  const [root, setRoot] = useState<string | null>(null);
  const [view, setView] = useState<'map' | 'list'>('map');
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
        const s = await api.disk(root);
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
  }, [root]);

  const currentRoot = state?.root ?? 'C:\\';
  const selectDrive = (r: string) => { setRoot(r); setPath(null); setTree(null); setGrowth(null); };

  const snapId = state?.snapshot?.id ?? null;

  // Дерево и рост перечитываем, когда появился новый снимок или сменилась папка.
  useEffect(() => {
    if (snapId == null) { setTree(null); return; }
    api.diskTree(currentRoot, path).then(setTree).catch(() => {});
  }, [snapId, path, currentRoot]);
  useEffect(() => {
    if (snapId == null) { setGrowth(null); return; }
    api.diskGrowth(currentRoot).then(setGrowth).catch(() => {});
  }, [snapId, currentRoot]);

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
    try { await fn(); setState(await api.disk(root)); } catch (e) { setError(String((e as Error).message ?? e)); }
  };

  const clean = () => run(async () => {
    setConfirm(false);
    await api.diskClean([...checked], [...excluded]);
  });

  const drive = state?.drives.find(d => d.root === state.root) ?? null;
  const used = drive ? drive.total - drive.free : 0;
  const snap = state?.snapshot;
  const crumbs = useMemo(() => {
    const r = currentRoot;
    const p = tree?.path ?? r;
    const parts = p.slice(r.length).split('\\').filter(Boolean);
    return [{ label: r.replace('\\', ''), path: r }, ...parts.map((part, i) => ({ label: part, path: r + parts.slice(0, i + 1).join('\\') }))];
  }, [tree, currentRoot]);

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Диск')}</h1>
          <p>{t('Куда уходит место и что можно безопасно удалить. Раз в сутки SysWatch сам делает снимок диска, когда компьютер не занят.')}</p>
        </div>
        <div className="head-tools">
          {busy ? (
            <span className="progress-text">
              {state?.busyRoot && state.busyRoot !== state.root
                ? t('Сканирую {0}: {1}', state.busyRoot.replace('\\', ''), opFraction(state.op) != null ? Math.floor(opFraction(state.op)! * 100) + '%' : '…')
                : ''}
            </span>
          ) : (
            <button className="btn primary" onClick={() => run(() => api.diskScan(currentRoot))}>
              {t('Сканировать {0}', currentRoot.replace('\\', ''))}
            </button>
          )}
        </div>
      </div>

      {(error || state?.error) && <div className="error">{error ?? state?.error}</div>}

      {state && state.drives.length > 0 && (
        <div className="drives" role="group" aria-label={t('Диски')}>
          {state.drives.map(d => {
            const usedPct = d.total ? ((d.total - d.free) / d.total) * 100 : 0;
            return (
              <button key={d.root} className="drive-card" aria-pressed={d.root === state.root} onClick={() => selectDrive(d.root)}>
                <div className="drive-top"><b>{d.root.replace('\\', '')}</b><span>{d.label}{d.isSystem ? t(', системный') : ''}</span></div>
                <div className="mini-bar"><i className={usedPct > 90 ? 'full' : ''} style={{ width: `${usedPct}%` }} /></div>
                {state.busyRoot === d.root && (
                  <div className={`mini-progress ${opFraction(state.op) == null ? 'indeterminate' : ''}`}>
                    <i style={opFraction(state.op) != null ? { width: `${opFraction(state.op)! * 100}%` } : undefined} />
                  </div>
                )}
                <div className="drive-sub">
                  {t('свободно {0} из {1}', bytes(d.free), bytes(d.total))}
                  {state.busyRoot === d.root
                    ? t(', сканирую{0}', opFraction(state.op) != null ? ` ${Math.floor(opFraction(state.op)! * 100)}%` : '…')
                    : d.lastScan ? t(', снимок {0}', ago(d.lastScan)) : t(', снимка нет')}
                </div>
              </button>
            );
          })}
        </div>
      )}

      {busy && state?.op && state.busyRoot === state.root && (
        <OpPanel op={state.op} root={state.root} onStop={() => run(api.diskCancel)} />
      )}

      {drive && (
        <section className="block">
          <div className="drive-bar" role="img" aria-label={t('Занято {0} из {1}', bytes(used), bytes(drive.total))}>
            {snap ? (
              <>
                <i style={{ width: `${(snap.scanned / drive.total) * 100}%`, background: COLOR.disk }} title={t('Файлы и папки')} />
                <i style={{ width: `${(Math.min(snap.hidden, used) / drive.total) * 100}%`, background: '#8a5a52' }} title={t('Скрыто системой')} />
              </>
            ) : (
              <i style={{ width: `${(used / drive.total) * 100}%`, background: COLOR.disk }} />
            )}
          </div>
          <div className="facts">
            <div className="fact"><b>{bytes(drive.free)}</b><span>{t('свободно на {0} из {1}', drive.root.replace('\\', ''), bytes(drive.total))}</span></div>
            <div className="fact">
              <b style={{ color: growth && growth.driveDelta > 0 ? 'var(--warn)' : undefined }}>{growth?.since ? signed(growth.driveDelta) || '0' : '—'}</b>
              <span>{growth?.since ? t('занятое место с {0}', dayTime(growth.since)) : t('изменение появится после второго снимка')}</span>
            </div>
            <div className="fact"><b>{snap?.shadow != null ? bytes(snap.shadow) : '—'}</b><span>{t('точки восстановления Windows')}</span></div>
            <div className="fact"><b>{snap ? bytes(snap.hidden) : '—'}</b><span>{t('занято, но не видно в папках')}</span></div>
          </div>
        </section>
      )}

      {!snap ? (
        <div className="panel empty">
          {t('Снимка этого диска ещё нет. Нажми «Сканировать»: обход занимает от минуты до нескольких минут и идёт с низким приоритетом, работе не мешает.')}
        </div>
      ) : (
        <>
          <section className="block">
            <h2>
              {t('Что занимает место')} <small>{t('снимок {0}, {1} файлов', ago(snap.ts), snap.files.toLocaleString(locale()))}</small>
              <span className="seg" role="group" aria-label={t('Вид')} style={{ marginLeft: 'auto' }}>
                <button aria-pressed={view === 'map'} onClick={() => setView('map')}>{t('Карта')}</button>
                <button aria-pressed={view === 'list'} onClick={() => setView('list')}>{t('Список')}</button>
              </span>
            </h2>
            <div className="panel">
              <nav className="crumbs" aria-label={t('Путь')}>
                {crumbs.map((c, i) => (
                  <span key={c.path}>
                    {i > 0 && <span className="sep">\</span>}
                    <button className="link" onClick={() => setPath(c.path)} disabled={i === crumbs.length - 1}>{c.label}</button>
                  </span>
                ))}
                {tree?.delta ? <span className="delta" style={{ marginLeft: 'auto' }}>{signed(tree.delta)}</span> : null}
              </nav>
              {!tree ? <div className="empty">{t('Загружаю…')}</div> : view === 'map' ? (
                <Treemap items={tree.items} other={tree.other} total={tree.size} onOpen={setPath} />
              ) : (
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
                      <span className="tree-name">{t('Остальное — мелкие файлы и папки до 100 МБ')}</span>
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
            <h2>{t('Что выросло')} <small>{growth?.since ? t('с {0}', dayTime(growth.since)) : ''}</small></h2>
            <div className="panel">
              {!growth?.since ? (
                <div className="empty">{t('Сравнивать пока не с чем. Завтра здесь появятся папки, которые выросли за сутки, а через неделю — за неделю.')}</div>
              ) : !growth.items.length ? (
                <div className="empty">{t('Ни одна папка не выросла больше чем на 300 МБ.')}</div>
              ) : growth.items.map(g => (
                <div key={g.path} className="grow-row" onClick={() => { setPath(g.path.slice(0, g.path.lastIndexOf('\\')) || null); setView('list'); }} title={t('Показать в дереве')}>
                  <span className="grow-path">{g.path}{g.isNew && <span className="tag new">{t('новая')}</span>}</span>
                  <span className="delta up">{signed(g.delta)}</span>
                  <span className="dim">{bytes(g.size)}</span>
                </div>
              ))}
            </div>
          </section>
        </>
      )}

      {state && !drive?.isSystem ? null : (
      <section className="block">
        <h2>
          {t('Можно очистить')}
          <small>{state?.cleanup ? t('проверено {0}', ago(state.cleanup.scannedAt)) : t('появится после сканирования')}</small>
          {state?.cleanup && !busy && (
            <button className="link" style={{ marginLeft: 'auto' }} onClick={() => run(api.diskCleanupScan)}>{t('Пересчитать')}</button>
          )}
        </h2>

        {state?.lastClean && (
          <div className="note ok">
            {t('Освобождено {0}', bytes(state.lastClean.freed))}
            {state.lastClean.recycled > 0 && t(', в Корзину отправлено файлов: {0}', state.lastClean.recycled.toLocaleString(locale()))}
            {state.lastClean.skipped > 0 && t('. Пропущено занятых файлов: {0} — их держат открытые программы', state.lastClean.skipped.toLocaleString(locale()))}.
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
                            {isOpen ? t('Скрыть') : c.itemsSelectable ? t('Выбрать ({0})', c.items.length) : t('Где лежит')}
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
              {t('Выбрано')} <b>{bytes(selected.total)}</b>
              {selected.forever > 0 && <span className="dim">{t(': навсегда {0}', bytes(selected.forever))}</span>}
              {selected.recycle > 0 && <span className="dim">{selected.forever > 0 ? ',' : ':'} {t('в Корзину {0}', bytes(selected.recycle))}</span>}
            </span>
            {confirm ? (
              <span className="confirm">
                <span>{t('Удалить выбранное? Удалённое навсегда вернуть нельзя.')}</span>
                <button className="btn danger" onClick={clean}>{t('Да, очистить')}</button>
                <button className="btn" onClick={() => setConfirm(false)}>{t('Отмена')}</button>
              </span>
            ) : (
              <button className="btn primary" disabled={busy || selected.total === 0} onClick={() => setConfirm(true)}>{t('Очистить выбранное')}</button>
            )}
          </div>
        )}
      </section>
      )}
    </>
  );
}
