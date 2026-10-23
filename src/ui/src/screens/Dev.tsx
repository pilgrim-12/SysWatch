import { useEffect, useMemo, useState } from 'react';
import { api, type DevGroup, type DevItem, type DevService, type DevState, type DockerInfo, type NodeProject } from '../api';
import { ago, bytes, dayTime } from '../format';
import { locale, t } from '../i18n';

type Tab = 'tools' | 'docker' | 'projects' | 'services' | 'path';

const TABS: { key: Tab; label: string }[] = [
  { key: 'tools', label: t('Инструменты и версии') },
  { key: 'docker', label: 'Docker' },
  { key: 'projects', label: t('Проекты Node') },
  { key: 'services', label: t('Службы') },
  { key: 'path', label: t('Команды и PATH') },
];

const dateFmt = new Intl.DateTimeFormat(locale(), { day: 'numeric', month: 'short', year: 'numeric' });
const date = (ts: number) => dateFmt.format(ts * 1000);

function Support({ item }: { item: DevItem }) {
  switch (item.support) {
    case 'eol': return <span className="tag bad" title={t('Исправления безопасности больше не выходят')}>{item.until ? t('без поддержки с {0}', date(item.until)) : t('без поддержки')}</span>;
    case 'soon': return <span className="tag warn-tag">{t('поддержка до {0}', date(item.until!))}</span>;
    case 'lts': return <span className="tag good">LTS</span>;
    case 'ok': return <span className="tag good" title={item.until ? t('Поддерживается до {0}', date(item.until)) : undefined}>{t('поддерживается')}</span>;
    default: return null;
  }
}

/** Загрузка с ожиданием фонового сканирования: пока агрегатор занят, опрашиваем раз в секунду. */
function useDev() {
  const [state, setState] = useState<DevState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let alive = true;
    let timer: ReturnType<typeof setTimeout>;
    const load = (refresh: boolean) => api.dev(refresh)
      .then(s => { if (!alive) return; setState(s); setError(null); if (s.busy) timer = setTimeout(() => load(false), 1000); })
      .catch(e => alive && setError(String(e?.message ?? e)));
    load(tick > 0);
    return () => { alive = false; clearTimeout(timer); };
  }, [tick]);

  return { state, error, refresh: () => setTick(n => n + 1), reload: () => api.dev(false).then(setState).catch(() => {}) };
}

