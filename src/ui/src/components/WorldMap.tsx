import { useEffect, useMemo, useRef, useState } from 'react';
import { geoCentroid, geoNaturalEarth1, geoPath, type GeoPermissibleObjects } from 'd3-geo';
import { feature } from 'topojson-client';
import type { Topology, GeometryCollection } from 'topojson-specification';
import atlas from 'world-atlas/countries-110m.json';
import type { GeoCountry } from '../api';
import { bytes, countryName } from '../format';
import { t } from '../i18n';
import { NAME_TO_A2, NUMERIC_TO_A2, SMALL } from './countries';

const W = 960, H = 430;

/** Центры стран, у которых geoCentroid уезжает из-за заморских территорий или формы. [широта, долгота] */
const CENTER: Record<string, [number, number]> = {
  US: [39, -98], RU: [60, 90], FR: [46.6, 2.4], NO: [61, 9], NL: [52.2, 5.3], CA: [57, -100], GB: [53, -1.5],
  CN: [35, 103], AU: [-25, 134], NZ: [-41, 174], CL: [-33, -71], ID: [-2, 118], JP: [36, 138], MY: [4, 102],
  DK: [56, 9.5], ES: [40, -3.7], PT: [39.5, -8], IT: [42.8, 12.5], GR: [39.3, 22], HR: [45.3, 16], PH: [12, 122],
};

type Shape = { code: string | null; d: string; center: [number, number] | null };

// Карта и проекция считаются один раз: геометрия встроена в программу, интернет не нужен.
const projection = geoNaturalEarth1();
const topo = atlas as unknown as Topology<{ countries: GeometryCollection<{ name: string }> }>;
const land = feature(topo, topo.objects.countries);
// Антарктиду не рисуем: трафика туда нет, а места она занимает много.
land.features = land.features.filter(f => String(f.id) !== '010');
projection.fitExtent([[4, 4], [W - 4, H - 4]], land as unknown as GeoPermissibleObjects);
const path = geoPath(projection);

const SHAPES: Shape[] = land.features.map(f => {
  const code = (f.id != null ? NUMERIC_TO_A2[String(f.id)] : undefined) ?? NAME_TO_A2[f.properties?.name ?? ''] ?? null;
  const c = code && CENTER[code] ? projection([CENTER[code][1], CENTER[code][0]]) : projection(geoCentroid(f));
  return { code, d: path(f) ?? '', center: c ? [c[0], c[1]] : null };
});

function point(code: string): [number, number] | null {
  if (CENTER[code]) { const p = projection([CENTER[code][1], CENTER[code][0]]); return p ? [p[0], p[1]] : null; }
  const s = SHAPES.find(x => x.code === code && x.center);
  if (s?.center) return s.center;
  const small = SMALL[code];
  if (small) { const p = projection([small[1], small[0]]); return p ? [p[0], p[1]] : null; }
  return null;
}

/** Дуга от дома к стране: изгиб вверх, пропорционально расстоянию. */
function arc(a: [number, number], b: [number, number], flip = false) {
  const [x1, y1] = a, [x2, y2] = b;
  const mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
  const dx = x2 - x1, dy = y2 - y1, len = Math.hypot(dx, dy);
  // Нормаль, направленная вверх по экрану.
  let nx = -dy / (len || 1), ny = dx / (len || 1);
  if (ny > 0) { nx = -nx; ny = -ny; }
  if (flip) { nx = -nx * 0.6; ny = -ny * 0.6; } // встречная линия изгибается в другую сторону
  const lift = Math.min(160, len * 0.28);
  return `M${x1},${y1} Q${mx + nx * lift},${my + ny * lift} ${x2},${y2}`;
}

/**
 * mode = 'traffic' — объём за период (tx/rx в байтах);
 * mode = 'live' — соединения прямо сейчас: tx — исходящих, rx — входящих, apps[].bytes — число соединений.
 */
