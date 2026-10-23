import { useEffect, useMemo, useState } from 'react';
import { api, type CpuInfo, type StartupItem } from '../api';
import { useLoad } from '../hooks';
import { COLOR, bytes, dayTime } from '../format';
import { Plot } from '../components/Plot';

type Tab = 'startup' | 'disks' | 'battery' | 'wakes' | 'cpu';

const TABS: { key: Tab; label: string }[] = [
  { key: 'startup', label: 'Автозагрузка' },
  { key: 'disks', label: 'Здоровье дисков' },
  { key: 'battery', label: 'Батарея' },
  { key: 'wakes', label: 'Пробуждения' },
  { key: 'cpu', label: 'Процессор' },
];

export function System({ onApp }: { onApp: (name: string) => void }) {
  const [tab, setTab] = useState<Tab>('startup');
  return (
    <>
      <div className="head">
        <div>
          <h1>Система</h1>
          <p>Что запускается вместе с Windows, в каком состоянии диски и батарея, кто будит компьютер и не сбрасывает ли процессор частоту.</p>
        </div>
      </div>
      <div className="seg" role="tablist" style={{ marginBottom: 18 }}>
        {TABS.map(t => <button key={t.key} role="tab" aria-pressed={tab === t.key} onClick={() => setTab(t.key)}>{t.label}</button>)}
      </div>
      {tab === 'startup' && <StartupTab onApp={onApp} />}
      {tab === 'disks' && <DisksTab />}
      {tab === 'battery' && <BatteryTab />}
      {tab === 'wakes' && <WakesTab />}
      {tab === 'cpu' && <CpuTab />}
    </>
  );
}

// ── Автозагрузка ──────────────────────────────────────────

function SigTag({ s }: { s: StartupItem }) {
  if (s.signature === 'signed') return <span className="tag good" title={`Подписано: ${s.signer}`}>{s.signer}</span>;
  if (s.signature === 'system') return <span className="tag good">Windows</span>;
  if (s.signature === 'unsigned') return <span className="tag bad">без подписи</span>;
  return <span className="tag">файл не найден</span>;
}