export function Dev() {
  const { state, error, refresh, reload } = useDev();
  const [tab, setTab] = useState<Tab>('tools');
  const [q, setQ] = useState('');
  const [onlyIssues, setOnlyIssues] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const data = state?.data;

  const all = useMemo(() => data?.groups.flatMap(g => g.items) ?? [], [data]);
  const eol = all.filter(i => i.support === 'eol').length;
  const soon = all.filter(i => i.support === 'soon').length;
  const total = all.reduce((s, i) => s + (i.size ?? 0), 0);
  const running = data?.services.filter(s => s.state === 'Running') ?? [];
  const pathIssues = (data?.path.filter(p => !p.exists || p.duplicate).length ?? 0) + (data?.commands.filter(c => c.note).length ?? 0);

  const uninstall = async (i: DevItem) => {
    if (!i.uninstallId) return;
    if (!confirm(t('Запустить деинсталлятор «{0} {1}»? Удаление подтверждается в его собственном окне.', i.name, i.version))) return;
    await api.uninstall(i.uninstallId);
    setMsg(t('Запущен деинсталлятор {0} {1}. После удаления нажмите «Проверить заново».', i.name, i.version));
  };

  const service = async (s: DevService, action: 'start' | 'stop' | 'auto' | 'manual' | 'disabled') => {
    const r = await api.devService(s.name, action);
    setMsg(`${s.display}: ${r.message}`);
    setTimeout(reload, 2500);
  };

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Разработка')}</h1>
          <p>{t('Какие SDK, среды выполнения и инструменты установлены, каких версий, сколько места занимают и когда заканчивается их поддержка. Плюс службы баз данных и то, какой файл реально запускается по команде.')}</p>
        </div>
        <div className="head-tools">
          {data && <span className="dim" style={{ fontSize: 13 }}>{t('проверено {0}', ago(data.scannedAt))}</span>}
          <button className="btn" disabled={state?.busy} onClick={refresh}>{state?.busy ? t('Проверяю…') : t('Проверить заново')}</button>
        </div>
      </div>

      {error && <div className="error">{t('Не удалось получить данные: {0}', error)}</div>}
      {msg && <div className="note">{msg}</div>}
      {!data ? <div className="empty">{state?.busy || !state ? t('Ищу SDK, среды выполнения и инструменты… Первый раз это занимает до минуты: считаются размеры папок.') : t('Нет данных')}</div> : (
        <>
          <div className="facts">
            <div className="fact"><b>{all.length}</b><span>{t('установлено компонентов')}</span></div>
            <div className="fact"><b style={{ color: eol ? 'var(--warn)' : 'var(--ok)' }}>{eol}</b><span>{t('без поддержки — нет исправлений безопасности')}</span></div>
            <div className="fact"><b style={{ color: soon ? 'var(--cpu)' : undefined }}>{soon}</b><span>{t('поддержка закончится в ближайшие полгода')}</span></div>
            <div className="fact"><b style={{ color: 'var(--disk)' }}>{bytes(total)}</b><span>{t('занимают на диске')}</span></div>
          </div>

          <div className="filters" style={{ margin: '18px 0 14px' }}>
            <div className="seg" role="tablist">
              {TABS.map(tb => (
                <button key={tb.key} role="tab" aria-pressed={tab === tb.key} onClick={() => setTab(tb.key)}>
                  {tb.label}
                  {tb.key === 'docker' && (data.docker?.images.length ?? 0) > 0 && <span className="seg-count">{data.docker!.images.length}</span>}
                  {tb.key === 'projects' && data.projects.length > 0 && <span className="seg-count">{data.projects.length}</span>}
                  {tb.key === 'services' && running.length > 0 && <span className="seg-count">{running.length}</span>}
                  {tb.key === 'path' && pathIssues > 0 && <span className="seg-count warn">{pathIssues}</span>}
                </button>
              ))}
            </div>
            {tab === 'tools' && (
              <>
                <input className="search" placeholder={t('Название или версия')} value={q} onChange={e => setQ(e.target.value)} />
                <label className="check"><input type="checkbox" checked={onlyIssues} onChange={e => setOnlyIssues(e.target.checked)} />{t('Только без поддержки и устаревающее')}</label>
              </>
            )}
          </div>

          {tab === 'tools' && data.groups.map(g => (
            <GroupView key={g.id} group={g} q={q} onlyIssues={onlyIssues} onUninstall={uninstall}
              onOpen={p => api.devOpen(p).then(r => !r.ok && setMsg(r.message))} />
          ))}
          {tab === 'docker' && <DockerView info={data.docker} onDone={(m) => { setMsg(m); reload(); }} />}
          {tab === 'projects' && <Projects list={data.projects} onDone={(m) => { setMsg(m); reload(); }} onOpen={p => api.devOpen(p)} />}
          {tab === 'services' && <Services list={data.services} onAction={service} />}
          {tab === 'path' && <PathView data={data} />}
        </>
      )}
    </>
  );
}

