import { useEffect, useMemo, useState } from 'react';
import { api, type Why } from '../api';
import { WhyCard } from '../components/WhyCard';
import { useLoad } from '../hooks';
import { COLOR, RANGES, appLabel, bytes, dayTime, pct } from '../format';
import { Plot } from '../components/Plot';
import { Heatmap } from '../components/Heatmap';
import { GPU_COLORS, shortGpu } from '../components/Scopes';
import { t } from '../i18n';

type Metric = 'cpu' | 'mem' | 'net';

const METRICS: { key: Metric; label: string; color: string; fmt: (v: number) => string }[] = [
  { key: 'cpu', label: t('Процессор'), color: COLOR.cpu, fmt: v => pct(v) },
  { key: 'mem', label: t('Память'), color: COLOR.mem, fmt: v => bytes(v) },
  { key: 'net', label: t('Сеть'), color: COLOR.net, fmt: v => t('{0} за минуту', bytes(v)) },
];

export function Timeline({ range, setRange, onApp }: { range: number; setRange: (m: number) => void; onApp: (n: string) => void }) {
  const [metric, setMetric] = useState<Metric>('cpu');
  const [picked, setPicked] = useState<number | null>(null);
  const tl = useLoad(() => api.timeline(range), [range], 60_000);
  const m = METRICS.find(x => x.key === metric)!;

  // Если агент работает меньше выбранного периода — начинаем график с первой записанной минуты, без пустой половины.
  const { data, since } = useMemo(() => {
    const d = tl.data;
    if (!d) return { data: null, since: null as number | null };
    const first = d.minutes.findIndex((_, i) => d.system.cpu[i] != null || d.system.mem[i] != null);
    if (first <= 0) return { data: d, since: null };
    const cut = Math.max(0, first - 1);
    return {
      since: d.minutes[first],
      data: {
        ...d,
        minutes: d.minutes.slice(cut),
        system: { cpu: d.system.cpu.slice(cut), mem: d.system.mem.slice(cut), gpu: d.system.gpu?.map(g => ({ ...g, util: g.util.slice(cut) })) },
        apps: d.apps.map(a => ({ ...a, cpu: a.cpu.slice(cut), mem: a.mem.slice(cut), net: a.net.slice(cut) })),
      },
    };
  }, [tl.data]);
  useEffect(() => { setPicked(null); }, [range]);

  // Видеокарты, которые за период хоть раз были загружены больше 1%: простаивающую встроенную графику не рисуем.
  const gpus = useMemo(() => (data?.system.gpu ?? []).filter(g => g.util.some(v => v != null && v > 1)), [data]);
  const sysSeries = useMemo(() => data ? [
    { label: t('Процессор'), color: COLOR.cpu, values: data.system.cpu, fill: true },
    { label: t('Память'), color: COLOR.mem, values: data.system.mem },
    ...gpus.map((g, i) => ({ label: shortGpu(g.name), color: GPU_COLORS[i % GPU_COLORS.length], values: g.util as number[] })),
  ] : [], [data, gpus]);

  const rows = useMemo(() => {
    if (!data) return [];
    return data.apps
      .map(a => ({ name: a.name, values: a[metric], total: a[metric].reduce((s, v) => s + v, 0) }))
      .filter(r => r.total > 0)
      .sort((a, b) => b.total - a.total)
      .slice(0, 16);
  }, [data, metric]);

  const idx = data && picked != null ? data.minutes.indexOf(picked) : -1;
  const [why, setWhy] = useState<{ at: number; data: Why | null } | null>(null);
  const explain = async () => {
    if (picked == null || !data) return;
    const bin = data.minutes.length > 1 ? data.minutes[1] - data.minutes[0] : 60;
    const at = picked;
    setWhy({ at, data: null });
    try { const w = await api.why(at - 120, at + Math.max(bin, 60) + 120); setWhy(cur => cur?.at === at ? { at, data: w } : cur); }
    catch { setWhy(null); }
  };
  const snapshot = useMemo(() => {
    if (!data || idx < 0) return null;
    return data.apps
      .map(a => ({ name: a.name, cpu: a.cpu[idx], mem: a.mem[idx], net: a.net[idx] }))
      .sort((a, b) => b.cpu - a.cpu)
      .slice(0, 8);
  }, [data, idx]);

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Таймлайн')}</h1>
          <p>{t('Кто и когда нагружал систему. Нажми на минуту на графике или на карте, чтобы увидеть снимок того момента.')}</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label={t('Период')}>
            {RANGES.map(r => (
              <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>
            ))}
          </div>
        </div>
      </div>

      {tl.error && <div className="error">{t('Не удалось загрузить историю: {0}', tl.error)}</div>}

      <section className="block">
        <h2>{t('Вся система')} <small>{gpus.length ? t('процессор, память и видеокарта, % по минутам') : t('процессор и занятая память, % по минутам')}</small>
          {since != null && <small style={{ marginLeft: 'auto' }}>{t('данные есть с {0} — раньше агент не работал', dayTime(since))}</small>}
        </h2>
        <div className="panel chart-box">
          {gpus.length > 0 && (
            <div className="plot-legend">
              {sysSeries.map(x => <span key={x.label}><i style={{ background: x.color }} />{x.label}</span>)}
            </div>
          )}
          {data && data.minutes.length > 1
            ? <Plot ts={data.minutes} series={sysSeries} height={170} yMax={100} yFormat={v => `${v}%`} onPick={setPicked} />
            : <div className="empty">{tl.loading ? t('Загружаю…') : t('За этот период агент ещё ничего не записал.')}</div>}
        </div>
      </section>

      <div className="grid-2">
        <section className="block">
          <h2>
            {t('Программы по минутам')}
            <span className="seg" role="group" aria-label={t('Показатель')} style={{ marginLeft: 'auto' }}>
              {METRICS.map(x => (
                <button key={x.key} aria-pressed={metric === x.key} onClick={() => setMetric(x.key)}>{x.label}</button>
              ))}
            </span>
          </h2>
          <div className="panel" style={{ padding: 12 }}>
            {data && rows.length
              ? <Heatmap rows={rows} minutes={data.minutes} color={m.color} selected={picked} format={m.fmt} onPick={setPicked} onApp={onApp} />
              : <div className="empty">{t('Нет данных за период')}</div>}
          </div>
        </section>

        <section className="block">
          <h2>{t('Снимок момента')}</h2>
          <div className="panel snapshot">
            {!snapshot || picked == null ? (
              <div className="empty">{t('Выбери минуту на графике или на карте слева.')}</div>
            ) : (
              <>
                <h3>{dayTime(picked)} <span>
                  {t('процессор {0}, память {1}', pct(data!.system.cpu[idx], 0), pct(data!.system.mem[idx], 0))}
                  {gpus.map(g => g.util[idx] != null && <span key={g.name}>{`, ${shortGpu(g.name)} ${pct(g.util[idx], 0)}`}</span>)}
                </span></h3>
                <table className="table">
                  <thead><tr><th>{t('Программа')}</th><th>{t('Процессор')}</th><th>{t('Память')}</th><th>{t('Сеть')}</th></tr></thead>
                  <tbody>
                    {snapshot.map(s => (
                      <tr key={s.name} onClick={() => onApp(s.name)}>
                        <td><span className="name">{appLabel(s.name)}</span></td>
                        <td style={{ color: COLOR.cpu }}>{pct(s.cpu)}</td>
                        <td>{bytes(s.mem)}</td>
                        <td className="dim">{s.net ? bytes(s.net) : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <div style={{ marginTop: 12 }}>
                  {why?.at === picked && why.data
                    ? <WhyCard why={why.data} onApp={onApp} />
                    : <button className="btn" disabled={why?.at === picked} onClick={explain}>{why?.at === picked ? t('Разбираю…') : t('Почему тормозило?')}</button>}
                </div>
              </>
            )}
          </div>
        </section>
      </div>
    </>
  );
}
