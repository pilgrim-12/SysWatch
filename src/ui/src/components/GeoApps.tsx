import { useMemo, useState } from 'react';
import type { GeoFlow } from '../api';
import { appLabel, bytes, countryName } from '../format';
import { t } from '../i18n';

type App = { name: string; tx: number; rx: number; flows: GeoFlow[]; countries: string[] };

/** «Какая программа, куда и сколько»: программа → её адреса со страной; сортировка по отправленному. */
export function GeoApps({ flows, onApp }: { flows: GeoFlow[]; onApp: (n: string) => void }) {
  const [q, setQ] = useState('');
  const [open, setOpen] = useState<Set<string>>(new Set());

  const apps = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const map = new Map<string, App>();
    for (const f of flows) {
      if (needle && !(f.app.toLowerCase().includes(needle) || f.dest.toLowerCase().includes(needle)
        || f.ip.includes(needle) || countryName(f.country).toLowerCase().includes(needle))) continue;
      const a = map.get(f.app) ?? { name: f.app, tx: 0, rx: 0, flows: [], countries: [] };
      a.tx += f.tx; a.rx += f.rx; a.flows.push(f);
      if (f.country && !a.countries.includes(f.country)) a.countries.push(f.country);
      map.set(f.app, a);
    }
    const list = [...map.values()];
    for (const a of list) a.flows.sort((x, y) => y.tx - x.tx || y.rx - x.rx);
    return list.sort((x, y) => y.tx - x.tx);
  }, [flows, q]);

  const toggle = (n: string) => setOpen(s => { const c = new Set(s); if (c.has(n)) c.delete(n); else c.add(n); return c; });
  const isOpen = (a: App, i: number) => open.has(a.name) !== (i < 3 && !q); // первые три раскрыты сразу

  return (
    <div className="panel geo-apps-panel">
      <div className="report-toolbar" style={{ padding: '10px 12px 0', margin: 0 }}>
        <input className="search" placeholder={t('Программа, адрес, IP или страна')} value={q} onChange={e => setQ(e.target.value)} />
        <span className="dim" style={{ color: 'var(--muted)', fontSize: 13 }}>{t('Сортировка — по отправленному: сверху те, кто больше всего отдаёт наружу.')}</span>
      </div>
      <div className="scroll" style={{ maxHeight: 620 }}>
        {apps.length === 0 && <div className="empty">{t('Ничего не найдено.')}</div>}
        {apps.map((a, i) => (
          <div key={a.name} className="ga-app">
            <button className="ga-head" onClick={() => toggle(a.name)} aria-expanded={isOpen(a, i)}>
              <span className="ga-caret">{isOpen(a, i) ? '▾' : '▸'}</span>
              <b>{appLabel(a.name)}</b>
              <span className="ga-sum">
                <span>{t('отправлено')} <b>{bytes(a.tx)}</b></span>
                <span>{t('скачано')} <b style={{ color: 'var(--net)' }}>{bytes(a.rx)}</b></span>
              </span>
              <span className="ga-countries">{a.countries.slice(0, 5).map(countryName).join(', ')}{a.countries.length > 5 ? ' …' : ''}</span>
            </button>
            {isOpen(a, i) && (
              <table className="table ga-table">
                <thead>
                  <tr><th>{t('Куда')}</th><th>{t('Страна')}</th><th>{t('Отправлено')}</th><th>{t('Скачано')}</th><th>{t('Подключений')}</th></tr>
                </thead>
                <tbody>
                  {a.flows.slice(0, 40).map(f => (
                    <tr key={f.dest} style={{ cursor: 'default' }}>
                      <td><span className="ga-dest">{f.dest}</span>{f.dest !== f.ip && <span className="dim ga-ip"> {f.ip}</span>}</td>
                      <td>{f.country ? countryName(f.country) : <span className="dim">—</span>}</td>
                      <td>{bytes(f.tx)}</td>
                      <td style={{ color: 'var(--net)' }}>{bytes(f.rx)}</td>
                      <td className="dim">{f.connects}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {isOpen(a, i) && (
              <div className="ga-foot">
                {a.flows.length > 40 && <span className="dim">{t('и ещё {0} адресов', a.flows.length - 40)} · </span>}
                <button className="link" onClick={() => onApp(a.name)}>{t('Открыть карточку программы')}</button>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
