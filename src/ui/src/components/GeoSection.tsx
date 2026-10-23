import { useState } from 'react';
import { api, type GeoCountry } from '../api';
import { useLoad } from '../hooks';
import { appLabel, bytes, countryName } from '../format';
import { t } from '../i18n';
import { WorldMap } from './WorldMap';
import { GeoApps } from './GeoApps';

type View = 'map' | 'list' | 'apps';

function readView(): View {
  try { const v = localStorage.getItem('geo_view'); return v === 'list' || v === 'apps' ? v : 'map'; } catch { return 'map'; }
}

/** Программы страны одной строкой: «Telegram 2,1 ГБ, chrome 800 МБ». */
function AppsLine({ c, onApp, max = 3 }: { c: GeoCountry; onApp: (n: string) => void; max?: number }) {
  return (
    <span className="geo-apps">
      {c.apps.slice(0, max).map((a, i) => (
        <span key={a.name}>
          {i > 0 && ', '}
          <button className="link" onClick={e => { e.stopPropagation(); onApp(a.name); }}>{appLabel(a.name)}</button>
          <span className="dim"> {bytes(a.bytes)}</span>
        </span>
      ))}
      {c.apps.length > max && <span className="dim"> {t('и ещё {0}', c.apps.length - max)}</span>}
    </span>
  );
}

