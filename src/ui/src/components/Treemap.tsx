import { useEffect, useMemo, useRef, useState } from 'react';
import { bytes } from '../format';

export interface MapChild { name: string; isDir: boolean; size: number }
export interface MapItem { path: string; name: string; isDir: boolean; size: number; delta: number | null; children: MapChild[]; other: number }

interface Rect { x: number; y: number; w: number; h: number }

/** Squarified treemap (Bruls, Huizing, van Wijk): прямоугольники ближе к квадрату — их легче сравнивать глазом. */
function squarify(values: number[], box: Rect): Rect[] {
  const out: Rect[] = new Array(values.length);
  const total = values.reduce((s, v) => s + v, 0);
  if (total <= 0 || box.w <= 0 || box.h <= 0) return values.map(() => ({ x: box.x, y: box.y, w: 0, h: 0 }));
  const scale = (box.w * box.h) / total;
  const order = values.map((v, i) => ({ i, a: v * scale })).filter(o => o.a > 0);

  let { x, y, w, h } = box;
  let row: { i: number; a: number }[] = [];

  const worst = (r: { a: number }[], side: number) => {
    const sum = r.reduce((s, o) => s + o.a, 0);
    const max = Math.max(...r.map(o => o.a)), min = Math.min(...r.map(o => o.a));
    return Math.max((side * side * max) / (sum * sum), (sum * sum) / (side * side * min));
  };

  const layout = (r: { i: number; a: number }[]) => {
    const sum = r.reduce((s, o) => s + o.a, 0);
    if (w >= h) {
      const cw = sum / h;
      let cy = y;
      for (const o of r) { const ch = o.a / cw; out[o.i] = { x, y: cy, w: cw, h: ch }; cy += ch; }
      x += cw; w -= cw;
    } else {
      const rh = sum / w;
      let cx = x;
      for (const o of r) { const cw = o.a / rh; out[o.i] = { x: cx, y, w: cw, h: rh }; cx += cw; }
      y += rh; h -= rh;
    }
  };

  for (const o of order) {
    const side = Math.min(w, h);
    if (row.length === 0 || worst([...row, o], side) <= worst(row, side)) row.push(o);
    else { layout(row); row = [o]; }
  }
  if (row.length) layout(row);
  values.forEach((_, i) => { if (!out[i]) out[i] = { x: box.x, y: box.y, w: 0, h: 0 }; });
  return out;
}

// Приглушённые тона на тёмном фоне: различимы между собой и не спорят с цветами ресурсов в шапке.
const HUES = ['#c98b2e', '#7d64c9', '#c45f50', '#2f9bb0', '#4c9a6a', '#4f74c4', '#b05c8f', '#8a8f3a'];
const FILE = '#4a5a70';
const OTHER = '#2a3a52';

interface Tip { x: number; y: number; title: string; lines: string[] }

export function Treemap({ items, other, total, onOpen }: {
  items: MapItem[];
  other: number;
  total: number;
  onOpen: (path: string) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 800, h: 460 });
  const [tip, setTip] = useState<Tip | null>(null);

  useEffect(() => {
    const el = host.current!;
    const ro = new ResizeObserver(() => setSize({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const tiles = useMemo(() => {
    const entries = [...items.map(i => i.size), other];
    const rects = squarify(entries, { x: 0, y: 0, w: size.w, h: size.h });
    let dirIndex = 0;
    return entries.map((_, k) => {
      const item = k < items.length ? items[k] : null;
      const color = !item ? OTHER : item.isDir ? HUES[dirIndex++ % HUES.length] : FILE;
      return { item, rect: rects[k], color };
    });
  }, [items, other, size]);

  const pct = (v: number) => (total > 0 ? `${((v / total) * 100).toFixed(1).replace('.', ',')}% папки` : '');
  const show = (e: React.MouseEvent, title: string, lines: string[]) => {
    const r = host.current!.getBoundingClientRect();
    setTip({ x: e.clientX - r.left, y: e.clientY - r.top, title, lines });
  };

  return (
    <div className="tmap" ref={host} onMouseLeave={() => setTip(null)}>
      {tiles.map(({ item, rect, color }, k) => {
        if (rect.w < 1 || rect.h < 1) return null;
        const big = rect.w > 70 && rect.h > 34;
        const style = { left: rect.x, top: rect.y, width: rect.w, height: rect.h, '--tile': color } as React.CSSProperties;

        if (!item) {
          return (
            <div key="other" className="tile other" style={style}
              onMouseMove={e => show(e, 'Мелкие файлы и папки', [bytes(other), pct(other), 'Каждый объект меньше 100 МБ'])}>
              {big && <span className="tile-label"><b>Мелкое</b> {bytes(other)}</span>}
            </div>
          );
        }

        // Внутри папки — её содержимое, если плитка достаточно крупная.
        const header = big ? 22 : 0;
        const inner = { x: 3, y: header + 1, w: rect.w - 6, h: rect.h - header - 4 };
        const subs = item.isDir && inner.w > 30 && inner.h > 24 ? [...item.children.map(c => c.size), item.other] : [];
        const subRects = subs.length ? squarify(subs, inner) : [];
        const growing = item.delta != null && item.delta >= 300 * 1024 * 1024;

        return (
          <div key={item.path} className={`tile ${item.isDir ? 'dir' : 'file'}`} style={style}
            onClick={() => item.isDir && onOpen(item.path)}
            onMouseMove={e => show(e, item.name, [
              bytes(item.size), pct(item.size),
              ...(item.delta ? [`${item.delta > 0 ? '+' : '−'}${bytes(Math.abs(item.delta))} с прошлого снимка`] : []),
              item.isDir ? 'Нажми, чтобы открыть' : 'Файл',
            ])}>
            {big && (
              <span className="tile-label">
                <b>{item.name}</b> {bytes(item.size)}
                {growing && <em className="tile-grow">+{bytes(item.delta!)}</em>}
              </span>
            )}
            {subRects.map((r, j) => {
              if (r.w < 2 || r.h < 2) return null;
              const child = j < item.children.length ? item.children[j] : null;
              const label = r.w > 64 && r.h > 18;
              return (
                <div key={j} className={`subtile ${child ? (child.isDir ? 'dir' : 'file') : 'rest'}`}
                  style={{ left: r.x, top: r.y, width: r.w, height: r.h }}
                  onClick={e => { if (child?.isDir) { e.stopPropagation(); onOpen(`${item.path}\\${child.name}`); } }}
                  onMouseMove={e => {
                    e.stopPropagation();
                    show(e, child ? `${item.name}\\${child.name}` : `${item.name}: мелкое`, [
                      bytes(child ? child.size : item.other),
                      child ? (child.isDir ? 'Нажми, чтобы открыть' : 'Файл') : 'Файлы и папки меньше 100 МБ',
                    ]);
                  }}>
                  {label && <span className="subtile-label">{child ? child.name : 'мелкое'}</span>}
                </div>
              );
            })}
          </div>
        );
      })}

      {tip && (
        <div className="tmap-tip" style={{ left: Math.min(tip.x + 14, size.w - 230), top: Math.min(tip.y + 16, size.h - 90) }}>
          <b>{tip.title}</b>
          {tip.lines.map((l, i) => <div key={i}>{l}</div>)}
        </div>
      )}
    </div>
  );
}