export function WorldMap({ countries, home, selected, onSelect, mode = 'traffic' }: {
  countries: GeoCountry[]; home: string | null; selected: string | null; onSelect: (code: string | null) => void; mode?: 'traffic' | 'live';
}) {
  const live = mode === 'live';
  const fmt = (v: number) => live ? String(v) : bytes(v);
  const [hover, setHover] = useState<{ c: GeoCountry; x: number; y: number; right: boolean } | null>(null);

  // Масштаб и сдвиг в координатах карты: колесо — приблизить к курсору, перетаскивание — двигать.
  const [z, setZ] = useState({ k: 1, x: 0, y: 0 });
  const svgRef = useRef<SVGSVGElement>(null);
  const drag = useRef<{ sx: number; sy: number; x: number; y: number; moved: boolean } | null>(null);
  const clamp = (v: { k: number; x: number; y: number }) => {
    const k = Math.min(12, Math.max(1, v.k));
    return { k, x: Math.min(0, Math.max(W - W * k, v.x)), y: Math.min(0, Math.max(H - H * k, v.y)) };
  };
  const zoomAt = (factor: number, px = W / 2, py = H / 2) => setZ(cur => {
    const k = Math.min(12, Math.max(1, cur.k * factor));
    return clamp({ k, x: px - (px - cur.x) * (k / cur.k), y: py - (py - cur.y) * (k / cur.k) });
  });
  // Колесо: обработчик не пассивный, иначе вместо масштаба прокручивается вся страница.
  useEffect(() => {
    const el = svgRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = el.getBoundingClientRect();
      zoomAt(e.deltaY < 0 ? 1.25 : 0.8, ((e.clientX - r.left) / r.width) * W, ((e.clientY - r.top) / r.height) * H);
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);
  const onDown = (e: React.PointerEvent) => { drag.current = { sx: e.clientX, sy: e.clientY, x: z.x, y: z.y, moved: false }; };
  const onMove = (e: React.PointerEvent) => {
    const d = drag.current;
    if (!d || !svgRef.current) return;
    const r = svgRef.current.getBoundingClientRect();
    const dx = ((e.clientX - d.sx) / r.width) * W, dy = ((e.clientY - d.sy) / r.height) * H;
    if (!d.moved && Math.hypot(e.clientX - d.sx, e.clientY - d.sy) < 4) return;
    if (!d.moved) { d.moved = true; svgRef.current.setPointerCapture(e.pointerId); setHover(null); }
    setZ(cur => clamp({ k: cur.k, x: d.x + dx, y: d.y + dy }));
  };
  const onUp = () => { setTimeout(() => { drag.current = null; }, 0); };
  // Клик после перетаскивания не считается выбором страны.
  const pick = (code: string | null) => { if (!drag.current?.moved) onSelect(code); };
  const inv = 1 / z.k; // точки и линии остаются одного размера при любом масштабе
  const byCode = useMemo(() => new Map(countries.map(c => [c.code, c])), [countries]);
  const max = Math.max(1, ...countries.map(c => c.tx + c.rx));
  // Логарифмическая шкала: иначе одна страна с гигабайтами «гасит» все остальные.
  const share = (v: number) => Math.log1p(v) / Math.log1p(max);
  const homePt = home ? point(home) : null;

  // Линии — только к главным странам, иначе карта превращается в «ежа».
  const topCodes = new Set(countries.filter(c => c.code !== home).slice(0, live ? 40 : 12).map(c => c.code));
  const dests = countries
    .map(c => ({ c, p: point(c.code) }))
    .filter((x): x is { c: GeoCountry; p: [number, number] } => !!x.p)
    .slice(0, 60);

  const tip = (c: GeoCountry, e: React.MouseEvent) => {
    const r = (e.currentTarget.closest('svg') as SVGSVGElement).getBoundingClientRect();
    setHover({ c, x: e.clientX - r.left, y: e.clientY - r.top, right: e.clientX - r.left > r.width * 0.6 });
  };

  return (
    <div className="world-map">
      <svg ref={svgRef} viewBox={`0 0 ${W} ${H}`} role="img" aria-label={t('Карта стран, с которыми обменивался данными компьютер')}
        className={z.k > 1 ? 'zoomed' : undefined}
        onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerLeave={onUp}
        onDoubleClick={e => { const r = svgRef.current!.getBoundingClientRect(); zoomAt(2, ((e.clientX - r.left) / r.width) * W, ((e.clientY - r.top) / r.height) * H); }}
        onClick={e => { if (e.target === e.currentTarget) pick(null); }}>
        <defs>
          <radialGradient id="wm-glow"><stop offset="0%" stopColor="#4fc3d9" stopOpacity=".55" /><stop offset="100%" stopColor="#4fc3d9" stopOpacity="0" /></radialGradient>
        </defs>
        <g transform={`translate(${z.x},${z.y}) scale(${z.k})`}>
        {SHAPES.map((s, i) => {
          const c = s.code ? byCode.get(s.code) : undefined;
          const isHome = s.code != null && s.code === home;
          return (
            <path key={i} d={s.d}
              className={`wm-country${c ? ' has' : ''}${isHome ? ' home' : ''}${s.code && s.code === selected ? ' sel' : ''}`}
              style={c ? { fillOpacity: 0.18 + 0.55 * share(c.tx + c.rx) } : undefined}
              onMouseMove={c ? e => tip(c, e) : undefined}
              onMouseLeave={() => setHover(null)}
              onClick={c ? () => pick(c.code === selected ? null : c.code) : () => pick(null)} />
          );
        })}

        {homePt && dests.filter(x => x.c.code !== home && (x.c.code === selected || x.c.code === hover?.c.code || topCodes.has(x.c.code))).map(({ c, p }) => {
          const k = share(c.tx + c.rx);
          const focus = selected === c.code || hover?.c.code === c.code;
          const dim = (selected || hover) && !focus;
          const d = arc(homePt, p);
          // Входящие (кто-то подключился к вам) — жёлтая линия от страны к вам.
          const din = live && c.rx > 0 ? arc(p, homePt, true) : null;
          return (
            <g key={'a' + c.code} className={`wm-link${dim ? ' dim' : ''}${focus ? ' focus' : ''}`}>
              {(!live || c.tx > 0) && <>
                <path d={d} className="wm-arc" style={{ strokeWidth: (0.6 + 1.6 * k) * inv }} />
                {/* светлая «капля» бежит от вас к стране — направление и интенсивность обмена */}
                <path d={d} className="wm-pulse" pathLength={100} style={{ animationDuration: `${3.2 - 1.8 * k}s`, strokeWidth: 2.2 * inv }} />
              </>}
              {din && <>
                <path d={din} className="wm-arc in" style={{ strokeWidth: 1.4 * inv }} />
                <path d={din} className="wm-pulse in" pathLength={100} style={{ animationDuration: '1.6s', strokeWidth: 2.4 * inv }} />
              </>}
            </g>
          );
        })}

        {dests.map(({ c, p }) => {
          const r = (2 + 7 * Math.sqrt((c.tx + c.rx) / max)) * Math.pow(inv, 0.7);
          return (
            <g key={'d' + c.code}>
            {topCodes.has(c.code) && <circle cx={p[0]} cy={p[1]} r={r * 2.6} fill="url(#wm-glow)" pointerEvents="none" />}
            <circle cx={p[0]} cy={p[1]} r={r}
              className={`wm-dot${selected === c.code ? ' sel' : ''}${selected && selected !== c.code ? ' dim' : ''}`}
              onMouseMove={e => tip(c, e)} onMouseLeave={() => setHover(null)}
              style={{ strokeWidth: 0.8 * inv }}
              onClick={() => pick(c.code === selected ? null : c.code)} />
            </g>
          );
        })}

        {homePt && (
          <g className="wm-home" transform={`translate(${homePt[0]},${homePt[1]}) scale(${inv})`}>
            <circle r={9} className="wm-home-pulse" />
            <circle r={4.5} />
          </g>
        )}
        </g>
      </svg>

      <div className="wm-zoom" role="group" aria-label={t('Масштаб карты')}>
        <button onClick={() => zoomAt(1.6)} title={t('Приблизить')}>+</button>
        <button onClick={() => zoomAt(1 / 1.6)} title={t('Отдалить')} disabled={z.k <= 1}>−</button>
        <button onClick={() => setZ({ k: 1, x: 0, y: 0 })} title={t('Весь мир')} disabled={z.k <= 1}>⤢</button>
      </div>
      {z.k === 1 && <div className="wm-help">{t('Колесо мыши — масштаб, перетаскивание — сдвиг')}</div>}

      {hover && (
        <div className={`wm-tip${hover.right ? ' left' : ''}`} style={{ left: `${hover.x}px`, top: `${hover.y}px` }}>
          <b>{countryName(hover.c.code)}</b>
          <div className="wm-tip-sum">{live
            ? t('исходящих {0} · входящих {1}', hover.c.tx, hover.c.rx)
            : t('скачано {0} · отправлено {1}', bytes(hover.c.rx), bytes(hover.c.tx))}</div>
          {hover.c.apps.slice(0, 4).map(a => (
            <div key={a.name} className="wm-tip-row"><span>{a.name.replace(/\.exe$/i, '')}</span><span>{live ? `×${fmt(a.bytes)}` : fmt(a.bytes)}</span></div>
          ))}
          {hover.c.domains.length > 0 && <div className="wm-tip-doms">{hover.c.domains.slice(0, 4).join(' · ')}</div>}
          {!live && <div className="dim">{t('{0} адресов · {1} подключений', hover.c.ips, hover.c.connects)}</div>}
          {live && hover.c.rx > 0 && <div className="wm-tip-in">{t('К вам подключаются отсюда: {0}', hover.c.rx)}</div>}
        </div>
      )}
    </div>
  );
}