/** «Куда уходят данные»: карта или таблица стран с программами. */
export function GeoSection({ range, onApp }: { range: number; onApp: (n: string) => void }) {
  const geo = useLoad(() => api.geo(range), [range], 120_000);
  const [selected, setSelected] = useState<string | null>(null);
  const [view, setView] = useState<View>(readView);
  const [busy, setBusy] = useState(false);
  const g = geo.data;

  const switchView = (v: View) => {
    setView(v);
    try { localStorage.setItem('geo_view', v); } catch { /* не страшно */ }
  };
  const download = async () => {
    setBusy(true);
    try { await api.geoDownload(); geo.reload(); } finally { setBusy(false); }
  };

  const total = g ? g.countries.reduce((s, c) => s + c.tx + c.rx, 0) : 0;
  const sel = g?.countries.find(c => c.code === selected) ?? null;

  return (
    <section className="block">
      <h2>{t('Куда уходят данные')} <small>{g ? t('{0} стран', g.countries.length) : ''}</small>
        <span className="seg" role="group" aria-label={t('Вид')} style={{ marginLeft: 'auto' }}>
          <button aria-pressed={view === 'map'} onClick={() => switchView('map')}>{t('Карта')}</button>
          <button aria-pressed={view === 'list'} onClick={() => switchView('list')}>{t('Страны')}</button>
          <button aria-pressed={view === 'apps'} onClick={() => switchView('apps')}>{t('Программы')}</button>
        </span>
      </h2>

      {geo.error && <div className="error">{t('Не удалось построить карту: {0}', geo.error)}</div>}
      {g && !g.geoAvailable ? (
        <div className="note geo-note">
          <span>{t('Чтобы видеть страну каждого адреса, нужна база стран: около 7 МБ с db-ip.com, скачивается один раз и дальше работает без интернета.')}</span>
          <button className="btn" disabled={busy} onClick={download}>{busy ? t('Скачиваю…') : t('Скачать базу стран')}</button>
          {g.geoStatus && !busy && <span className="act-bad">{g.geoStatus}</span>}
        </div>
      ) : !g ? (
        <div className="panel"><div className="empty">{t('Загружаю…')}</div></div>
      ) : g.countries.length === 0 ? (
        <div className="panel"><div className="empty">{t('За этот период соединений с внешними адресами нет.')}</div></div>
      ) : view === 'apps' ? (
        <GeoApps flows={g.flows} onApp={onApp} />
      ) : view === 'list' ? (
        <div className="panel scroll" style={{ maxHeight: 560 }}>
          <table className="table geo-table">
            <thead>
              <tr>
                <th>{t('Страна')}</th>
                <th>{t('Получено')}</th>
                <th>{t('Отправлено')}</th>
                <th>{t('Программы')}</th>
                <th>{t('Адреса')}</th>
              </tr>
            </thead>
            <tbody>
              {g.countries.map(c => (
                <tr key={c.code} style={{ cursor: 'default' }}>
                  <td>
                    <div className="name">{countryName(c.code)}</div>
                    <div className="dim" style={{ fontSize: 12 }}>{t('{0} адресов · {1} подключений', c.ips, c.connects)}</div>
                  </td>
                  <td style={{ color: 'var(--net)' }}>{bytes(c.rx)}</td>
                  <td>{bytes(c.tx)}</td>
                  <td><AppsLine c={c} onApp={onApp} /></td>
                  <td className="dim geo-doms">{c.domains.slice(0, 3).join(', ') || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="geo-grid">
          <div className="panel geo-map">
            <WorldMap countries={g.countries} home={g.home} selected={selected} onSelect={setSelected} />
          </div>

          <div className="panel geo-side">
            {sel ? (
              <div className="geo-detail">
                <button className="link dim" onClick={() => setSelected(null)}>← {t('все страны')}</button>
                <h3>{countryName(sel.code)}</h3>
                <div className="geo-kv">
                  <span>{t('получено')}</span><b style={{ color: 'var(--net)' }}>{bytes(sel.rx)}</b>
                  <span>{t('отправлено')}</span><b>{bytes(sel.tx)}</b>
                  <span>{t('адресов')}</span><b>{sel.ips}</b>
                  <span>{t('подключений')}</span><b>{sel.connects}</b>
                </div>
                <h4>{t('Кто и куда')}</h4>
                {g.flows.filter(f => f.country === sel.code).slice(0, 25).map(f => (
                  <div key={f.app + f.dest} className="geo-flow">
                    <div>
                      <button className="link" onClick={() => onApp(f.app)}>{appLabel(f.app)}</button>
                      <span className="dim"> → </span><span className="geo-flow-dest">{f.dest}</span>
                    </div>
                    <div className="dim">{t('отправлено {0} · скачано {1}', bytes(f.tx), bytes(f.rx))}</div>
                  </div>
                ))}
              </div>
            ) : (
              <div className="geo-list">
                {g.countries.slice(0, 14).map(c => (
                  <button key={c.code} className="geo-row" onClick={() => setSelected(c.code)}>
                    <span className="geo-name">{countryName(c.code)}</span>
                    <span className="geo-bar"><i style={{ width: `${Math.max(2, ((c.tx + c.rx) / (total || 1)) * 100)}%` }} /></span>
                    <span className="dim">{bytes(c.tx + c.rx)}</span>
                    <span className="geo-row-apps">
                      {c.apps.slice(0, 2).map(a => `${appLabel(a.name)} ${bytes(a.bytes)}`).join(', ')}
                      {c.domains[0] && <em> · {c.domains[0]}</em>}
                    </span>
                  </button>
                ))}
                {g.countries.length > 14 && <div className="hint-line" style={{ margin: '6px 0 0' }}>{t('и ещё {0} — в списке', g.countries.length - 14)}</div>}
              </div>
            )}
          </div>
        </div>
      )}

      {g?.geoAvailable && (
        <p className="hint-line" style={{ marginTop: 8 }}>
          {g.home && t('Вы — {0} (по часовому поясу Windows).', countryName(g.home)) + ' '}
          {t('«Скачано» — то, что пришло к вам (видео, страницы, обновления), «отправлено» — то, что ушло от вас.')}{' '}
          {t('Страна определяется по IP офлайн. Крупные сервисы (Google, Microsoft, Cloudflare) отвечают из ближайшего дата-центра, поэтому «страна» — это где стоит сервер, а не где находится компания.')}
          {g.unknownBytes > 0 && ' ' + t('Без страны: {0}.', bytes(g.unknownBytes))}
          {' '}<a href="https://db-ip.com" target="_blank" rel="noreferrer">IP Geolocation by DB-IP</a> (CC BY 4.0).
        </p>
      )}
    </section>
  );
}
