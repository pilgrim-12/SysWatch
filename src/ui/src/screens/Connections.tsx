import { useEffect, useMemo, useState } from 'react';
import { ConnMap } from '../components/ConnMap';
import { api, type BlockedApp, type ConnRow, type ConnSnapshot } from '../api';
import { appLabel, countryName, dayTime } from '../format';
import { useLoad } from '../hooks';
import { t } from '../i18n';

type Dir = 'all' | ConnRow['dir'];

const DIRS: { key: Dir; label: string }[] = [
  { key: 'all', label: t('Все') },
  { key: 'out', label: t('Исходящие') },
  { key: 'in', label: t('Входящие') },
  { key: 'listen', label: t('Слушают порт') },
];

const ARROW: Record<ConnRow['dir'], { sign: string; title: string; cls: string }> = {
  out: { sign: '↗', title: t('Исходящее: программа подключилась к серверу'), cls: 'out' },
  in: { sign: '↙', title: t('Входящее: к программе подключились извне'), cls: 'in' },
  listen: { sign: '◉', title: t('Программа ждёт входящих подключений на этом порту'), cls: 'listen' },
};

function age(from: number, now: number) {
  const s = Math.max(0, now - from);
  if (s < 60) return t('{0} с', s);
  if (s < 3600) return t('{0} мин', Math.floor(s / 60));
  return t('{0} ч {1} мин', Math.floor(s / 3600), Math.floor((s % 3600) / 60));
}

const isLocal = (r: ConnRow) =>
  r.domain === 'localhost' || r.remote === '127.0.0.1' || r.remote === '::1' ||
  (r.dir === 'listen' && (r.local === '127.0.0.1' || r.local === '::1'));

