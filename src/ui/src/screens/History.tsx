import { useEffect, useMemo, useState } from 'react';
import { api, type HistoryItem } from '../api';
import { useLoad } from '../hooks';
import { pluralWord } from '../format';

type Kind = 'search' | 'visit' | 'domains' | 'profiles';

const KINDS: { key: Kind; label: string }[] = [
  { key: 'search', label: 'Поисковые запросы' },
  { key: 'visit', label: 'Посещения' },
  { key: 'domains', label: 'Сайты' },
  { key: 'profiles', label: 'Браузеры и профили' },
];

const PERIODS = [
  { days: 1, label: 'Сутки' },
  { days: 7, label: '7 дней' },
  { days: 30, label: '30 дней' },
  { days: 90, label: '90 дней' },
];

const dayFmt = new Intl.DateTimeFormat('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' });
const timeFmt = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });
const shortFmt = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

function dayKey(ts: number) {
  const d = new Date(ts * 1000);
  return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
}

function dayTitle(key: number) {
  const today = dayKey(Date.now() / 1000);
  if (key === today) return 'Сегодня';
  if (key === today - 86400000) return 'Вчера';
  return dayFmt.format(key);
}

/** Задержка ввода в поиске, чтобы не перечитывать историю на каждую букву. */
function useDebounced<T>(value: T, ms: number) {
  const [v, setV] = useState(value);
  useEffect(() => { const id = setTimeout(() => setV(value), ms); return () => clearTimeout(id); }, [value, ms]);
  return v;
}

