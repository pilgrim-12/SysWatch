import { useMemo } from 'react';
import { api, type GpuAdapterView, type GpuSeries } from '../api';
import { useLoad } from '../hooks';
import { COLOR, RANGES, appLabel, bytes, dayTime, pct } from '../format';
import { Plot } from '../components/Plot';
import { CrashView } from '../components/Crashes';

const daysOf = (range: number) => Math.max(1, Math.ceil(range / 1440));

export function Gpu({ range, setRange, onApp }: { range: number; setRange: (r: number) => void; onApp: (name: string) => void }) {
  const gpu = useLoad(() => api.gpu(range), [range], 60_000);
  const crashes = useLoad(() => api.crashes(Math.max(30, daysOf(range))), [range]);
  const d = gpu.data;
  const names = useMemo(() => new Map(d?.adapters.map(a => [a.key, a.name]) ?? []), [d]);

  return (
    <>
      <div className="head">
        <div>
          <h1>Видеокарта</h1>
          <p>Загрузка, видеопамять и температура по минутам, какие программы нагружали видеокарту и какие были сбои видеодрайвера.</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label="Период">
            {RANGES.map(r => <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>)}
          </div>
        </div>
      </div>

      {gpu.error && <div className="error">Не удалось загрузить историю видеокарты: {gpu.error}</div>}
      {d && !d.hasData && (
        <div className="panel"><div className="empty">
          Истории видеокарты пока нет. Её собирает агент начиная с этой версии: первые точки появятся через минуту-две после установки.
        </div></div>
      )}

      {d?.hasData && (
        <>
          <div className="hw-grid" style={{ marginBottom: 20 }}>
            {d.adapters.map(a => <AdapterCard key={a.key} a={a} />)}
          </div>

          {d.series.map(s => <Charts key={s.adapter} s={s} ts={d.ts} name={names.get(s.adapter) ?? ''} memTotal={d.adapters.find(a => a.key === s.adapter)?.memTotal ?? 0} multi={d.series.length > 1} />)}

          <section className="block">
            <h2>Кто нагружал видеокарту <small>средняя загрузка за период и пик видеопамяти</small></h2>
            <div className="panel">
              <table className="table dev-table">
                <thead><tr><th style={{ textAlign: 'left' }}>Программа</th>{d.adapters.length > 1 && <th style={{ textAlign: 'left' }}>Видеокарта</th>}<th>В среднем</th><th>Пик</th><th>Видеопамять</th></tr></thead>
                <tbody>
                  {d.apps.length === 0 && <tr><td colSpan={5} className="dim">Программы видеокарту почти не использовали.</td></tr>}
                  {d.apps.map(a => (
                    <tr key={a.name + a.adapter} onClick={() => onApp(a.name)}>
                      <td><span className="name">{appLabel(a.name)}</span></td>
                      {d.adapters.length > 1 && <td style={{ textAlign: 'left' }} className="dim">{names.get(a.adapter)}</td>}
                      <td style={{ color: COLOR.gpu }}>{pct(a.avgUtil)}</td>
                      <td>{pct(a.peakUtil, 0)}</td>
                      <td>{a.memMax ? bytes(a.memMax) : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </>
      )}

      <section className="block">
        <h2>Сбои видеодрайвера и оборудования <small>за {Math.max(30, daysOf(range))} дней, из журналов Windows</small></h2>
        {crashes.error && <div className="error">Не удалось прочитать журналы: {crashes.error}</div>}
        <CrashView data={crashes.data} only={['gpu', 'bsod', 'power', 'hardware']} />
        <p className="hint-line">Сброс видеодрайвера (экран гаснет на пару секунд) чаще всего — перегрев, разгон, нехватка питания или нестабильная версия драйвера. Все сбои системы — в разделе «Система → Сбои».</p>
      </section>
    </>
  );
}

function AdapterCard({ a }: { a: GpuAdapterView }) {
  const hot = a.temp != null && a.temp >= 85;
  return (
    <div className="panel hw-card">
      <div className="hw-title"><b title={a.name}>{a.name}</b>{a.lastTs && <span className="dim" style={{ fontSize: 12 }}>{dayTime(a.lastTs)}</span>}</div>
      <div className="gpu-big"><b>{a.util != null ? pct(a.util, 0) : '—'}</b><span>загрузка в последнюю минуту</span></div>
      <dl className="hw-stats">
        <div><dt>Видеопамять</dt><dd>{a.memUsed != null ? bytes(a.memUsed) : '—'}{a.memTotal ? <span className="dim" style={{ fontSize: 12, fontWeight: 400 }}> / {bytes(a.memTotal)}</span> : ''}</dd></div>
        <div><dt>Температура</dt><dd style={{ color: hot ? 'var(--warn)' : undefined }}>{a.temp != null ? `${a.temp.toFixed(0)} °C` : '—'}</dd></div>
        <div><dt>Максимум за период</dt><dd style={{ color: a.peakTemp != null && a.peakTemp >= 85 ? 'var(--warn)' : undefined }}>{a.peakTemp != null ? `${a.peakTemp.toFixed(0)} °C` : '—'}</dd></div>
        <div><dt>В среднем</dt><dd>{pct(a.avgUtil)}</dd></div>
        <div><dt>Пик загрузки</dt><dd>{pct(a.peakUtil, 0)}</dd></div>
        <div><dt>{a.fan != null ? 'Вентилятор' : 'Мощность'}</dt><dd>{a.fan != null ? `${a.fan} об/мин` : a.power != null ? `${a.power.toFixed(0)}%` : '—'}</dd></div>
      </dl>
      {a.temp == null && <div className="dim" style={{ fontSize: 12, marginTop: 6 }}>Температуру этот драйвер не сообщает (обычно у встроенной графики).</div>}
    </div>
  );
}

function Charts({ s, ts, name, memTotal, multi }: { s: GpuSeries; ts: number[]; name: string; memTotal: number; multi: boolean }) {
  const util = useMemo(() => [
    { label: 'Всего', color: COLOR.gpu, values: s.util, fill: true },
    { label: '3D', color: COLOR.gpu2, values: s.util3D },
    { label: 'Видео', color: COLOR.net, values: s.video },
    { label: 'Вычисления', color: COLOR.cpu, values: s.compute },
  ], [s]);
  const mem = useMemo(() => [{ label: 'Видеопамять', color: COLOR.mem, values: s.mem, fill: true }], [s]);
  const temp = useMemo(() => [{ label: 'Температура', color: COLOR.disk, values: s.temp, fill: true }], [s]);
  const hasTemp = s.temp.some(v => v != null);

  return (
    <section className="block">
      {multi && <h2>{name}</h2>}
      <div className="chart-row">
        <div>
          <div className="sub-h">Загрузка: всего, 3D, видео, вычисления</div>
          <div className="panel chart-box"><Plot ts={ts} series={util} height={170} yMax={100} yFormat={v => `${v}%`} /></div>
        </div>
        <div>
          <div className="sub-h">Видеопамять{memTotal ? ` из ${bytes(memTotal)}` : ''}</div>
          <div className="panel chart-box"><Plot ts={ts} series={mem} height={170} yMax={memTotal || undefined} yFormat={v => bytes(v, 0)} /></div>
        </div>
        {hasTemp && (
          <div>
            <div className="sub-h">Температура</div>
            <div className="panel chart-box"><Plot ts={ts} series={temp} height={170} yFormat={v => `${v} °C`} /></div>
          </div>
        )}
      </div>
    </section>
  );
}