export function Connections({ onApp }: { onApp: (name: string) => void }) {
  const [snap, setSnap] = useState<ConnSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dir, setDir] = useState<Dir>('out');
  const [q, setQ] = useState('');
  const [hideLocal, setHideLocal] = useState(true);
  const [paused, setPaused] = useState(false);
  const [onlyExposed, setOnlyExposed] = useState(false);
  const [geoBusy, setGeoBusy] = useState(false);
  const [view, setView] = useState<'table' | 'map'>(() => { try { return localStorage.getItem('conn_view') === 'map' ? 'map' : 'table'; } catch { return 'table'; } });
  const switchView = (v: 'table' | 'map') => { setView(v); try { localStorage.setItem('conn_view', v); } catch { /* не страшно */ } };
  const blocked = useLoad(() => api.blocked(), [], 30_000);

  useEffect(() => {
    if (paused) return;
    let alive = true;
    const tick = () => api.connections()
      .then(s => { if (alive) { setSnap(s); setError(null); } })
      .catch(e => alive && setError(String(e?.message ?? e)));
    tick();
    const id = setInterval(tick, 1000);
    return () => { alive = false; clearInterval(id); };
  }, [paused]);

  const rows = useMemo(() => {
    const needle = q.toLowerCase();
    return (snap?.rows ?? [])
      .filter(r => dir === 'all' || r.dir === dir)
      .filter(r => !hideLocal || !isLocal(r))
      .filter(r => !onlyExposed || r.exposed)
      .filter(r => !needle || r.app.toLowerCase().includes(needle) || (r.domain ?? '').toLowerCase().includes(needle)
        || r.remote.includes(needle) || String(r.remotePort) === needle || String(r.localPort) === needle
        || countryName(r.country).toLowerCase().includes(needle))
      .slice(0, 400);
  }, [snap, dir, q, hideLocal, onlyExposed]);

  const downloadGeo = async () => {
    setGeoBusy(true);
    try { await api.geoDownload(); } finally { setGeoBusy(false); }
  };

  const now = snap?.ts ?? Math.floor(Date.now() / 1000);

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Соединения')}</h1>
          <p>{t('Живая таблица TCP и UDP, обновляется каждую секунду. Содержимое HTTPS зашифровано — видно, какая программа, куда и когда подключилась.')}</p>
        </div>
        <div className="head-tools">
          <input className="search" placeholder={t('Программа, домен, IP или порт')} value={q} onChange={e => setQ(e.target.value)} />
          <button className="btn" aria-pressed={paused} onClick={() => setPaused(p => !p)}>{paused ? t('Продолжить') : t('Пауза')}</button>
        </div>
      </div>

      {error && <div className="error">{t('Не удалось получить соединения: {0}', error)}</div>}

      {snap && !snap.geoAvailable && (
        <div className="note geo-note">
          <span>{t('Чтобы видеть страну каждого адреса, нужна база стран: около 7 МБ с db-ip.com, скачивается один раз и дальше работает без интернета.')}</span>
          <button className="btn" disabled={geoBusy} onClick={downloadGeo}>{geoBusy ? t('Скачиваю…') : t('Скачать базу стран')}</button>
          {snap.geoStatus && !geoBusy && <span className="act-bad">{snap.geoStatus}</span>}
        </div>
      )}

      {snap && (
        <div className="facts">
          <div className="fact"><b style={{ color: 'var(--net)' }}>{snap.out}</b><span>{t('исходящих соединений')}</span></div>
          <div className="fact"><b style={{ color: snap.in > 0 ? 'var(--cpu)' : undefined }}>{snap.in}</b><span>{t('входящих соединений')}</span></div>
          <div className="fact"><b>{snap.listen}</b><span>{t('портов ждут подключений')}</span></div>
          <div className="fact">
            <b style={{ color: snap.exposed > 0 ? 'var(--warn)' : undefined }}>{snap.exposed}</b>
            <span>{t('TCP-портов открыто в сеть, не только для этого компьютера')}</span>
          </div>
        </div>
      )}

      <div className="filters" style={{ marginBottom: 12 }}>
        <div className="seg" role="group" aria-label={t('Направление')}>
          {DIRS.map(d => <button key={d.key} aria-pressed={dir === d.key} onClick={() => setDir(d.key)}>{d.label}</button>)}
        </div>
        <label className="check">
          <input type="checkbox" checked={hideLocal} onChange={e => setHideLocal(e.target.checked)} />
          {t('Скрыть соединения внутри компьютера (localhost)')}
        </label>
        <label className="check">
          <input type="checkbox" checked={onlyExposed} onChange={e => setOnlyExposed(e.target.checked)} />
          {t('Только доступное из сети')}
        </label>
        <div className="seg" role="group" aria-label={t('Вид')} style={{ marginLeft: 'auto' }}>
          <button aria-pressed={view === 'table'} onClick={() => switchView('table')}>{t('Таблица')}</button>
          <button aria-pressed={view === 'map'} onClick={() => switchView('map')}>{t('Карта')}</button>
        </div>
      </div>

      {view === 'map' ? <ConnMap rows={rows} onApp={onApp} /> : (

      <div className="panel scroll" style={{ maxHeight: 'calc(100vh - 420px)', minHeight: 320 }}>
        {!rows.length ? <div className="empty">{snap ? t('Нет соединений под этот фильтр.') : t('Загружаю…')}</div> : (
          <table className="table conn-table">
            <thead>
              <tr>
                <th style={{ width: 28 }} />
                <th style={{ textAlign: 'left' }}>{t('Программа')}</th>
                <th style={{ textAlign: 'left' }}>{dir === 'listen' ? t('Адрес') : t('Удалённый адрес')}</th>
                <th>{t('Порт')}</th>
                <th style={{ textAlign: 'left' }}>{dir === 'listen' ? t('Кому доступен') : t('Страна')}</th>
                <th style={{ textAlign: 'left' }}>{t('Состояние')}</th>
                <th>{t('Длится')}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(r => {
                const a = ARROW[r.dir];
                const fresh = r.closedAt == null && now - r.firstSeen <= 2;
                return (
                  <tr key={r.key} className={`${fresh ? 'fresh' : ''} ${r.closedAt ? 'closed' : ''}`} onClick={() => onApp(r.app)}>
                    <td className={`conn-dir ${a.cls}`} title={a.title}>{a.sign}</td>
                    <td><span className="name" title={r.path ?? r.app}>{appLabel(r.app)}<span className="count">{r.pid}</span></span></td>
                    <td style={{ textAlign: 'left' }}>
                      {r.dir === 'listen' ? (
                        <span>{r.local === '0.0.0.0' || r.local === '::' ? t('все сетевые адреса') : r.local}</span>
                      ) : (
                        <span title={r.remote}>
                          {r.domain ?? r.remote}
                          {r.domain && <span className="dim"> {r.remote}</span>}
                        </span>
                      )}
                    </td>
                    <td>
                      {r.dir === 'listen' ? `${r.proto} ${r.localPort}` : r.dir === 'in' ? `← ${r.localPort}` : r.remotePort}
                    </td>
                    <td style={{ textAlign: 'left' }}>
                      {r.dir === 'out'
                        ? <span className="dim">{countryName(r.country)}</span>
                        : r.exposed
                          ? <span className="tag bad" title={t('Подключиться может любое устройство в сети, если Брандмауэр это разрешает')}>{t('из сети')}</span>
                          : <span className="tag">{t('только этот ПК')}</span>}
                      {r.dir === 'in' && r.country && <span className="dim"> {countryName(r.country)}</span>}
                    </td>
                    <td style={{ textAlign: 'left' }} className="dim">{r.state}</td>
                    <td className="dim">{r.closedAt ? t('закрыто') : age(r.firstSeen, now)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
      )}

      {(blocked.data?.length ?? 0) > 0 && (
        <section className="block" style={{ marginTop: 24 }}>
          <h2>{t('Выход в сеть запрещён')} <small>{t('правилами Брандмауэра Windows от SysWatch')}</small></h2>
          <div className="panel">
            {blocked.data!.map((b: BlockedApp) => (
              <div key={b.path} className="grow-row" style={{ cursor: 'default' }}>
                <span className="grow-path" title={b.path}>{b.path}</span>
                <span className="dim">{t('с {0}', dayTime(b.since))}</span>
                <button className="link" onClick={async () => { await api.unblock(b.path); blocked.reload(); }}>{t('Разрешить')}</button>
              </div>
            ))}
          </div>
        </section>
      )}

      {snap?.geoAvailable && <p className="attrib">{t('Страны по IP:')} <a href="https://db-ip.com" target="_blank" rel="noreferrer">IP Geolocation by DB-IP</a> (CC BY 4.0).</p>}
    </>
  );
}