export function History() {
  const [kind, setKind] = useState<Kind>('search');
  const [days, setDays] = useState(7);
  const [source, setSource] = useState('');
  const [q, setQ] = useState('');
  const query = useDebounced(q, 300);
  const data = useLoad(() => api.history(days, query, source, kind === 'domains' || kind === 'profiles' ? '' : kind), [days, query, source, kind]);
  const r = data.data;
  const [selected, setSelected] = useState<Map<string, { source: string; url: string }>>(new Map());
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  useEffect(() => setSelected(new Map()), [days, query, source, kind]);

  const selKey = (i: HistoryItem) => `${i.source}\u0001${i.url}`;
  const toggle = (i: HistoryItem) => setSelected(m => {
    const n = new Map(m);
    if (n.has(selKey(i))) n.delete(selKey(i)); else n.set(selKey(i), { source: i.source, url: i.url });
    return n;
  });
  const toggleDay = (items: HistoryItem[], on: boolean) => setSelected(m => {
    const n = new Map(m);
    for (const i of items) { if (on) n.set(selKey(i), { source: i.source, url: i.url }); else n.delete(selKey(i)); }
    return n;
  });

  const clearProfile = (id: string, all: boolean) => {
    const period = PERIODS.find(p => p.days === days)!.label.toLowerCase();
    remove({ clearProfiles: [id], clearFrom: all ? undefined : Math.floor(Date.now() / 1000) - days * 86400 },
      all ? `ВСЮ историю профиля «${id}» за всё время` : `историю профиля «${id}» за последние ${period}`);
  };

  /** Удаление: если браузер открыт — спрашиваем, можно ли его закрыть, и повторяем. */
  const remove = async (req: { items?: { source: string; url: string }[]; domain?: string; sources?: string[]; clearProfiles?: string[]; clearFrom?: number }, what: string) => {
    if (!confirm(`Удалить из истории ${what}? Перед удалением SysWatch сохранит копию истории рядом с ней.`)) return;
    setBusy(true); setMsg(null);
    try {
      let res = await api.historyDelete({ ...req, closeBrowsers: false });
      if (!res.ok && res.running.length > 0) {
        if (!confirm(`${res.running.join(', ')} сейчас ${res.running.length > 1 ? 'открыты' : 'открыт'}: пока браузер работает, он держит историю и вернёт удалённое. Закрыть ${res.running.length > 1 ? 'их' : 'его'}? Вкладки восстановятся при следующем запуске.`)) {
          setMsg({ ok: false, text: res.message });
          return;
        }
        res = await api.historyDelete({ ...req, closeBrowsers: true });
      }
      setMsg({ ok: res.ok, text: res.message });
      if (res.ok) { setSelected(new Map()); data.reload(); }
    } catch (e) {
      setMsg({ ok: false, text: String((e as Error)?.message ?? e) });
    } finally {
      setBusy(false);
    }
  };

  const groups = useMemo(() => {
    const map = new Map<number, HistoryItem[]>();
    for (const i of r?.items ?? []) {
      const k = dayKey(i.ts);
      if (!map.has(k)) map.set(k, []);
      map.get(k)!.push(i);
    }
    return [...map.entries()];
  }, [r]);

  const maxVisits = r?.domains[0]?.visits ?? 1;
  const failed = r?.profiles.filter(p => !p.ok) ?? [];

  return (
    <>
      <div className="head">
        <div>
          <h1>История браузеров</h1>
          <p>Что вы искали и какие сайты открывали — во всех браузерах и профилях этой учётной записи Windows. SysWatch читает копию истории и ничего не сохраняет и не отправляет.</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label="Период">
            {PERIODS.map(p => <button key={p.days} aria-pressed={days === p.days} onClick={() => setDays(p.days)}>{p.label}</button>)}
          </div>
        </div>
      </div>

      {data.error && <div className="error">Не удалось прочитать историю: {data.error}</div>}

      {r && (
        <div className="facts">
          <div className="fact"><b style={{ color: 'var(--cpu)' }}>{r.searches}</b><span>{pluralWord(r.searches, 'поисковый запрос', 'поисковых запроса', 'поисковых запросов')}</span></div>
          <div className="fact"><b style={{ color: 'var(--net)' }}>{r.visits}</b><span>{pluralWord(r.visits, 'посещение', 'посещения', 'посещений')} страниц</span></div>
          <div className="fact"><b>{r.domains.length >= 100 ? '100+' : r.domains.length}</b><span>разных сайтов</span></div>
          <div className="fact"><b>{r.profiles.filter(p => p.ok).length}</b><span>браузеров и профилей прочитано</span></div>
        </div>
      )}

      {failed.map(p => <div key={p.id} className="note">{p.id}: {p.error}</div>)}
      {msg && <div className={msg.ok ? 'note' : 'error'}>{msg.text}</div>}

      <div className="filters" style={{ margin: '18px 0 14px' }}>
        <div className="seg" role="tablist">
          {KINDS.map(k => <button key={k.key} role="tab" aria-pressed={kind === k.key} onClick={() => setKind(k.key)}>{k.label}</button>)}
        </div>
        <input className="search" placeholder="Слово, сайт или адрес" value={q} onChange={e => setQ(e.target.value)} />
        {r && r.profiles.length > 1 && (
          <select className="select" value={source} onChange={e => setSource(e.target.value)} aria-label="Браузер">
            <option value="">Все браузеры</option>
            {r.profiles.filter(p => p.ok).map(p => <option key={p.id} value={p.id}>{p.id}</option>)}
          </select>
        )}
        {data.loading && <span className="dim" style={{ fontSize: 13 }}>Читаю историю…</span>}
        {kind !== 'domains' && selected.size > 0 && (
          <div className="sel-bar">
            <span>Выбрано: {selected.size}</span>
            <button className="btn danger" disabled={busy} onClick={() => remove({ items: [...selected.values()] }, `выбранные записи (${selected.size})`)}>{busy ? 'Удаляю…' : 'Удалить из истории'}</button>
            <button className="link dim" onClick={() => setSelected(new Map())}>Снять выбор</button>
          </div>
        )}
      </div>

      {!r ? <div className="empty">{data.error ? '' : 'Читаю историю браузеров…'}</div> : kind === 'profiles' ? (
        <div className="panel">
          <table className="table dev-table">
            <thead><tr><th style={{ textAlign: 'left' }}>Браузер и профиль</th><th>Посещений</th><th>Запросов</th><th /></tr></thead>
            <tbody>
              {r.profiles.map(p => (
                <tr key={p.id}>
                  <td className="dev-name"><b>{p.browser}</b>{p.profile && <code>{p.profile}</code>}{!p.ok && <div className="act-bad">{p.error}</div>}</td>
                  <td>{p.visits}</td>
                  <td>{p.searches}</td>
                  <td className="dev-act">
                    <button className="link" disabled={busy || !p.ok} onClick={() => clearProfile(p.id, false)}>Очистить за {PERIODS.find(x => x.days === days)!.label.toLowerCase()}</button>
                    <button className="link danger-link" disabled={busy || !p.ok} onClick={() => clearProfile(p.id, true)}>Очистить всё</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : kind === 'domains' ? (
        <div className="panel">
          <table className="table dev-table">
            <thead><tr><th style={{ textAlign: 'left' }}>Сайт</th><th style={{ textAlign: 'left', width: '40%' }}>Посещений</th><th>Последний раз</th><th /></tr></thead>
            <tbody>
              {r.domains.length === 0 && <tr><td colSpan={4} className="dim">Ничего не найдено</td></tr>}
              {r.domains.map(d => (
                <tr key={d.domain} onClick={() => { setQ(d.domain); setKind('visit'); }}>
                  <td className="dev-name"><b>{d.domain}</b>{r.profiles.length > 1 && <div className="dim">{d.sources.join(', ')}</div>}</td>
                  <td style={{ textAlign: 'left' }}>
                    <div className="hist-bar"><i style={{ width: `${(d.visits / maxVisits) * 100}%` }} /><span>{d.visits}</span></div>
                  </td>
                  <td className="dim">{shortFmt.format(d.last * 1000)}</td>
                  <td className="dev-act">
                    <button className="link" disabled={busy} onClick={e => {
                      e.stopPropagation();
                      remove({ domain: d.domain, sources: source ? [source] : undefined }, `все страницы ${d.domain}${source ? ` в ${source}` : ' во всех браузерах'} — за всё время, а не только за выбранный период`);
                    }}>Удалить сайт</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : groups.length === 0 ? (
        <div className="panel"><div className="empty">{q ? 'Ничего не найдено.' : kind === 'search' ? 'Поисковых запросов за период нет.' : 'Посещений за период нет.'}</div></div>
      ) : groups.map(([key, items]) => (
        <section key={key} className="block">
          <h2>
            <label className="check day-check">
              <input type="checkbox" checked={items.every(i => selected.has(selKey(i)))} onChange={e => toggleDay(items, e.target.checked)} />
              {dayTitle(key)}
            </label>
            <small>{items.length}</small>
          </h2>
          <div className="panel">
            {items.map((i, n) => (
              <div key={n} className={`hist-line ${selected.has(selKey(i)) ? 'sel' : ''}`}>
              <input type="checkbox" className="hist-check" checked={selected.has(selKey(i))} onChange={() => toggle(i)} aria-label="Выбрать" />
              <a className="hist-row" href={i.url} target="_blank" rel="noreferrer" title={i.url}>
                <span className="hist-time">{timeFmt.format(i.ts * 1000)}</span>
                {i.kind === 'search' ? (
                  <span className="hist-main"><b>{i.term}</b><span className="tag">{i.engine}</span></span>
                ) : (
                  <span className="hist-main"><span className="hist-title">{i.title ?? i.url}</span><span className="dim">{i.domain}</span></span>
                )}
                {r.profiles.length > 1 && <span className="hist-src">{i.source}</span>}
              </a>
              </div>
            ))}
          </div>
        </section>
      ))}

      {r && r.items.length >= 1500 && <p className="hint-line">Показаны последние 1500 записей. Уточните поиск или период.</p>}
      <p className="hint-line">Режим инкогнито браузеры не записывают — его здесь нет. Ссылки открываются в браузере по умолчанию.
        Удаление действует на этот компьютер: если в браузере включена синхронизация, история в аккаунте Google, Microsoft или Firefox остаётся — её чистят в настройках аккаунта.</p>
    </>
  );
}