function GroupView({ group, q, onlyIssues, onUninstall, onOpen }: {
  group: DevGroup; q: string; onlyIssues: boolean; onUninstall: (i: DevItem) => void; onOpen: (p: string) => void;
}) {
  const needle = q.toLowerCase();
  const items = group.items.filter(i =>
    (!needle || `${i.name} ${i.version} ${i.note ?? ''}`.toLowerCase().includes(needle)) &&
    (!onlyIssues || i.support === 'eol' || i.support === 'soon'));
  if (!items.length) return null;
  const size = group.items.reduce((s, i) => s + (i.size ?? 0), 0);

  return (
    <section className="block">
      <h2>{group.title} <small>{items.length}{size > 0 ? ` · ${bytes(size)}` : ''}</small></h2>
      {group.hint && <p className="hint-line" style={{ margin: '-4px 0 8px' }}>{group.hint}</p>}
      <div className="panel">
        <table className="table dev-table">
          <tbody>
            {items.map((i, n) => (
              <tr key={n}>
                <td className="dev-name">
                  <b>{i.name}</b>
                  {i.version && <code>{i.version}</code>}
                  {i.note && <div className="dim">{i.note}</div>}
                </td>
                <td className="dev-sup"><Support item={i} /></td>
                <td className="dev-size">{i.size ? bytes(i.size) : ''}</td>
                <td className="dev-act">
                  {i.path && <button className="link dim" title={i.path} onClick={() => onOpen(i.path!)}>{t('Папка')}</button>}
                  {i.uninstallId && <button className="link" onClick={() => onUninstall(i)}>{t('Удалить')}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

const MODE: Record<string, string> = { Auto: t('автоматически'), Manual: t('вручную'), Disabled: t('отключена'), Boot: t('при загрузке'), System: t('системой') };

function Services({ list, onAction }: { list: DevService[]; onAction: (s: DevService, a: 'start' | 'stop' | 'auto' | 'manual' | 'disabled') => void }) {
  if (!list.length) return <div className="panel"><div className="empty">{t('Служб баз данных, брокеров и веб-серверов не найдено.')}</div></div>;
  const heavy = list.filter(s => s.state === 'Running' && s.startMode === 'Auto');
  return (
    <>
      {heavy.length > 0 && (
        <p className="hint-line" style={{ marginTop: 0, marginBottom: 12 }}>
          {heavy.length === 1
            ? t('Служба запускается вместе с Windows и держит память, даже если вы с ней сейчас не работаете.')
            : t('Службы запускаются вместе с Windows и держат память, даже если вы с ними сейчас не работаете.')}
          {' '}{t('Тип запуска «вручную» освобождает ресурсы: служба стартует, только когда вы нажмёте «Запустить» здесь или в «Службах» Windows.')}
        </p>
      )}
      <div className="panel">
        <table className="table dev-table">
          <thead><tr><th style={{ textAlign: 'left' }}>{t('Служба')}</th><th style={{ textAlign: 'left' }}>{t('Состояние')}</th><th>{t('Память')}</th><th style={{ textAlign: 'left' }}>{t('Запуск')}</th><th /></tr></thead>
          <tbody>
            {list.map(s => (
              <tr key={s.name}>
                <td className="dev-name"><b>{s.display}</b><div className="dim">{s.name}</div></td>
                <td style={{ textAlign: 'left' }}>
                  <span className={`agent-dot ${s.state === 'Running' ? '' : 'off'}`} />{s.state === 'Running' ? t('работает') : s.state === 'Stopped' ? t('остановлена') : s.state}
                </td>
                <td>{s.memory ? bytes(s.memory) : '—'}</td>
                <td style={{ textAlign: 'left' }}>
                  <select className="select" value={s.startMode} onChange={e => onAction(s, e.target.value === 'Auto' ? 'auto' : e.target.value === 'Manual' ? 'manual' : 'disabled')}>
                    {['Auto', 'Manual', 'Disabled'].map(m => <option key={m} value={m}>{MODE[m]}</option>)}
                  </select>
                </td>
                <td className="dev-act">
                  {s.state === 'Running'
                    ? <button className="link" onClick={() => onAction(s, 'stop')}>{t('Остановить')}</button>
                    : s.startMode !== 'Disabled' && <button className="link" onClick={() => onAction(s, 'start')}>{t('Запустить')}</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function PathView({ data }: { data: NonNullable<DevState['data']> }) {
  const bad = data.path.filter(p => !p.exists || p.duplicate);
  return (
    <>
      <section className="block">
        <h2>{t('Что запускается по команде')} <small>{t('как это увидит новая консоль')}</small></h2>
        <div className="panel">
          <table className="table dev-table">
            <tbody>
              {data.commands.map(c => (
                <tr key={c.command}>
                  <td className="dev-name" style={{ width: 120 }}><code className="cmd">{c.command}</code></td>
                  <td className="dev-name">
                    <span>{c.path}</span>{c.version && <code>{c.version}</code>}
                    {c.note && <div className={/\\WindowsApps\\/i.test(c.path ?? '') && c.command.startsWith('python') ? 'act-bad' : 'dim'}>{c.note}</div>}
                    {c.shadowed.map(s => <div key={s} className="dim shadowed">{t('заслонён: {0}', s)}</div>)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className="block">
        <h2>PATH <small>{t('{0} папок', data.path.length)}{bad.length ? t(', проблемных: {0}', bad.length) : ''}</small></h2>
        <div className="panel">
          <table className="table dev-table">
            <tbody>
              {data.path.map((p, i) => (
                <tr key={i} className={!p.exists || p.duplicate ? '' : 'muted-row'}>
                  <td style={{ width: 36, textAlign: 'right' }} className="dim">{i + 1}</td>
                  <td className="dev-name" style={{ wordBreak: 'break-all' }}>{p.dir}</td>
                  <td style={{ textAlign: 'left', width: 120 }} className="dim">{p.scope === 'system' ? t('системный') : t('пользователя')}</td>
                  <td style={{ textAlign: 'left', width: 170 }}>
                    {!p.exists && <span className="tag bad">{t('папки нет')}</span>}
                    {p.duplicate && <span className="tag">{t('повтор')}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="hint-line">{t('Порядок важен: при одинаковых именах запускается файл из папки, которая выше. Изменить PATH: «Параметры → Система → О системе → Дополнительные параметры → Переменные среды».')}</p>
      </section>
    </>
  );
}

// ── Docker ──────────────────────────────────────────────────

function DockerView({ info, onDone }: { info: DockerInfo | null; onDone: (msg: string) => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  if (!info || !info.installed) return <div className="panel"><div className="empty">{t('Docker не установлен.')}</div></div>;

  const act = async (action: string, id: string, question: string) => {
    if (!confirm(question)) return;
    setBusy(action + id);
    try { const r = await api.dockerAction(action, id); onDone(r.message); }
    finally { setBusy(null); }
  };

  const imagesSize = info.images.reduce((s, i) => s + i.size, 0);
  const dangling = info.images.filter(i => i.repository === '<none>').length;
  const stopped = info.containers.filter(c => c.state !== 'running').length;

  return (
    <>
      {info.note && <div className="note">{info.note}</div>}
      {info.running && (
        <div className="facts" style={{ marginBottom: 16 }}>
          <div className="fact"><b>{info.images.length}</b><span>{t('образов, {0}', bytes(imagesSize))}</span></div>
          <div className="fact"><b>{info.containers.length}</b><span>{t('контейнеров, остановлено {0}', stopped)}</span></div>
          <div className="fact"><b>{info.volumes.length}</b><span>{t('томов, {0}', bytes(info.volumes.reduce((s, v) => s + v.size, 0)))}</span></div>
          <div className="fact"><b style={{ color: 'var(--disk)' }}>{info.diskFile ? bytes(info.diskFile) : '—'}</b><span>{t('файл диска Docker на Windows')}</span></div>
        </div>
      )}
      {info.running && (
        <div className="filters" style={{ marginBottom: 14 }}>
          <button className="btn" disabled={!!busy || dangling === 0} onClick={() => act('prune-images', '', t('Удалить {0} образов без имени (<none>)? Их не использует ни один контейнер.', dangling))}>{t('Удалить образы без имени')}{dangling ? ` (${dangling})` : ''}</button>
          <button className="btn" disabled={!!busy || stopped === 0} onClick={() => act('prune-containers', '', t('Удалить все остановленные контейнеры ({0})? Данные внутри них пропадут, тома останутся.', stopped))}>{t('Удалить остановленные контейнеры')}</button>
          <button className="btn" disabled={!!busy || info.buildCache === 0} onClick={() => act('prune-cache', '', t('Очистить кэш сборки? Следующая сборка образов будет дольше.'))}>{t('Очистить кэш сборки')} {info.buildCache ? `(${bytes(info.buildCache)})` : ''}</button>
        </div>
      )}

      {info.images.length > 0 && (
        <section className="block">
          <h2>{t('Образы')} <small>Docker {info.version}</small></h2>
          <div className="panel">
            <table className="table dev-table">
              <thead><tr><th style={{ textAlign: 'left' }}>{t('Образ')}</th><th style={{ textAlign: 'left' }}>{t('Создан')}</th><th>{t('Размер')}</th><th>{t('Контейнеров')}</th><th /></tr></thead>
              <tbody>
                {info.images.map(i => (
                  <tr key={i.id}>
                    <td className="dev-name"><b>{i.repository === '<none>' ? t('без имени') : i.repository}</b>{i.tag && i.tag !== '<none>' && <code>{i.tag}</code>}<div className="dim">{i.id.replace('sha256:', '').slice(0, 12)}</div></td>
                    <td style={{ textAlign: 'left' }} className="dim">{i.created}</td>
                    <td style={{ color: 'var(--disk)' }}>{bytes(i.size)}{i.unique > 0 && i.unique < i.size && <div className="dim" style={{ fontSize: 12 }}>{t('своих {0}', bytes(i.unique))}</div>}</td>
                    <td>{i.containers || '—'}</td>
                    <td className="dev-act">
                      <button className="link" disabled={!!busy} onClick={() => act('rmi', i.id, t('Удалить образ {0}:{1}? Его можно будет скачать заново.', i.repository, i.tag))}>{t('Удалить')}</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {info.containers.length > 0 && (
        <section className="block">
          <h2>{t('Контейнеры')}</h2>
          <div className="panel">
            <table className="table dev-table">
              <tbody>
                {info.containers.map(c => (
                  <tr key={c.id}>
                    <td className="dev-name"><b>{c.name}</b><div className="dim">{c.image}</div></td>
                    <td style={{ textAlign: 'left' }}><span className={`agent-dot ${c.state === 'running' ? '' : 'off'}`} />{c.status}</td>
                    <td>{c.size ? bytes(c.size) : ''}</td>
                    <td className="dev-act">
                      {c.state === 'running'
                        ? <button className="link" disabled={!!busy} onClick={() => act('stop', c.id, t('Остановить контейнер {0}?', c.name))}>{t('Остановить')}</button>
                        : <button className="link" disabled={!!busy} onClick={() => act('rm', c.id, t('Удалить контейнер {0}? Данные внутри него (кроме томов) пропадут.', c.name))}>{t('Удалить')}</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {info.volumes.length > 0 && (
        <section className="block">
          <h2>{t('Тома')} <small>{t('здесь хранятся данные баз и сервисов')}</small></h2>
          <div className="panel">
            <table className="table dev-table">
              <tbody>
                {info.volumes.map(v => (
                  <tr key={v.name}>
                    <td className="dev-name"><b>{v.name}</b></td>
                    <td className="dim" style={{ textAlign: 'left' }}>{v.links ? t('используют контейнеров: {0}', v.links) : t('не используется')}</td>
                    <td style={{ color: 'var(--disk)' }}>{v.size ? bytes(v.size) : ''}</td>
                    <td className="dev-act">
                      {v.links === 0 && <button className="link" disabled={!!busy} onClick={() => act('volume-rm', v.name, t('Удалить том {0}? Данные в нём (например, база) пропадут безвозвратно.', v.name))}>{t('Удалить')}</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
      <p className="hint-line">{t('Удаление освобождает место внутри Docker, но файл его диска на Windows сам не уменьшается. Сжать его: Docker Desktop → Troubleshoot → Clean / Purge data, либо Optimize-VHD для файла диска при выключенном Docker.')}</p>
    </>
  );
}

// ── Проекты Node ────────────────────────────────────────────

function Projects({ list, onDone, onOpen }: { list: NodeProject[]; onDone: (msg: string) => void; onOpen: (p: string) => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  if (!list.length) return <div className="panel"><div className="empty">{t('Проектов с node_modules в папках разработки ({0}) не найдено.', 'C:\\GIT, source\\repos, Projects…')}</div></div>;
  const total = list.reduce((s, p) => s + p.modulesSize, 0);
  const del = async (p: NodeProject) => {
    if (!confirm(t('Удалить node_modules в «{0}» ({1})? Удаление без Корзины; вернуть — командой npm install в папке проекта.', p.name, bytes(p.modulesSize)))) return;
    setBusy(p.path);
    try { const r = await api.deleteNodeModules(p.path); onDone(r.message); } finally { setBusy(null); }
  };
  return (
    <>
      <p className="hint-line" style={{ marginTop: 0, marginBottom: 12 }}>
        {t('Все node_modules вместе занимают')} <b style={{ color: 'var(--disk)' }}>{bytes(total)}</b>. {t('Для давно не тронутых проектов их можно удалить и вернуть потом одной командой npm install.')}
      </p>
      <div className="panel">
        <table className="table dev-table">
          <thead><tr><th style={{ textAlign: 'left' }}>{t('Проект')}</th><th>{t('Зависимостей')}</th><th style={{ textAlign: 'left' }}>{t('Изменён')}</th><th>node_modules</th><th /></tr></thead>
          <tbody>
            {list.map(p => (
              <tr key={p.path}>
                <td className="dev-name"><b>{p.name}</b><div className="dim">{p.path}</div></td>
                <td>{p.dependencies}{p.devDependencies ? <span className="dim"> + {p.devDependencies} dev</span> : ''}</td>
                <td style={{ textAlign: 'left' }} className="dim">{dayTime(p.lastModified)}</td>
                <td style={{ color: 'var(--disk)' }}>{bytes(p.modulesSize)}</td>
                <td className="dev-act">
                  <button className="link dim" onClick={() => onOpen(p.path)}>{t('Папка')}</button>
                  <button className="link" disabled={busy === p.path} onClick={() => del(p)}>{busy === p.path ? t('Удаляю…') : t('Удалить node_modules')}</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
