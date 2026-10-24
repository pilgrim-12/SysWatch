import { useCallback, useEffect, useState } from 'react';
import { api, type Live } from './api';
import { ago, bytes, dayTime, pct } from './format';
import { LANGS, lang, setLang, t, type Lang } from './i18n';
import { Scopes, emptyHistory, pushLive, type LiveHistory } from './components/Scopes';
import { BrandMark, IconAlert, IconApps, IconChip, IconDisk, IconHeat, IconLink, IconList, IconNet, IconClock, IconCode, IconGpu, IconPulse, IconReport, IconGauge, IconGear, IconShield } from './components/Icons';
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
import { Gpu } from './screens/Gpu';
import { Overview } from './screens/Overview';
import { Settings } from './screens/Settings';
import { Security } from './screens/Security';

type Screen = 'overview' | 'now' | 'connections' | 'timeline' | 'network' | 'anomalies' | 'security' | 'disk' | 'programs' | 'system' | 'gpu' | 'report' | 'dev' | 'history' | 'events' | 'settings';

const NAV: { key: Screen; label: string; icon: () => JSX.Element }[] = [
  { key: 'overview', label: t('Обзор'), icon: IconGauge },
  { key: 'now', label: t('Сейчас'), icon: IconPulse },
  { key: 'connections', label: t('Соединения'), icon: IconLink },
  { key: 'timeline', label: t('Таймлайн'), icon: IconHeat },
  { key: 'network', label: t('Сеть'), icon: IconNet },
  { key: 'anomalies', label: t('Аномалии'), icon: IconAlert },
  { key: 'security', label: t('Безопасность'), icon: IconShield },
  { key: 'disk', label: t('Диск'), icon: IconDisk },
  { key: 'programs', label: t('Программы'), icon: IconApps },
  { key: 'system', label: t('Система'), icon: IconChip },
  { key: 'gpu', label: t('Видеокарта'), icon: IconGpu },
  { key: 'dev', label: t('Разработка'), icon: IconCode },
  { key: 'history', label: t('История'), icon: IconClock },
  { key: 'report', label: t('Отчёты'), icon: IconReport },
  { key: 'events', label: t('События'), icon: IconList },
  { key: 'settings', label: t('Настройки'), icon: IconGear },
];

function readPref<T>(key: string, fallback: T): T {
  try { const v = localStorage.getItem(key); return v ? (JSON.parse(v) as T) : fallback; } catch { return fallback; }
}
function writePref(key: string, v: unknown) {
  try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* недоступно — не страшно */ }
}

/** Смена языка: запоминаем (окно и трей) и перезагружаем окно — все тексты и форматы берутся заново. */
function switchLang(l: Lang) {
  setLang(l);
  api.setLanguage(l).catch(() => {}).finally(() => location.reload());
}

