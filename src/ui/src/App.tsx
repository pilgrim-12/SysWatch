import { useEffect, useState } from 'react';
import { api, type Live } from './api';
import { ago, bytes, pct } from './format';
import { Scopes, emptyHistory, pushLive, type LiveHistory } from './components/Scopes';
import { BrandMark, IconHeat, IconList, IconNet, IconPulse } from './components/Icons';
import { Now } from './screens/Now';
import { Timeline } from './screens/Timeline';
import { Network } from './screens/Network';
import { Events } from './screens/Events';
import { AppDetail } from './screens/AppDetail';

type Screen = 'now' | 'timeline' | 'network' | 'events';

const NAV: { key: Screen; label: string; icon: () => JSX.Element }[] = [
  { key: 'now', label: 'Сейчас', icon: IconPulse },
  { key: 'timeline', label: 'Таймлайн', icon: IconHeat },
  { key: 'network', label: 'Сеть', icon: IconNet },
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

  useEffect(() => writePref('screen', screen), [screen]);
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

  const go = (s: Screen) => { setScreen(s); setApp(null); };
  const agent = live?.agent;

  return (
    <div className="shell">
      <nav className="rail" aria-label="Разделы">
        <div className="brand"><BrandMark /> SysWatch</div>
        {NAV.map(n => (
          <button key={n.key} className="nav-btn" aria-current={screen === n.key && !app ? 'page' : undefined} onClick={() => go(n.key)}>
            <n.icon /> {n.label}
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
          ) : (
            <Events range={range} setRange={setRange} onApp={setApp} />
          )}
        </div>
      </main>
    </div>
  );
}
