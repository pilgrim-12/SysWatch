import { useCallback, useEffect, useState } from 'react';
import { api, type Live } from './api';
import { ago, bytes, pct } from './format';
import { Scopes, emptyHistory, pushLive, type LiveHistory } from './components/Scopes';
import { BrandMark, IconAlert, IconApps, IconChip, IconDisk, IconHeat, IconLink, IconList, IconNet, IconClock, IconCode, IconPulse, IconReport } from './components/Icons';
import { Now } from './screens/Now';
import { Timeline } from './screens/Timeline';
import { Network } from './screens/Network';
import { Events } from './screens/Events';
import { AppDetail } from './screens/AppDetail';
import { Anomalies } from './screens/Anomalies';
import { Disk } from './screens/Disk';
import { Connections } from './screens/Connections';
import { Programs } from './screens/Programs';
import { System } from './screens/System';
import { Report } from './screens/Report';
import { Dev } from './screens/Dev';
import { History } from './screens/History';

type Screen = 'now' | 'connections' | 'timeline' | 'network' | 'anomalies' | 'disk' | 'programs' | 'system' | 'report' | 'dev' | 'history' | 'events';

const NAV: { key: Screen; label: string; icon: () => JSX.Element }[] = [
  { key: 'now', label: 'Сейчас', icon: IconPulse },
  { key: 'connections', label: 'Соединения', icon: IconLink },
  { key: 'timeline', label: 'Таймлайн', icon: IconHeat },
  { key: 'network', label: 'Сеть', icon: IconNet },
  { key: 'anomalies', label: 'Аномалии', icon: IconAlert },
  { key: 'disk', label: 'Диск', icon: IconDisk },
  { key: 'programs', label: 'Программы', icon: IconApps },
  { key: 'system', label: 'Система', icon: IconChip },
  { key: 'dev', label: 'Разработка', icon: IconCode },
  { key: 'history', label: 'История', icon: IconClock },
  { key: 'report', label: 'Отчёт', icon: IconReport },
  { key: 'events', label: 'События', icon: IconList },
];

function readPref<T>(key: string, fallback: T): T {
  try { const v = localStorage.getItem(key); return v ? (JSON.parse(v) as T) : fallback; } catch { return fallback; }
}
function writePref(key: string, v: unknown) {
  try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* недоступно — не страшно */ }
}

export function App() {
  const [screen, setScreen] = useState<Screen>(() => readPref('screen', 'now'));
  const [range, setRange] = useState<number>(() => readPref('range', 60));
  const [app, setApp] = useState<string | null>(null);
  const [live, setLive] = useState<Live | null>(null);
  const [hist, setHist] = useState<LiveHistory>(emptyHistory);
  const [liveError, setLiveError] = useState<string | null>(null);

  const [unseen, setUnseen] = useState(0);

  useEffect(() => writePref('screen', screen), [screen]);

  // Переход по ссылке вида #anomalies — из уведомления или меню в трее.
  useEffect(() => {
    const fromHash = () => {
      const h = location.hash.slice(1) as Screen;
      if (NAV.some(n => n.key === h)) { setScreen(h); setApp(null); }
    };
    fromHash();
    window.addEventListener('hashchange', fromHash);
    return () => window.removeEventListener('hashchange', fromHash);
  }, []);

  // Счётчик новых аномалий для меню.
  const refreshUnseen = useCallback(() => {
    api.anomalies(60).then(a => setUnseen(a.unseen)).catch(() => {});
  }, []);
  useEffect(() => {
    refreshUnseen();
    const id = setInterval(refreshUnseen, 30_000);
    return () => clearInterval(id);
  }, [refreshUnseen]);
  useEffect(() => writePref('range', range), [range]);

  // Живые данные раз в секунду — только пока окно открыто.
  useEffect(() => {
    let alive = true;
    const tick = () => api.live()
      .then(l => { if (!alive) return; setLive(l); setHist(h => pushLive(h, l)); setLiveError(null); })
      .catch(e => alive && setLiveError(String(e?.message ?? e)));
    tick();
    const id = setInterval(tick, 1000);
    return () => { alive = false; clearInterval(id); };
  }, []);

  const go = (s: Screen) => { setScreen(s); setApp(null); if (location.hash) history.replaceState(null, '', location.pathname); };
  const agent = live?.agent;

  return (
    <div className="shell">
      <nav className="rail" aria-label="Разделы">
        <div className="brand"><BrandMark /> SysWatch</div>
        {NAV.map(n => (
          <button key={n.key} className="nav-btn" aria-current={screen === n.key && !app ? 'page' : undefined} onClick={() => go(n.key)}>
            <n.icon /> {n.label}
            {n.key === 'anomalies' && unseen > 0 && <span className="nav-badge" aria-label={`новых: ${unseen}`}>{unseen}</span>}
          </button>
        ))}
        <div className="rail-foot">
          <div style={{ marginBottom: 6 }}>
            <span className={`agent-dot ${agent?.running ? '' : 'off'}`} />
            <strong>{agent?.running ? 'Агент пишет данные' : 'Агент не запущен'}</strong>
          </div>
          {agent?.running ? (
            <>
              <div>Расход: {pct(agent.cpu, 2)} процессора, {bytes(agent.ram)}</div>
              <div>Потеряно событий: {agent.eventsLost ?? '—'}</div>
              <div>Последняя запись {ago(agent.lastWrite)}</div>
            </>
          ) : (
            <div>История не пополняется. Запусти службу SysWatch Agent.</div>
          )}
        </div>
      </nav>

      <main className="main">
        <Scopes live={live} hist={hist} />
        <div className="content">
          {liveError && !live && <div className="error">Нет связи с SysWatch: {liveError}</div>}
          {app ? (
            <AppDetail name={app} range={range} setRange={setRange} onBack={() => setApp(null)} />
          ) : screen === 'now' ? (
            <Now live={live} onApp={setApp} />
          ) : screen === 'timeline' ? (
            <Timeline range={range} setRange={setRange} onApp={setApp} />
          ) : screen === 'network' ? (
            <Network range={range} setRange={setRange} onApp={setApp} />
          ) : screen === 'connections' ? (
            <Connections onApp={setApp} />
          ) : screen === 'system' ? (
            <System onApp={setApp} />
          ) : screen === 'history' ? (
            <History />
          ) : screen === 'dev' ? (
            <Dev />
          ) : screen === 'report' ? (
            <Report onApp={setApp} />
          ) : screen === 'programs' ? (
            <Programs />
          ) : screen === 'disk' ? (
            <Disk />
          ) : screen === 'anomalies' ? (
            <Anomalies onApp={setApp} onSeen={refreshUnseen} />
          ) : (
            <Events range={range} setRange={setRange} onApp={setApp} />
          )}
        </div>
      </main>
    </div>
  );
}
