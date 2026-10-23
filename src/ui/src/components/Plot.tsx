import { useEffect, useRef } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';

export interface PlotSeries {
  label: string;
  color: string;
  values: (number | null)[];
  fill?: boolean;
  format?: (v: number) => string;
}

interface Props {
  ts: number[];
  series: PlotSeries[];
  height: number;
  yFormat?: (v: number) => string;
  yMax?: number;
  axes?: boolean;        // false — «голый» график без осей (осциллограф)
  glow?: boolean;
  onPick?: (ts: number) => void;
}

function gradient(u: uPlot, color: string) {
  const ctx = u.ctx;
  const g = ctx.createLinearGradient(0, u.bbox.top, 0, u.bbox.top + u.bbox.height);
  g.addColorStop(0, color + '55');
  g.addColorStop(1, color + '00');
  return g;
}

/** Тонкая обёртка над uPlot: пересоздаёт график при смене набора серий, иначе только обновляет данные. */
export function Plot({ ts, series, height, yFormat, yMax, axes = true, glow = false, onPick }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const plot = useRef<uPlot | null>(null);
  const pickRef = useRef(onPick);
  pickRef.current = onPick;

  const shape = series.map(s => s.label + s.color + (s.fill ? 'f' : '')).join('|') + height + axes + yMax;

  useEffect(() => {
    const el = host.current!;
    const axisStyle = {
      stroke: '#8fa3bc',
      grid: { stroke: '#24364f', width: 1 },
      ticks: { show: false },
      font: '12px "Fira Sans Condensed", sans-serif',
    };
    const opts: uPlot.Options = {
      width: el.clientWidth || 300,
      height,
      padding: axes ? [8, 8, 0, 0] : [2, 0, 0, 0],
      legend: { show: false },
      cursor: axes
        ? { y: false, points: { size: 6 }, drag: { x: false, y: false } }
        : { show: false },
      scales: {
        x: { time: true },
        y: { range: (_u, _min, max) => [0, yMax ?? Math.max(max * 1.15, 1e-9)] },
      },
      axes: axes
        ? [
            { ...axisStyle, grid: { show: false }, values: (u, vals) => { const span = (u.scales.x.max ?? 0) - (u.scales.x.min ?? 0); return vals.map(v => new Date(v * 1000).toLocaleTimeString('ru-RU', span < 600 ? { hour: '2-digit', minute: '2-digit', second: '2-digit' } : span > 2 * 86400 ? { weekday: 'short', hour: '2-digit', minute: '2-digit' } : { hour: '2-digit', minute: '2-digit' })); } },
            { ...axisStyle, size: 64, values: (_u, vals) => vals.map(v => (yFormat ? yFormat(v) : String(v))) },
          ]
        : [{ show: false }, { show: false }],
      series: [
        {},
        ...series.map(s => ({
          label: s.label,
          stroke: s.color,
          width: glow ? 1.75 : 1.5,
          fill: s.fill ? (u: uPlot) => gradient(u, s.color) : undefined,
          points: { show: false },
        })),
      ],
      hooks: {
        drawSeries: glow
          ? [(u: uPlot) => { u.ctx.shadowBlur = 0; }]
          : [],
        drawClear: glow
          ? [(u: uPlot) => { u.ctx.shadowColor = series[0]?.color ?? '#fff'; u.ctx.shadowBlur = 8; }]
          : [],
      },
    };
    const p = new uPlot(opts, [ts, ...series.map(s => s.values)] as uPlot.AlignedData, el);
    plot.current = p;

    if (axes) {
      p.over.addEventListener('click', () => {
        const idx = p.cursor.idx;
        if (idx != null && pickRef.current) pickRef.current(p.data[0][idx]);
      });
    }

    const ro = new ResizeObserver(() => p.setSize({ width: el.clientWidth, height }));
    ro.observe(el);
    return () => { ro.disconnect(); p.destroy(); plot.current = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shape]);

  useEffect(() => {
    plot.current?.setData([ts, ...series.map(s => s.values)] as uPlot.AlignedData);
  }, [ts, series]);

  return <div ref={host} style={{ height }} />;
}
