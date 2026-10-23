import { useEffect, useMemo, useState } from 'react';
import { api, type DevGroup, type DevItem, type DevService, type DevState, type DockerInfo, type NodeProject } from '../api';
import { ago, bytes, dayTime } from '../format';

type Tab = 'tools' | 'docker' | 'projects' | 'services' | 'path';

const TABS: { key: Tab; label: string }[] = [
  { key: 'tools', label: 'Инструменты и версии' },
  { key: 'docker', label: 'Docker' },
  { key: 'projects', label: 'Проекты Node' },
  { key: 'services', label: 'Службы' },
  { key: 'path', label: 'Команды и PATH' },
];

const dateFmt = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
const date = (ts: number) => dateFmt.format(ts * 1000);

function Support({ item }: { item: DevItem }) {
  switch (item.support) {
    case 'eol': return <span className="tag bad" title="Исправления безопасности больше не выходят">{item.until ? `без поддержки с ${date(item.until)}` : 'без поддержки'}</span>;
    case 'soon': return <span className="tag warn-tag">поддержка до {date(item.until!)}</span>;
    case 'lts': return <span className="tag good">LTS</span>;
    case 'ok': return <span className="tag good" title={item.until ? `Поддерживается до ${date(item.until)}` : undefined}>поддерживается</span>;
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

  return { state, error, refresh: () => setTick(t => t + 1), reload: () => api.dev(false).then(setState).catch(() => {}) };
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
    if (!confirm(`Запустить деинсталлятор «${i.name} ${i.version}»? Удаление подтверждается в его собственном окне.`)) return;
    await api.uninstall(i.uninstallId);
    setMsg(`Запущен деинсталлятор ${i.name} ${i.version}. После удаления нажмите «Проверить заново».`);
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
          <h1>Разработка</h1>
          <p>Какие SDK, среды выполнения и инструменты установлены, каких версий, сколько места занимают и когда заканчивается их поддержка. Плюс службы баз данных и то, какой файл реально запускается по команде.</p>
        </div>
        <div className="head-tools">
          {data && <span className="dim" style={{ fontSize: 13 }}>проверено {ago(data.scannedAt)}</span>}
          <button className="btn" disabled={state?.busy} onClick={refresh}>{state?.busy ? 'Проверяю…' : 'Проверить заново'}</button>
        </div>
      </div>

      {error && <div className="error">Не удалось получить данные: {error}</div>}
      {msg && <div className="note">{msg}</div>}
      {!data ? <div className="empty">{state?.busy || !state ? 'Ищу SDK, среды выполнения и инструменты… Первый раз это занимает до минуты: считаются размеры папок.' : 'Нет данных'}</div> : (
        <>
          <div className="facts">
            <div className="fact"><b>{all.length}</b><span>установлено компонентов</span></div>
            <div className="fact"><b style={{ color: eol ? 'var(--warn)' : 'var(--ok)' }}>{eol}</b><span>без поддержки — нет исправлений безопасности</span></div>
            <div className="fact"><b style={{ color: soon ? 'var(--cpu)' : undefined }}>{soon}</b><span>поддержка закончится в ближайшие полгода</span></div>
            <div className="fact"><b style={{ color: 'var(--disk)' }}>{bytes(total)}</b><span>занимают на диске</span></div>
          </div>

          <div className="filters" style={{ margin: '18px 0 14px' }}>
            <div className="seg" role="tablist">
              {TABS.map(t => (
                <button key={t.key} role="tab" aria-pressed={tab === t.key} onClick={() => setTab(t.key)}>
                  {t.label}
                  {t.key === 'docker' && (data.docker?.images.length ?? 0) > 0 && <span className="seg-count">{data.docker!.images.length}</span>}
                  {t.key === 'projects' && data.projects.length > 0 && <span className="seg-count">{data.projects.length}</span>}
                  {t.key === 'services' && running.length > 0 && <span className="seg-count">{running.length}</span>}
                  {t.key === 'path' && pathIssues > 0 && <span className="seg-count warn">{pathIssues}</span>}
                </button>
              ))}
            </div>
            {tab === 'tools' && (
              <>
                <input className="search" placeholder="Название или версия" value={q} onChange={e => setQ(e.target.value)} />
                <label className="check"><input type="checkbox" checked={onlyIssues} onChange={e => setOnlyIssues(e.target.checked)} />Только без поддержки и устаревающее</label>
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
                  {i.path && <button className="link dim" title={i.path} onClick={() => onOpen(i.path!)}>Папка</button>}
                  {i.uninstallId && <button className="link" onClick={() => onUninstall(i)}>Удалить</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

const MODE: Record<string, string> = { Auto: 'автоматически', Manual: 'вручную', Disabled: 'отключена', Boot: 'при загрузке', System: 'системой' };

function Services({ list, onAction }: { list: DevService[]; onAction: (s: DevService, a: 'start' | 'stop' | 'auto' | 'manual' | 'disabled') => void }) {
  if (!list.length) return <div className="panel"><div className="empty">Служб баз данных, брокеров и веб-серверов не найдено.</div></div>;
  const heavy = list.filter(s => s.state === 'Running' && s.startMode === 'Auto');
  return (
    <>
      {heavy.length > 0 && (
        <p className="hint-line" style={{ marginTop: 0, marginBottom: 12 }}>
          {heavy.length === 1 ? 'Служба запускается' : 'Службы запускаются'} вместе с Windows и {heavy.length === 1 ? 'держит' : 'держат'} память, даже если вы с ними сейчас не работаете.
          Тип запуска «вручную» освобождает ресурсы: служба стартует, только когда вы нажмёте «Запустить» здесь или в «Службах» Windows.
        </p>
      )}
      <div className="panel">
        <table className="table dev-table">
          <thead><tr><th style={{ textAlign: 'left' }}>Служба</th><th style={{ textAlign: 'left' }}>Состояние</th><th>Память</th><th style={{ textAlign: 'left' }}>Запуск</th><th /></tr></thead>
          <tbody>
            {list.map(s => (
              <tr key={s.name}>
                <td className="dev-name"><b>{s.display}</b><div className="dim">{s.name}</div></td>
                <td style={{ textAlign: 'left' }}>
                  <span className={`agent-dot ${s.state === 'Running' ? '' : 'off'}`} />{s.state === 'Running' ? 'работает' : s.state === 'Stopped' ? 'остановлена' : s.state}
                </td>
                <td>{s.memory ? bytes(s.memory) : '—'}</td>
                <td style={{ textAlign: 'left' }}>
                  <select className="select" value={s.startMode} onChange={e => onAction(s, e.target.value === 'Auto' ? 'auto' : e.target.value === 'Manual' ? 'manual' : 'disabled')}>
                    {['Auto', 'Manual', 'Disabled'].map(m => <option key={m} value={m}>{MODE[m]}</option>)}
                  </select>
                </td>
                <td className="dev-act">
                  {s.state === 'Running'
                    ? <button className="link" onClick={() => onAction(s, 'stop')}>Остановить</button>
                    : s.startMode !== 'Disabled' && <button className="link" onClick={() => onAction(s, 'start')}>Запустить</button>}
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
        <h2>Что запускается по команде <small>как это увидит новая консоль</small></h2>
        <div className="panel">
          <table className="table dev-table">
            <tbody>
              {data.commands.map(c => (
                <tr key={c.command}>
                  <td className="dev-name" style={{ width: 120 }}><code className="cmd">{c.command}</code></td>
                  <td className="dev-name">
                    <span>{c.path}</span>{c.version && <code>{c.version}</code>}
                    {c.note && <div className={c.note.startsWith('Это заглушка') ? 'act-bad' : 'dim'}>{c.note}</div>}
                    {c.shadowed.map(s => <div key={s} className="dim shadowed">заслонён: {s}</div>)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className="block">
        <h2>PATH <small>{data.path.length} папок{bad.length ? `, проблемных: ${bad.length}` : ''}</small></h2>
        <div className="panel">
          <table className="table dev-table">
            <tbody>
              {data.path.map((p, i) => (
                <tr key={i} className={!p.exists || p.duplicate ? '' : 'muted-row'}>
                  <td style={{ width: 36, textAlign: 'right' }} className="dim">{i + 1}</td>
                  <td className="dev-name" style={{ wordBreak: 'break-all' }}>{p.dir}</td>
                  <td style={{ textAlign: 'left', width: 120 }} className="dim">{p.scope === 'system' ? 'системный' : 'пользователя'}</td>
                  <td style={{ textAlign: 'left', width: 170 }}>
                    {!p.exists && <span className="tag bad">папки нет</span>}
                    {p.duplicate && <span className="tag">повтор</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="hint-line">Порядок важен: при одинаковых именах запускается файл из папки, которая выше. Изменить PATH: «Параметры → Система → О системе → Дополнительные параметры → Переменные среды».</p>
      </section>
    </>
  );
}

// ── Docker ──────────────────────────────────────────────────

function DockerView({ info, onDone }: { info: DockerInfo | null; onDone: (msg: string) => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  if (!info || !info.installed) return <div className="panel"><div className="empty">Docker не установлен.</div></div>;

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
          <div className="fact"><b>{info.images.length}</b><span>образов, {bytes(imagesSize)}</span></div>
          <div className="fact"><b>{info.containers.length}</b><span>контейнеров, остановлено {stopped}</span></div>
          <div className="fact"><b>{info.volumes.length}</b><span>томов, {bytes(info.volumes.reduce((s, v) => s + v.size, 0))}</span></div>
          <div className="fact"><b style={{ color: 'var(--disk)' }}>{info.diskFile ? bytes(info.diskFile) : '—'}</b><span>файл диска Docker на Windows</span></div>
        </div>
      )}
      {info.running && (
        <div className="filters" style={{ marginBottom: 14 }}>
          <button className="btn" disabled={!!busy || dangling === 0} onClick={() => act('prune-images', '', `Удалить ${dangling} образов без имени (<none>)? Их не использует ни один контейнер.`)}>Удалить образы без имени{dangling ? ` (${dangling})` : ''}</button>
          <button className="btn" disabled={!!busy || stopped === 0} onClick={() => act('prune-containers', '', `Удалить все остановленные контейнеры (${stopped})? Данные внутри них пропадут, тома останутся.`)}>Удалить остановленные контейнеры</button>
          <button className="btn" disabled={!!busy || info.buildCache === 0} onClick={() => act('prune-cache', '', 'Очистить кэш сборки? Следующая сборка образов будет дольше.')}>Очистить кэш сборки {info.buildCache ? `(${bytes(info.buildCache)})` : ''}</button>
        </div>
      )}

      {info.images.length > 0 && (
        <section className="block">
          <h2>Образы <small>Docker {info.version}</small></h2>
          <div className="panel">
            <table className="table dev-table">
              <thead><tr><th style={{ textAlign: 'left' }}>Образ</th><th style={{ textAlign: 'left' }}>Создан</th><th>Размер</th><th>Контейнеров</th><th /></tr></thead>
              <tbody>
                {info.images.map(i => (
                  <tr key={i.id}>
                    <td className="dev-name"><b>{i.repository === '<none>' ? 'без имени' : i.repository}</b>{i.tag && i.tag !== '<none>' && <code>{i.tag}</code>}<div className="dim">{i.id.replace('sha256:', '').slice(0, 12)}</div></td>
                    <td style={{ textAlign: 'left' }} className="dim">{i.created}</td>
                    <td style={{ color: 'var(--disk)' }}>{bytes(i.size)}{i.unique > 0 && i.unique < i.size && <div className="dim" style={{ fontSize: 12 }}>своих {bytes(i.unique)}</div>}</td>
                    <td>{i.containers || '—'}</td>
                    <td className="dev-act">
                      <button className="link" disabled={!!busy} onClick={() => act('rmi', i.id, `Удалить образ ${i.repository}:${i.tag}? Его можно будет скачать заново.`)}>Удалить</button>
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
          <h2>Контейнеры</h2>
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
                        ? <button className="link" disabled={!!busy} onClick={() => act('stop', c.id, `Остановить контейнер ${c.name}?`)}>Остановить</button>
                        : <button className="link" disabled={!!busy} onClick={() => act('rm', c.id, `Удалить контейнер ${c.name}? Данные внутри него (кроме томов) пропадут.`)}>Удалить</button>}
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
          <h2>Тома <small>здесь хранятся данные баз и сервисов</small></h2>
          <div className="panel">
            <table className="table dev-table">
              <tbody>
                {info.volumes.map(v => (
                  <tr key={v.name}>
                    <td className="dev-name"><b>{v.name}</b></td>
                    <td className="dim" style={{ textAlign: 'left' }}>{v.links ? `используют контейнеров: ${v.links}` : 'не используется'}</td>
                    <td style={{ color: 'var(--disk)' }}>{v.size ? bytes(v.size) : ''}</td>
                    <td className="dev-act">
                      {v.links === 0 && <button className="link" disabled={!!busy} onClick={() => act('volume-rm', v.name, `Удалить том ${v.name}? Данные в нём (например, база) пропадут безвозвратно.`)}>Удалить</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
      <p className="hint-line">Удаление освобождает место внутри Docker, но файл его диска на Windows сам не уменьшается. Сжать его: Docker Desktop → Troubleshoot → Clean / Purge data, либо Optimize-VHD для файла диска при выключенном Docker.</p>
    </>
  );
}

// ── Проекты Node ────────────────────────────────────────────

function Projects({ list, onDone, onOpen }: { list: NodeProject[]; onDone: (msg: string) => void; onOpen: (p: string) => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  if (!list.length) return <div className="panel"><div className="empty">Проектов с node_modules в папках разработки (C:\GIT, source\repos, Projects…) не найдено.</div></div>;
  const total = list.reduce((s, p) => s + p.modulesSize, 0);
  const del = async (p: NodeProject) => {
    if (!confirm(`Удалить node_modules в «${p.name}» (${bytes(p.modulesSize)})? Удаление без Корзины; вернуть — командой npm install в папке проекта.`)) return;
    setBusy(p.path);
    try { const r = await api.deleteNodeModules(p.path); onDone(r.message); } finally { setBusy(null); }
  };
  return (
    <>
      <p className="hint-line" style={{ marginTop: 0, marginBottom: 12 }}>
        Все node_modules вместе занимают <b style={{ color: 'var(--disk)' }}>{bytes(total)}</b>. Для давно не тронутых проектов их можно удалить и вернуть потом одной командой npm install.
      </p>
      <div className="panel">
        <table className="table dev-table">
          <thead><tr><th style={{ textAlign: 'left' }}>Проект</th><th>Зависимостей</th><th style={{ textAlign: 'left' }}>Изменён</th><th>node_modules</th><th /></tr></thead>
          <tbody>
            {list.map(p => (
              <tr key={p.path}>
                <td className="dev-name"><b>{p.name}</b><div className="dim">{p.path}</div></td>
                <td>{p.dependencies}{p.devDependencies ? <span className="dim"> + {p.devDependencies} dev</span> : ''}</td>
                <td style={{ textAlign: 'left' }} className="dim">{dayTime(p.lastModified)}</td>
                <td style={{ color: 'var(--disk)' }}>{bytes(p.modulesSize)}</td>
                <td className="dev-act">
                  <button className="link dim" onClick={() => onOpen(p.path)}>Папка</button>
                  <button className="link" disabled={busy === p.path} onClick={() => del(p)}>{busy === p.path ? 'Удаляю…' : 'Удалить node_modules'}</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
