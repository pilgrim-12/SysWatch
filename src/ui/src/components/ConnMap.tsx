import { useEffect, useMemo, useState } from 'react';
import { api, type ConnRow, type GeoCountry } from '../api';
import { appLabel, countryName } from '../format';
import { t } from '../i18n';
import { WorldMap } from './WorldMap';

let homeCache: string | null | undefined;

/** Живые соединения на карте: голубые — от вас (исходящие), жёлтые — к вам (входящие). */
export function ConnMap({ rows, onApp, hiddenLocal = 0 }: { rows: ConnRow[]; onApp: (n: string) => void; hiddenLocal?: number }) {
  const [home, setHome] = useState<string | null>(homeCache ?? null);
  const [selected, setSelected] = useState<string | null>(null);
  useEffect(() => {
    if (homeCache !== undefined) return;
    api.geoHome().then(h => { homeCache = h.home; setHome(h.home); }).catch(() => { homeCache = null; });
  }, []);

  const { countries, noCountry } = useMemo(() => {
    const map = new Map<string, GeoCountry & { appCount: Map<string, number>; remotes: Set<string> }>();
    let none = 0;
    for (const r of rows) {
      if (r.dir === 'listen') continue;
      if (!r.country || r.country === 'LAN') { none++; continue; }
      const c = map.get(r.country) ?? { code: r.country, tx: 0, rx: 0, connects: 0, ips: 0, apps: [], domains: [] as string[], appCount: new Map<string, number>(), remotes: new Set<string>() };
      if (r.dir === 'in') c.rx++; else c.tx++;
      c.connects++;
      c.remotes.add(r.remote);
      c.appCount.set(r.app, (c.appCount.get(r.app) ?? 0) + 1);
      if (r.domain && !c.domains.includes(r.domain)) c.domains.push(r.domain);
      map.set(r.country, c);
    }
    const list = [...map.values()].map(c => ({
      ...c, ips: c.remotes.size,
      apps: [...c.appCount].sort((a, b) => b[1] - a[1]).map(([name, n]) => ({ name, bytes: n })),
    }));
    // Входящие — выше: их важнее заметить.
    list.sort((a, b) => b.rx - a.rx || (b.tx + b.rx) - (a.tx + a.rx));
    return { countries: list as GeoCountry[], noCountry: none };
  }, [rows]);

  const sel = countries.find(c => c.code === selected) ?? null;
  const selRows = sel ? rows.filter(r => r.country === sel.code && r.dir !== 'listen') : [];

  return (
    <>
      <div className="geo-grid">
        <div className="panel geo-map">
          {countries.length === 0
            ? <div className="empty">
                {t('Нет внешних соединений под этот фильтр.')}
                {hiddenLocal > 0 && <><br />{t('Входящие сейчас только внутри компьютера ({0}): одна ваша программа подключается к другой. Они скрыты галочкой «localhost».', hiddenLocal)}</>}
                {noCountry > 0 && <><br />{t('Из локальной сети (роутер, телефон, ТВ): {0} — у них нет страны.', noCountry)}</>}
              </div>
            : <WorldMap mode="live" countries={countries} home={home} selected={selected} onSelect={setSelected} />}
        </div>
        <div className="panel geo-side">
          {sel ? (
            <div className="geo-detail">
              <button className="link dim" onClick={() => setSelected(null)}>← {t('все страны')}</button>
              <h3>{countryName(sel.code)}</h3>
              <div className="geo-kv">
                <span>{t('исходящих')}</span><b style={{ color: 'var(--net)' }}>{sel.tx}</b>
                <span>{t('входящих')}</span><b style={{ color: sel.rx ? 'var(--cpu)' : undefined }}>{sel.rx}</b>
              </div>
              <h4>{t('Кто и куда')}</h4>
              {selRows.slice(0, 40).map(r => (
                <div key={r.key} className="geo-flow">
                  <div>
                    <span style={{ color: r.dir === 'in' ? 'var(--cpu)' : 'var(--net)' }}>{r.dir === 'in' ? '↘' : '↗'} </span>
                    <button className="link" onClick={() => onApp(r.app)}>{appLabel(r.app)}</button>
                    <span className="dim"> {r.dir === 'in' ? '←' : '→'} </span>
                    <span className="geo-flow-dest">{r.domain ?? r.remote}</span>
                  </div>
                  <div className="dim">{r.remote}:{r.remotePort} · {r.proto}{r.dir === 'in' ? ` · ${t('к порту {0}', r.localPort)}` : ''}</div>
                </div>
              ))}
            </div>
          ) : (
            <div className="geo-list">
              {countries.slice(0, 16).map(c => (
                <button key={c.code} className="geo-row" onClick={() => setSelected(c.code)}>
                  <span className="geo-name">{countryName(c.code)}</span>
                  <span className="dim" style={{ gridColumn: 'span 2', textAlign: 'right' }}>
                    {c.tx > 0 && <span style={{ color: 'var(--net)' }}>↗ {c.tx}</span>}
                    {c.rx > 0 && <span style={{ color: 'var(--cpu)', marginLeft: 10 }}>↘ {c.rx}</span>}
                  </span>
                  <span className="geo-row-apps">{c.apps.slice(0, 3).map(a => appLabel(a.name)).join(', ')}</span>
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
      <div className="conn-map-legend">
        <span><i style={{ background: 'var(--net)' }} />{t('исходящие — ваш компьютер подключился к серверу')}</span>
        <span><i style={{ background: 'var(--cpu)' }} />{t('входящие — к вашему компьютеру подключились извне')}</span>
        {noCountry > 0 && <span>{t('без страны (локальная сеть или нет в базе): {0}', noCountry)}</span>}
      </div>
    </>
  );
}
