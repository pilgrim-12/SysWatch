import { useEffect, useMemo, useState } from 'react';
import { api, type HistoryItem } from '../api';
import { useLoad } from '../hooks';
import { pluralWord } from '../format';
import { locale, t } from '../i18n';

type Kind = 'search' | 'visit' | 'domains' | 'profiles';

const KINDS: { key: Kind; label: string }[] = [
  { key: 'search', label: t('Поисковые запросы') },
  { key: 'visit', label: t('Посещения') },
  { key: 'domains', label: t('Сайты') },
  { key: 'profiles', label: t('Браузеры и профили') },
];

const PERIODS = [
  { days: 1, label: t('Сутки') },
  { days: 7, label: t('7 дней') },
  { days: 30, label: t('30 дней') },
  { days: 90, label: t('90 дней') },
];

const dayFmt = new Intl.DateTimeFormat(locale(), { weekday: 'long', day: 'numeric', month: 'long' });
const timeFmt = new Intl.DateTimeFormat(locale(), { hour: '2-digit', minute: '2-digit' });
const shortFmt = new Intl.DateTimeFormat(locale(), { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

function dayKey(ts: number) {
  const d = new Date(ts * 1000);
  return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
}

function dayTitle(key: number) {
  const today = dayKey(Date.now() / 1000);
  if (key === today) return t('Сегодня');
  if (key === today - 86400000) return t('Вчера');
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
      all ? t('ВСЮ историю профиля «{0}» за всё время', id) : t('историю профиля «{0}» за последние {1}', id, period));
  };

  /** Удаление: если браузер открыт — спрашиваем, можно ли его закрыть, и повторяем. */
  // Открытый браузер не закрываем: удаление ставится в очередь и выполняется, когда вы сами его закроете.
  const [pending, setPending] = useState<string[]>([]);
  useEffect(() => { api.historyPending().then(setPending).catch(() => {}); }, [r]);

  const remove = async (req: { items?: { source: string; url: string }[]; domain?: string; sources?: string[]; clearProfiles?: string[]; clearFrom?: number }, what: string) => {
    if (!confirm(t('Удалить из истории {0}? Перед удалением SysWatch сохранит копию истории рядом с ней.', what))) return;
    setBusy(true); setMsg(null);
    try {
      const res = await api.historyDelete({ ...req, closeBrowsers: false });
      // Если всё ушло в очередь — хватит плашки «Ждёт закрытия браузера», второе сообщение не нужно.
      setMsg(res.pending.length > 0 && res.deleted === 0 && res.ok ? null : { ok: res.ok, text: res.message });
      setSelected(new Map());
      data.reload();
    } catch (e) {
      setMsg({ ok: false, text: String((e as Error)?.message ?? e) });
    } finally {
      setBusy(false);
    }
  };

  const applyNow = async (browser: string) => {
    if (!confirm(t('Закрыть {0} сейчас и сразу удалить? Вкладки браузер восстановит при следующем запуске (если это включено в его настройках).', browser))) return;
    setBusy(true);
    try { const r2 = await api.historyApplyNow(browser); setMsg({ ok: r2.ok, text: r2.message }); data.reload(); }
    finally { setBusy(false); }
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
          <h1>{t('История браузеров')}</h1>
          <p>{t('Что вы искали и какие сайты открывали — во всех браузерах и профилях этой учётной записи Windows. SysWatch читает копию истории и ничего не сохраняет и не отправляет.')}</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label={t('Период')}>
            {PERIODS.map(p => <button key={p.days} aria-pressed={days === p.days} onClick={() => setDays(p.days)}>{p.label}</button>)}
          </div>
        </div>
      </div>

      {data.error && <div className="error">{t('Не удалось прочитать историю: {0}', data.error)}</div>}

      {r && (
        <div className="facts">
          <div className="fact"><b style={{ color: 'var(--cpu)' }}>{r.searches}</b><span>{pluralWord(r.searches, 'поисковый запрос', 'поисковых запроса', 'поисковых запросов')}</span></div>
          <div className="fact"><b style={{ color: 'var(--net)' }}>{r.visits}</b><span>{pluralWord(r.visits, 'посещение страницы', 'посещения страниц', 'посещений страниц')}</span></div>
          <div className="fact"><b>{r.domains.length >= 100 ? '100+' : r.domains.length}</b><span>{t('разных сайтов')}</span></div>
          <div className="fact"><b>{r.profiles.filter(p => p.ok).length}</b><span>{t('браузеров и профилей прочитано')}</span></div>
        </div>
      )}

      {failed.map(p => <div key={p.id} className="note">{p.id}: {p.error}</div>)}
      {msg && <div className={msg.ok ? 'note' : 'error'}>{msg.text}</div>}
      {pending.length > 0 && (
        <div className="note pending-note">
          <span>{t('Ждёт закрытия браузера:')} <b>{pending.join(', ')}</b>. {pending.length > 1 ? t('Удаление выполнится само, как только вы закроете их — SysWatch покажет уведомление.') : t('Удаление выполнится само, как только вы закроете его — SysWatch покажет уведомление.')}</span>
          {pending.map(b => <button key={b} className="link" disabled={busy} onClick={() => applyNow(b)}>{t('Закрыть {0} и удалить сейчас', b)}</button>)}
        </div>
      )}

      <div className="filters" style={{ margin: '18px 0 14px' }}>
        <div className="seg" role="tablist">
          {KINDS.map(k => <button key={k.key} role="tab" aria-pressed={kind === k.key} onClick={() => setKind(k.key)}>{k.label}</button>)}
        </div>
        <input className="search" placeholder={t('Слово, сайт или адрес')} value={q} onChange={e => setQ(e.target.value)} />
        {r && r.profiles.length > 1 && (
          <select className="select" value={source} onChange={e => setSource(e.target.value)} aria-label={t('Браузер')}>
            <option value="">{t('Все браузеры')}</option>
            {r.profiles.filter(p => p.ok).map(p => <option key={p.id} value={p.id}>{p.id}</option>)}
          </select>
        )}
        {data.loading && <span className="dim" style={{ fontSize: 13 }}>{t('Читаю историю…')}</span>}
      </div>

      {!r ? <div className="empty">{data.error ? '' : t('Читаю историю браузеров…')}</div> : kind === 'profiles' ? (
        <div className="panel">
          <table className="table dev-table">
            <thead><tr><th style={{ textAlign: 'left' }}>{t('Браузер и профиль')}</th><th>{t('Посещений')}</th><th>{t('Запросов')}</th><th /></tr></thead>
            <tbody>
              {r.profiles.map(p => (
                <tr key={p.id}>
                  <td className="dev-name"><b>{p.browser}</b>{p.profile && <code>{p.profile}</code>}{!p.ok && <div className="act-bad">{p.error}</div>}</td>
                  <td>{p.visits}</td>
                  <td>{p.searches}</td>
                  <td className="dev-act">
                    <button className="link" disabled={busy || !p.ok} onClick={() => clearProfile(p.id, false)}>{t('Очистить за {0}', PERIODS.find(x => x.days === days)!.label.toLowerCase())}</button>
                    <button className="link danger-link" disabled={busy || !p.ok} onClick={() => clearProfile(p.id, true)}>{t('Очистить всё')}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : kind === 'domains' ? (
        <div className="panel">
          <table className="table dev-table">
            <thead><tr><th style={{ textAlign: 'left' }}>{t('Сайт')}</th><th style={{ textAlign: 'left', width: '40%' }}>{t('Посещений')}</th><th>{t('Последний раз')}</th><th /></tr></thead>
            <tbody>
              {r.domains.length === 0 && <tr><td colSpan={4} className="dim">{t('Ничего не найдено')}</td></tr>}
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
                      remove({ domain: d.domain, sources: source ? [source] : undefined }, source ? t('все страницы {0} в {1} — за всё время, а не только за выбранный период', d.domain, source) : t('все страницы {0} во всех браузерах — за всё время, а не только за выбранный период', d.domain));
                    }}>{t('Удалить сайт')}</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : groups.length === 0 ? (
        <div className="panel"><div className="empty">{q ? t('Ничего не найдено.') : kind === 'search' ? t('Поисковых запросов за период нет.') : t('Посещений за период нет.')}</div></div>
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
              <input type="checkbox" className="hist-check" checked={selected.has(selKey(i))} onChange={() => toggle(i)} aria-label={t('Выбрать')} />
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

      {r && r.items.length >= 1500 && <p className="hint-line">{t('Показаны последние 1500 записей. Уточните поиск или период.')}</p>}
      <p className="hint-line">{t('Режим инкогнито браузеры не записывают — его здесь нет. Ссылки открываются в браузере по умолчанию.')}{' '}
        {t('Удаление действует на этот компьютер: если в браузере включена синхронизация, история в аккаунте Google, Microsoft или Firefox остаётся — её чистят в настройках аккаунта.')}</p>
      {kind !== 'domains' && selected.size > 0 && (
          <div className="sel-bar floating">
            <span>{t('Выбрано: {0}', selected.size)}</span>
            <button className="btn danger" disabled={busy} onClick={() => remove({ items: [...selected.values()] }, t('выбранные записи ({0})', selected.size))}>{busy ? t('Удаляю…') : t('Удалить из истории')}</button>
            <button className="link dim" onClick={() => setSelected(new Map())}>{t('Снять выбор')}</button>
          </div>
        )}
    </>
  );
}