function StartupTab({ onApp }: { onApp: (name: string) => void }) {
  const list = useLoad(() => api.startup(), []);
  const [busy, setBusy] = useState<string | null>(null);
  const items = list.data ?? [];
  const on = items.filter(i => i.enabled).length;

  const toggle = async (i: StartupItem) => {
    setBusy(i.id);
    try { await api.startupToggle(i.id, !i.enabled); list.reload(); } finally { setBusy(null); }
  };

  return (
    <>
      {list.data && (
        <div className="facts">
          <div className="fact"><b>{on}</b><span>запускается вместе с Windows</span></div>
          <div className="fact"><b>{items.length - on}</b><span>выключено</span></div>
          <div className="fact"><b style={{ color: items.some(i => i.enabled && i.signature === 'unsigned') ? 'var(--warn)' : undefined }}>
            {items.filter(i => i.enabled && i.signature === 'unsigned').length}</b><span>включённых без цифровой подписи</span></div>
          <div className="fact"><b>{items.filter(i => i.signature === 'unknown').length}</b><span>указывают на несуществующий файл</span></div>
        </div>
      )}
      {list.error && <div className="error">Не удалось прочитать автозагрузку: {list.error}</div>}
      <div className="panel">
        {!items.length ? <div className="empty">{list.loading ? 'Загружаю…' : 'Автозагрузка пуста.'}</div> : (
          <table className="table">
            <thead><tr><th>Программа</th><th style={{ textAlign: 'left' }}>Издатель</th><th style={{ textAlign: 'left' }}>Где записано</th><th>Запуск</th></tr></thead>
            <tbody>
              {items.map(i => (
                <tr key={i.id} style={{ cursor: 'default', opacity: i.enabled ? 1 : 0.6 }}>
                  <td>
                    <div className="name">
                      {i.path ? <button className="link" style={{ fontSize: 14 }} onClick={() => onApp(i.path!.split('\\').pop()!)}>{i.name}</button> : i.name}
                    </div>
                    <div className="cmd" title={i.command}>{i.command}</div>
                  </td>
                  <td style={{ textAlign: 'left', verticalAlign: 'top' }}><SigTag s={i} /></td>
                  <td className="dim" style={{ textAlign: 'left', verticalAlign: 'top' }}>{i.where}</td>
                  <td style={{ verticalAlign: 'top' }}>
                    <label className="switch" title={i.enabled ? 'Выключить запуск вместе с Windows' : 'Включить запуск вместе с Windows'}>
                      <input type="checkbox" checked={i.enabled} disabled={busy === i.id} onChange={() => toggle(i)} />
                      <span>{i.enabled ? 'включён' : 'выключен'}</span>
                    </label>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <p className="hint-line">Выключение работает так же, как в Диспетчере задач: запись остаётся, Windows просто перестаёт её запускать. Включить обратно можно здесь или в Диспетчере. Службы и задачи планировщика — в разделе «Аномалии», правило «Новое в автозагрузке».</p>
    </>
  );
}

// ── Диски ─────────────────────────────────────────────────

const HEALTH: Record<string, [string, string]> = {
  healthy: ['исправен', 'good'], warning: ['есть предупреждения', 'warn'], unhealthy: ['неисправен', 'bad'], unknown: ['неизвестно', ''],
};

function DisksTab() {
  const list = useLoad(() => api.disksHealth(), []);
  return (
    <>
      {list.error && <div className="error">Не удалось получить данные дисков: {list.error}</div>}
      <div className="hw-grid">
        {(list.data ?? []).map(d => {
          const [label, cls] = HEALTH[d.health] ?? HEALTH.unknown;
          const life = d.wear != null ? 100 - d.wear : null;
          return (
            <div key={d.name} className="panel hw-card">
              <div className="hw-title">
                <b>{d.name}</b>
                <span className={`tag ${cls}`}>{label}</span>
              </div>
              <div className="dim">{[d.media, d.bus, bytes(d.size)].filter(Boolean).join(', ')}</div>
              <dl className="hw-stats">
                <div><dt>Ресурс</dt><dd style={{ color: life != null && life < 20 ? 'var(--warn)' : undefined }}>{life != null ? `${life}%` : '—'}</dd></div>
                <div><dt>Температура</dt><dd style={{ color: d.temperature != null && d.temperature >= 60 ? 'var(--warn)' : undefined }}>{d.temperature != null ? `${d.temperature} °C` : '—'}</dd></div>
                <div><dt>Максимум была</dt><dd>{d.temperatureMax ? `${d.temperatureMax} °C` : '—'}</dd></div>
                <div><dt>Наработка</dt><dd>{d.powerOnHours != null ? `${d.powerOnHours.toLocaleString('ru-RU')} ч` : '—'}</dd></div>
                <div><dt>Ошибки чтения</dt><dd style={{ color: d.readErrors ? 'var(--warn)' : undefined }}>{d.readErrors ?? '—'}</dd></div>
                <div><dt>Ошибки записи</dt><dd style={{ color: d.writeErrors ? 'var(--warn)' : undefined }}>{d.writeErrors ?? '—'}</dd></div>
              </dl>
              {life != null && <div className="mini-bar"><i className={life < 20 ? 'full' : ''} style={{ width: `${life}%`, background: life < 20 ? undefined : 'var(--ok)' }} /></div>}
            </div>
          );
        })}
      </div>
      {list.data && !list.data.length && <div className="panel empty">Windows не сообщила ни об одном физическом диске.</div>}
      <p className="hint-line">«Ресурс» — сколько осталось от расчётного износа SSD по данным самого диска. Прочерк значит, что диск или его драйвер этот показатель не сообщает (часто у USB-дисков).</p>
    </>
  );
}

// ── Батарея ───────────────────────────────────────────────

function BatteryTab() {
  const b = useLoad(() => api.battery(), [], 10_000);
  const d = b.data;
  if (b.error) return <div className="error">Не удалось получить данные батареи: {b.error}</div>;
  if (!d) return <div className="panel empty">Загружаю…</div>;
  if (!d.present) return <div className="panel empty">Батарея не найдена — похоже, это настольный компьютер.</div>;
  const health = d.wearPct != null ? 100 - d.wearPct : null;
  const watts = (mw: number | null) => (mw != null ? `${(mw / 1000).toFixed(1).replace('.', ',')} Вт` : '—');
  return (
    <>
      <div className="facts">
        <div className="fact"><b style={{ color: COLOR.ok }}>{d.charge}%</b><span>{d.onAc ? 'заряд, от сети' : 'заряд, от батареи'}</span></div>
        <div className="fact"><b>{d.minutesLeft ? `${Math.floor(d.minutesLeft / 60)} ч ${d.minutesLeft % 60} мин` : '—'}</b><span>осталось работы по оценке Windows</span></div>
        <div className="fact"><b style={{ color: health != null && health < 70 ? 'var(--warn)' : undefined }}>{health != null ? `${health}%` : '—'}</b><span>ёмкость от заводской</span></div>
        <div className="fact"><b>{d.cycles ?? '—'}</b><span>циклов заряда</span></div>
      </div>
      <div className="panel hw-card" style={{ maxWidth: 640 }}>
        <dl className="hw-stats">
          <div><dt>Заводская ёмкость</dt><dd>{d.designCapacity ? `${(d.designCapacity / 1000).toFixed(1)} Вт·ч` : '—'}</dd></div>
          <div><dt>Сейчас вмещает</dt><dd>{d.fullCapacity ? `${(d.fullCapacity / 1000).toFixed(1)} Вт·ч` : '—'}</dd></div>
          <div><dt>Расход сейчас</dt><dd>{watts(d.dischargeRate)}</dd></div>
          <div><dt>Зарядка сейчас</dt><dd>{watts(d.chargeRate)}</dd></div>
        </dl>
      </div>
      <p className="hint-line">Какие программы сильнее всего садят батарею, видно на экране «Сейчас» и в «Таймлайне»: на ноутбуке расход почти полностью определяется нагрузкой на процессор.</p>
    </>
  );
}

// ── Пробуждения ───────────────────────────────────────────

function WakesTab() {
  const w = useLoad(() => api.wakes(), []);
  const d = w.data;
  return (
    <>
      {w.error && <div className="error">Не удалось прочитать журнал: {w.error}</div>}
      <div className="grid-2">
        <section className="block">
          <h2>Последние пробуждения <small>из журнала Windows</small></h2>
          <div className="panel">
            {!d ? <div className="empty">Загружаю…</div> : !d.events.length ? <div className="empty">Записей о выходе из сна нет.</div> : d.events.map((e, i) => (
              <div key={i} className="ev" style={{ gridTemplateColumns: '130px 1fr 150px' }}>
                <time>{dayTime(e.ts)}</time>
                <span title={e.source}>{e.source}</span>
                <span className="dim">{e.sourceType ?? ''}{e.sleepTs ? `, спал ${Math.max(1, Math.round((e.ts - e.sleepTs) / 60))} мин` : ''}</span>
              </div>
            ))}
          </div>
        </section>
        <section className="block">
          <h2>Кто может разбудить</h2>
          <div className="panel" style={{ padding: '12px 16px' }}>
            <h3 className="sub-h">Устройства</h3>
            {d?.armedDevices.length ? <ul className="plain">{d.armedDevices.map(x => <li key={x}>{x}</li>)}</ul> : <div className="dim">—</div>}
            <h3 className="sub-h">Таймеры пробуждения</h3>
            {d?.timers.length ? <ul className="plain">{d.timers.map(x => <li key={x}>{x}</li>)}</ul> : <div className="dim">—</div>}
          </div>
          <p className="hint-line">Мышь, клавиатура и сетевая карта в списке устройств — это нормально. Если компьютер просыпается ночью сам, ищи причину в таймерах: обычно это обновления или обслуживание Windows.</p>
        </section>
      </div>
    </>
  );
}

// ── Процессор ─────────────────────────────────────────────

function CpuTab() {
  const [hist, setHist] = useState<{ ts: number[]; mhz: number[]; limit: number[] }>({ ts: [], mhz: [], limit: [] });
  const [cpu, setCpu] = useState<CpuInfo | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    const tick = () => api.cpu().then(c => {
      if (!alive) return;
      setCpu(c);
      const t = Math.floor(Date.now() / 1000);
      setHist(h => ({ ts: [...h.ts, t].slice(-180), mhz: [...h.mhz, c.currentMhz].slice(-180), limit: [...h.limit, c.limitPct].slice(-180) }));
    }).catch(e => alive && setErr(String(e?.message ?? e)));
    tick();
    const id = setInterval(tick, 2000);
    return () => { alive = false; clearInterval(id); };
  }, []);

  const series = useMemo(() => [{ label: 'Частота', color: COLOR.cpu, values: hist.mhz, fill: true }], [hist]);
  const ghz = (m: number) => `${(m / 1000).toFixed(2).replace('.', ',')} ГГц`;

  return (
    <>
      {err && <div className="error">Не удалось получить данные процессора: {err}</div>}
      {cpu && (
        <div className="facts">
          <div className="fact"><b style={{ color: COLOR.cpu }}>{ghz(cpu.currentMhz)}</b><span>частота сейчас (базовая {ghz(cpu.baseMhz)})</span></div>
          <div className="fact"><b style={{ color: cpu.throttled ? 'var(--warn)' : COLOR.ok }}>{cpu.throttled ? `ограничен до ${Math.round(cpu.limitPct)}%` : 'без ограничений'}</b><span>троттлинг: перегрев или режим питания</span></div>
          <div className="fact"><b>{cpu.thermal.length ? `${Math.max(...cpu.thermal.map(t => t.celsius)).toFixed(0)} °C` : '—'}</b><span>{cpu.thermal.length ? 'тепловая зона ACPI' : 'датчик температуры недоступен'}</span></div>
          <div className="fact"><b>{cpu.cores}/{cpu.threads}</b><span>ядер и потоков</span></div>
        </div>
      )}
      <section className="block">
        <h2>Частота за последние минуты <small>{cpu?.name}</small></h2>
        <div className="panel chart-box">
          {hist.ts.length > 1 ? <Plot ts={hist.ts} series={series} height={200} yFormat={v => `${(v / 1000).toFixed(1)} ГГц`} /> : <div className="empty">Собираю данные…</div>}
        </div>
      </section>
      <p className="hint-line">Если частота падает ниже базовой под нагрузкой, а ограничение меньше 100%, процессор сбрасывает её из-за нагрева или энергосбережения. Температура ядер точнее у самого производителя (например, Armoury Crate на ASUS): Windows без отдельного драйвера видит только тепловые зоны платы.</p>
    </>
  );
}