export function App() {
  const [screen, setScreen] = useState<Screen>(() => {
    // Первый запуск новой версии открывает «Обзор», дальше — последний раздел.
    let first = false;
    try { first = !localStorage.getItem('overview_seen'); localStorage.setItem('overview_seen', '1'); } catch { /* нет хранилища */ }
    return first ? 'overview' : readPref('screen', 'overview');
  });
  const [range, setRange] = useState<number>(() => readPref('range', 60));
  const [app, setApp] = useState<string | null>(null);
  const [live, setLive] = useState<Live | null>(null);
  const [hist, setHist] = useState<LiveHistory>(emptyHistory);
  const [liveError, setLiveError] = useState<string | null>(null);

  const [unseen, setUnseen] = useState(0);

  useEffect(() => writePref('screen', screen), [screen]);

  // Меню слева можно свернуть до значков — как боковые панели в VS Code и Figma.
  const [mini, setMini] = useState<boolean>(() => readPref('rail_mini', false));
  useEffect(() => writePref('rail_mini', mini), [mini]);

  const [compact, setCompact] = useState<boolean>(() => readPref('scopes_compact', false));
  useEffect(() => writePref('scopes_compact', compact), [compact]);

  useCollapsibleBlocks();

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
    <div className={`shell${mini ? ' mini' : ''}`}>
      <nav className="rail" aria-label={t('Разделы')}>
        <div className="brand">
          <BrandMark /> <span className="nav-label">SysWatch</span>
          <button className="rail-toggle" onClick={() => setMini(m => !m)}
            title={mini ? t('Развернуть меню') : t('Свернуть меню')} aria-label={mini ? t('Развернуть меню') : t('Свернуть меню')} aria-expanded={!mini}>
            {mini ? '»' : '«'}
          </button>
        </div>
        {NAV.map(n => (
          <button key={n.key} className="nav-btn" title={n.label} aria-current={screen === n.key && !app ? 'page' : undefined} onClick={() => go(n.key)}>
            <n.icon /> <span className="nav-label">{n.label}</span>
            {n.key === 'anomalies' && unseen > 0 && <span className="nav-badge" aria-label={t('новых: {0}', unseen)}>{unseen}</span>}
          </button>
        ))}
        <label className="lang-pick">
          <span>{t('Язык')}</span>
          <select className="select" value={lang()} onChange={e => switchLang(e.target.value as Lang)}>
            {LANGS.map(l => <option key={l.code} value={l.code}>{l.label}</option>)}
          </select>
        </label>
        <div className="rail-foot">
          <div className="rail-version" title={t('Версия SysWatch и время сборки интерфейса')}>
            {t('Версия {0} · сборка {1}', __APP_VERSION__, dayTime(__BUILD_TIME__))}
          </div>
          <div style={{ marginBottom: 6 }}>
            <span className={`agent-dot ${agent?.running ? '' : 'off'}`} />
            <strong>{agent?.running ? t('Агент пишет данные') : t('Агент не запущен')}</strong>
          </div>
          {agent?.running ? (
            <>
              <div>{t('Расход: {0} процессора, {1}', pct(agent.cpu, 2), bytes(agent.ram))}</div>
              <div>{t('Потеряно событий: {0}', agent.eventsLost ?? '—')}</div>
              <div>{t('Последняя запись {0}', ago(agent.lastWrite))}</div>
            </>
          ) : (
            <div>{t('История не пополняется. Запусти службу SysWatch Agent.')}</div>
          )}
        </div>
      </nav>

      <main className="main">
        <div className={`scopes-wrap${compact ? ' compact' : ''}`}>
          <Scopes live={live} hist={hist} />
          <button className="scopes-toggle" onClick={() => setCompact(c => !c)}
            title={compact ? t('Показать графики') : t('Скрыть графики — только цифры')} aria-label={compact ? t('Показать графики') : t('Скрыть графики — только цифры')}>
            {compact ? '▾' : '▴'}
          </button>
        </div>
        <div className="content" data-screen={app ? 'app' : screen}>
          {liveError && !live && <div className="error">{t('Нет связи с SysWatch: {0}', liveError)}</div>}
          {app ? (
            <AppDetail name={app} range={range} setRange={setRange} onBack={() => setApp(null)} />
          ) : screen === 'settings' ? (
            <Settings />
          ) : screen === 'security' ? (
            <Security />
          ) : screen === 'overview' ? (
            <Overview />
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
          ) : screen === 'gpu' ? (
            <Gpu range={range} setRange={setRange} onApp={setApp} />
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

/**
 * Любой блок раздела (section.block с заголовком h2) сворачивается кликом по заголовку или Enter/Пробелом.
 * Состояние запоминается для каждого раздела отдельно. Кнопки и переключатели внутри заголовка работают как обычно.
 */
function useCollapsibleBlocks() {
  useEffect(() => {
    const KEY = 'collapsed_blocks';
    const load = (): Set<string> => new Set(readPref<string[]>(KEY, []));
    let closed = load();
    const keyOf = (h: HTMLElement) => {
      const screen = (h.closest('.content') as HTMLElement | null)?.dataset.screen ?? '';
      const title = (h.childNodes[0]?.textContent ?? '').trim() || (h.textContent ?? '').trim().slice(0, 40);
      return `${screen}|${title}`;
    };
    const apply = () => {
      document.querySelectorAll<HTMLElement>('.content .block > h2').forEach(h => {
        const sec = h.parentElement!;
        const shut = closed.has(keyOf(h));
        sec.classList.toggle('collapsed', shut);
        if (h.getAttribute('aria-expanded') !== String(!shut)) h.setAttribute('aria-expanded', String(!shut));
        if (!h.hasAttribute('tabindex')) { h.tabIndex = 0; h.setAttribute('role', 'button'); h.classList.add('collapsible'); }
      });
    };
    const toggle = (h: HTMLElement) => {
      const k = keyOf(h);
      if (closed.has(k)) closed.delete(k); else closed.add(k);
      writePref(KEY, [...closed]);
      apply();
    };
    const fromEvent = (e: Event) => {
      const el = e.target as HTMLElement;
      if (el.closest('button, a, input, select, label, .seg, .btn')) return null;
      return el.closest<HTMLElement>('.content .block > h2');
    };
    const onClick = (e: MouseEvent) => { const h = fromEvent(e); if (h) toggle(h); };
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      const h = e.target as HTMLElement;
      if (h.matches?.('.content .block > h2')) { e.preventDefault(); toggle(h); }
    };
    let raf = 0;
    const mo = new MutationObserver(() => { cancelAnimationFrame(raf); raf = requestAnimationFrame(apply); });
    mo.observe(document.body, { childList: true, subtree: true });
    document.addEventListener('click', onClick);
    document.addEventListener('keydown', onKey);
    apply();
    return () => { mo.disconnect(); document.removeEventListener('click', onClick); document.removeEventListener('keydown', onKey); cancelAnimationFrame(raf); };
  }, []);
}
