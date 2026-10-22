import { useEffect, useRef, useState } from 'react';
import { appLabel, time } from '../format';

interface Props {
  rows: { name: string; values: number[] }[];
  minutes: number[];
  color: string;         // hex цвета ресурса
  max?: number;          // значение, при котором клетка полностью яркая
  selected: number | null;
  format: (v: number) => string;
  onPick: (ts: number) => void;
  onApp: (name: string) => void;
}

const ROW = 24;

function hexToRgb(hex: string) {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

/** Тепловая карта «приложения × минуты». Клик по клетке выбирает минуту для снимка. */
export function Heatmap({ rows, minutes, color, max, selected, format, onPick, onApp }: Props) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [hover, setHover] = useState<{ x: number; y: number; r: number; c: number } | null>(null);
  const [width, setWidth] = useState(800);

  useEffect(() => {
    const el = canvas.current!.parentElement!;
    const ro = new ResizeObserver(() => setWidth(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    const cv = canvas.current!;
    const dpr = window.devicePixelRatio || 1;
    const h = rows.length * ROW;
    cv.width = Math.floor(width * dpr);
    cv.height = Math.floor(h * dpr);
    cv.style.height = `${h}px`;
    const ctx = cv.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, h);

    const n = minutes.length;
    if (!n) return;
    const cw = width / n;
    const top = max ?? Math.max(1e-9, ...rows.flatMap(r => r.values));
    const [cr, cg, cb] = hexToRgb(color);

    rows.forEach((r, ri) => {
      ctx.fillStyle = '#142236';
      ctx.fillRect(0, ri * ROW + 1, width, ROW - 2);
      r.values.forEach((v, ci) => {
        if (v <= 0) return;
        // корень — чтобы слабая активность тоже была видна
        const a = Math.min(1, Math.sqrt(v / top));
        ctx.fillStyle = `rgba(${cr},${cg},${cb},${0.08 + a * 0.92})`;
        ctx.fillRect(ci * cw, ri * ROW + 1, Math.max(1, cw - (cw > 4 ? 1 : 0)), ROW - 2);
      });
    });

    if (selected != null) {
      const ci = minutes.indexOf(selected);
      if (ci >= 0) {
        ctx.strokeStyle = '#e6edf5';
        ctx.lineWidth = 1.5;
        ctx.strokeRect(ci * cw + 0.75, 0.75, Math.max(2, cw - 1.5), h - 1.5);
      }
    }
  }, [rows, minutes, color, max, selected, width]);

  const cell = (e: React.MouseEvent) => {
    const rect = canvas.current!.getBoundingClientRect();
    const x = e.clientX - rect.left, y = e.clientY - rect.top;
    const c = Math.min(minutes.length - 1, Math.max(0, Math.floor((x / rect.width) * minutes.length)));
    const r = Math.min(rows.length - 1, Math.max(0, Math.floor(y / ROW)));
    return { x, y, r, c };
  };

  const ticks = minutes.length
    ? [0, 0.25, 0.5, 0.75, 1].map(f => minutes[Math.min(minutes.length - 1, Math.round(f * (minutes.length - 1)))])
    : [];

  return (
    <div>
      <div className="heat">
        <div className="heat-labels">
          {rows.map(r => (
            <div key={r.name} className="heat-label" title={r.name} onClick={() => onApp(r.name)}>
              {appLabel(r.name)}
            </div>
          ))}
        </div>
        <div style={{ position: 'relative' }}>
          <canvas
            ref={canvas}
            onMouseMove={e => setHover(cell(e))}
            onMouseLeave={() => setHover(null)}
            onClick={e => onPick(minutes[cell(e).c])}
          />
          {hover && rows[hover.r] && (
            <div className="panel" style={{
              position: 'absolute', pointerEvents: 'none', padding: '6px 10px', fontSize: 13, whiteSpace: 'nowrap',
              left: Math.min(hover.x + 12, width - 200), top: hover.y + 14, zIndex: 2,
            }}>
              <b>{appLabel(rows[hover.r].name)}</b>, {time(minutes[hover.c])}<br />
              <span style={{ color }}>{format(rows[hover.r].values[hover.c] ?? 0)}</span>
            </div>
          )}
        </div>
      </div>
      <div className="heat-axis">{ticks.map((t, i) => <span key={i}>{time(t)}</span>)}</div>
    </div>
  );
}
