import { useEffect, useMemo, useState } from 'react';
import { api, type HistoryItem } from '../api';
import { useLoad } from '../hooks';
import { pluralWord } from '../format';

type Kind = 'search' | 'visit' | 'domains';

const KINDS: { key: Kind; label: string }[] = [
  { key: 'search', label: 'Поисковые запросы' },
  { key: 'visit', label: 'Посещения' },
  { key: 'domains', label: 'Сайты' },
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
  const data = useLoad(() => api.history(days, query, source, kind === 'domains' ? '' : kind), [days, query, source, kind]);
  const r = data.data;

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
      </div>

      {!r ? <div className="empty">{data.error ? '' : 'Читаю историю браузеров…'}</div> : kind === 'domains' ? (
        <div className="panel">
          <table className="table dev-table">
            <thead><tr><th style={{ textAlign: 'left' }}>Сайт</th><th style={{ textAlign: 'left', width: '40%' }}>Посещений</th><th>Последний раз</th></tr></thead>
            <tbody>
              {r.domains.length === 0 && <tr><td colSpan={3} className="dim">Ничего не найдено</td></tr>}
              {r.domains.map(d => (
                <tr key={d.domain} onClick={() => { setQ(d.domain); setKind('visit'); }}>
                  <td className="dev-name"><b>{d.domain}</b>{r.profiles.length > 1 && <div className="dim">{d.sources.join(', ')}</div>}</td>
                  <td style={{ textAlign: 'left' }}>
                    <div className="hist-bar"><i style={{ width: `${(d.visits / maxVisits) * 100}%` }} /><span>{d.visits}</span></div>
                  </td>
                  <td className="dim">{shortFmt.format(d.last * 1000)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : groups.length === 0 ? (
        <div className="panel"><div className="empty">{q ? 'Ничего не найдено.' : kind === 'search' ? 'Поисковых запросов за период нет.' : 'Посещений за период нет.'}</div></div>
      ) : groups.map(([key, items]) => (
        <section key={key} className="block">
          <h2>{dayTitle(key)} <small>{items.length}</small></h2>
          <div className="panel">
            {items.map((i, n) => (
              <a key={n} className="hist-row" href={i.url} target="_blank" rel="noreferrer" title={i.url}>
                <span className="hist-time">{timeFmt.format(i.ts * 1000)}</span>
                {i.kind === 'search' ? (
                  <span className="hist-main"><b>{i.term}</b><span className="tag">{i.engine}</span></span>
                ) : (
                  <span className="hist-main"><span className="hist-title">{i.title ?? i.url}</span><span className="dim">{i.domain}</span></span>
                )}
                {r.profiles.length > 1 && <span className="hist-src">{i.source}</span>}
              </a>
            ))}
          </div>
        </section>
      ))}

      {r && r.items.length >= 1500 && <p className="hint-line">Показаны последние 1500 записей. Уточните поиск или период.</p>}
      <p className="hint-line">Режим инкогнито браузеры не записывают — его здесь нет. Ссылки открываются в браузере по умолчанию.</p>
    </>
  );
}
