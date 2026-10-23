import { useEffect, useMemo, useState } from 'react';
import { api, type CpuInfo, type StartupItem } from '../api';
import { useLoad } from '../hooks';
import { COLOR, bytes, dayTime } from '../format';
import { Plot } from '../components/Plot';
import { CrashView } from '../components/Crashes';
import { locale, t } from '../i18n';

type Tab = 'startup' | 'disks' | 'battery' | 'wakes' | 'cpu' | 'crashes';

const TABS: { key: Tab; label: string }[] = [
  { key: 'startup', label: t('Автозагрузка') },
  { key: 'disks', label: t('Здоровье дисков') },
  { key: 'battery', label: t('Батарея') },
  { key: 'wakes', label: t('Пробуждения') },
  { key: 'cpu', label: t('Процессор') },
  { key: 'crashes', label: t('Сбои') },
];

export function System({ onApp }: { onApp: (name: string) => void }) {
  const [tab, setTab] = useState<Tab>('startup');
  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Система')}</h1>
          <p>{t('Что запускается вместе с Windows, в каком состоянии диски и батарея, кто будит компьютер и не сбрасывает ли процессор частоту.')}</p>
        </div>
      </div>
      <div className="seg" role="tablist" style={{ marginBottom: 18 }}>
        {TABS.map(x => <button key={x.key} role="tab" aria-pressed={tab === x.key} onClick={() => setTab(x.key)}>{x.label}</button>)}
      </div>
      {tab === 'startup' && <StartupTab onApp={onApp} />}
      {tab === 'disks' && <DisksTab />}
      {tab === 'battery' && <BatteryTab />}
      {tab === 'wakes' && <WakesTab />}
      {tab === 'cpu' && <CpuTab />}
      {tab === 'crashes' && <CrashesTab onApp={onApp} />}
    </>
  );
}

// ── Автозагрузка ──────────────────────────────────────────

function SigTag({ s }: { s: StartupItem }) {
  if (s.signature === 'signed') return <span className="tag good" title={t('Подписано: {0}', s.signer)}>{s.signer}</span>;
  if (s.signature === 'system') return <span className="tag good">Windows</span>;
  if (s.signature === 'unsigned') return <span className="tag bad">{t('без подписи')}</span>;
  return <span className="tag">{t('файл не найден')}</span>;
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
          <div className="fact"><b>{on}</b><span>{t('запускается вместе с Windows')}</span></div>
          <div className="fact"><b>{items.length - on}</b><span>{t('выключено')}</span></div>
          <div className="fact"><b style={{ color: items.some(i => i.enabled && i.signature === 'unsigned') ? 'var(--warn)' : undefined }}>
            {items.filter(i => i.enabled && i.signature === 'unsigned').length}</b><span>{t('включённых без цифровой подписи')}</span></div>
          <div className="fact"><b>{items.filter(i => i.signature === 'unknown').length}</b><span>{t('указывают на несуществующий файл')}</span></div>
        </div>
      )}
      {list.error && <div className="error">{t('Не удалось прочитать автозагрузку: {0}', list.error)}</div>}
      <div className="panel">
        {!items.length ? <div className="empty">{list.loading ? t('Загружаю…') : t('Автозагрузка пуста.')}</div> : (
          <table className="table">
            <thead><tr><th>{t('Программа')}</th><th style={{ textAlign: 'left' }}>{t('Издатель')}</th><th style={{ textAlign: 'left' }}>{t('Где записано')}</th><th>{t('Запуск')}</th></tr></thead>
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
                    <label className="switch" title={i.enabled ? t('Выключить запуск вместе с Windows') : t('Включить запуск вместе с Windows')}>
                      <input type="checkbox" checked={i.enabled} disabled={busy === i.id} onChange={() => toggle(i)} />
                      <span>{i.enabled ? t('включён') : t('выключен')}</span>
                    </label>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <p className="hint-line">{t('Выключение работает так же, как в Диспетчере задач: запись остаётся, Windows просто перестаёт её запускать. Включить обратно можно здесь или в Диспетчере. Службы и задачи планировщика — в разделе «Аномалии», правило «Новое в автозагрузке».')}</p>
    </>
  );
}

// ── Диски ─────────────────────────────────────────────────

const HEALTH: Record<string, [string, string]> = {
  healthy: [t('исправен'), 'good'], warning: [t('есть предупреждения'), 'warn'], unhealthy: [t('неисправен'), 'bad'], unknown: [t('неизвестно'), ''],
};

function DisksTab() {
  const list = useLoad(() => api.disksHealth(), []);
  return (
    <>
      {list.error && <div className="error">{t('Не удалось получить данные дисков: {0}', list.error)}</div>}
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
                <div><dt>{t('Ресурс')}</dt><dd style={{ color: life != null && life < 20 ? 'var(--warn)' : undefined }}>{life != null ? `${life}%` : '—'}</dd></div>
                <div><dt>{t('Температура')}</dt><dd style={{ color: d.temperature != null && d.temperature >= 60 ? 'var(--warn)' : undefined }}>{d.temperature != null ? `${d.temperature} °C` : '—'}</dd></div>
                <div><dt>{t('Максимум была')}</dt><dd>{d.temperatureMax ? `${d.temperatureMax} °C` : '—'}</dd></div>
                <div><dt>{t('Наработка')}</dt><dd>{d.powerOnHours != null ? t('{0} ч', d.powerOnHours.toLocaleString(locale())) : '—'}</dd></div>
                <div><dt>{t('Ошибки чтения')}</dt><dd style={{ color: d.readErrors ? 'var(--warn)' : undefined }}>{d.readErrors ?? '—'}</dd></div>
                <div><dt>{t('Ошибки записи')}</dt><dd style={{ color: d.writeErrors ? 'var(--warn)' : undefined }}>{d.writeErrors ?? '—'}</dd></div>
              </dl>
              {life != null && <div className="mini-bar"><i className={life < 20 ? 'full' : ''} style={{ width: `${life}%`, background: life < 20 ? undefined : 'var(--ok)' }} /></div>}
            </div>
          );
        })}
      </div>
      {list.data && !list.data.length && <div className="panel empty">{t('Windows не сообщила ни об одном физическом диске.')}</div>}
      <p className="hint-line">{t('«Ресурс» — сколько осталось от расчётного износа SSD по данным самого диска. Прочерк значит, что диск или его драйвер этот показатель не сообщает (часто у USB-дисков).')}</p>
    </>
  );
}

// ── Батарея ───────────────────────────────────────────────

function BatteryTab() {
  const b = useLoad(() => api.battery(), [], 10_000);
  const d = b.data;
  if (b.error) return <div className="error">{t('Не удалось получить данные батареи: {0}', b.error)}</div>;
  if (!d) return <div className="panel empty">{t('Загружаю…')}</div>;
  if (!d.present) return <div className="panel empty">{t('Батарея не найдена — похоже, это настольный компьютер.')}</div>;
  const health = d.wearPct != null ? 100 - d.wearPct : null;
  const watts = (mw: number | null) => (mw != null ? t('{0} Вт', (mw / 1000).toLocaleString(locale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 })) : '—');
  const wh = (mwh: number) => t('{0} Вт·ч', (mwh / 1000).toLocaleString(locale(), { minimumFractionDigits: 1, maximumFractionDigits: 1 }));
  return (
    <>
      <div className="facts">
        <div className="fact"><b style={{ color: COLOR.ok }}>{d.charge}%</b><span>{d.onAc ? t('заряд, от сети') : t('заряд, от батареи')}</span></div>
        <div className="fact"><b>{d.minutesLeft ? t('{0} ч {1} мин', Math.floor(d.minutesLeft / 60), d.minutesLeft % 60) : '—'}</b><span>{t('осталось работы по оценке Windows')}</span></div>
        <div className="fact"><b style={{ color: health != null && health < 70 ? 'var(--warn)' : undefined }}>{health != null ? `${health}%` : '—'}</b><span>{t('ёмкость от заводской')}</span></div>
        <div className="fact"><b>{d.cycles ?? '—'}</b><span>{t('циклов заряда')}</span></div>
      </div>
      <div className="panel hw-card" style={{ maxWidth: 640 }}>
        <dl className="hw-stats">
          <div><dt>{t('Заводская ёмкость')}</dt><dd>{d.designCapacity ? wh(d.designCapacity) : '—'}</dd></div>
          <div><dt>{t('Сейчас вмещает')}</dt><dd>{d.fullCapacity ? wh(d.fullCapacity) : '—'}</dd></div>
          <div><dt>{t('Расход сейчас')}</dt><dd>{watts(d.dischargeRate)}</dd></div>
          <div><dt>{t('Зарядка сейчас')}</dt><dd>{watts(d.chargeRate)}</dd></div>
        </dl>
      </div>
      <p className="hint-line">{t('Какие программы сильнее всего садят батарею, видно на экране «Сейчас» и в «Таймлайне»: на ноутбуке расход почти полностью определяется нагрузкой на процессор.')}</p>
    </>
  );
}

// ── Пробуждения ───────────────────────────────────────────

function WakesTab() {
  const w = useLoad(() => api.wakes(), []);
  const d = w.data;
  return (
    <>
      {w.error && <div className="error">{t('Не удалось прочитать журнал: {0}', w.error)}</div>}
      <div className="grid-2">
        <section className="block">
          <h2>{t('Последние пробуждения')} <small>{t('из журнала Windows')}</small></h2>
          <div className="panel">
            {!d ? <div className="empty">{t('Загружаю…')}</div> : !d.events.length ? <div className="empty">{t('Записей о выходе из сна нет.')}</div> : d.events.map((e, i) => (
              <div key={i} className="ev" style={{ gridTemplateColumns: '130px 1fr 150px' }}>
                <time>{dayTime(e.ts)}</time>
                <span title={e.source}>{e.source}</span>
                <span className="dim">{e.sourceType ?? ''}{e.sleepTs ? t(', спал {0} мин', Math.max(1, Math.round((e.ts - e.sleepTs) / 60))) : ''}</span>
              </div>
            ))}
          </div>
        </section>
        <section className="block">
          <h2>{t('Кто может разбудить')}</h2>
          <div className="panel" style={{ padding: '12px 16px' }}>
            <h3 className="sub-h">{t('Устройства')}</h3>
            {d?.armedDevices.length ? <ul className="plain">{d.armedDevices.map(x => <li key={x}>{x}</li>)}</ul> : <div className="dim">—</div>}
            <h3 className="sub-h">{t('Таймеры пробуждения')}</h3>
            {d?.timers.length ? <ul className="plain">{d.timers.map(x => <li key={x}>{x}</li>)}</ul> : <div className="dim">—</div>}
          </div>
          <p className="hint-line">{t('Мышь, клавиатура и сетевая карта в списке устройств — это нормально. Если компьютер просыпается ночью сам, ищи причину в таймерах: обычно это обновления или обслуживание Windows.')}</p>
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
      const now = Math.floor(Date.now() / 1000);
      setHist(h => ({ ts: [...h.ts, now].slice(-180), mhz: [...h.mhz, c.currentMhz].slice(-180), limit: [...h.limit, c.limitPct].slice(-180) }));
    }).catch(e => alive && setErr(String(e?.message ?? e)));
    tick();
    const id = setInterval(tick, 2000);
    return () => { alive = false; clearInterval(id); };
  }, []);

  const series = useMemo(() => [{ label: t('Частота'), color: COLOR.cpu, values: hist.mhz, fill: true }], [hist]);
  const ghz = (m: number, d = 2) => t('{0} ГГц', (m / 1000).toLocaleString(locale(), { minimumFractionDigits: d, maximumFractionDigits: d }));

  return (
    <>
      {err && <div className="error">{t('Не удалось получить данные процессора: {0}', err)}</div>}
      {cpu && (
        <div className="facts">
          <div className="fact"><b style={{ color: COLOR.cpu }}>{ghz(cpu.currentMhz)}</b><span>{t('частота сейчас (базовая {0})', ghz(cpu.baseMhz))}</span></div>
          <div className="fact"><b style={{ color: cpu.throttled ? 'var(--warn)' : COLOR.ok }}>{cpu.throttled ? t('ограничен до {0}%', Math.round(cpu.limitPct)) : t('без ограничений')}</b><span>{t('троттлинг: перегрев или режим питания')}</span></div>
          <div className="fact"><b>{cpu.thermal.length ? `${Math.max(...cpu.thermal.map(z => z.celsius)).toFixed(0)} °C` : '—'}</b><span>{cpu.thermal.length ? t('тепловая зона ACPI') : t('датчик температуры недоступен')}</span></div>
          <div className="fact"><b>{cpu.cores}/{cpu.threads}</b><span>{t('ядер и потоков')}</span></div>
        </div>
      )}
      <section className="block">
        <h2>{t('Частота за последние минуты')} <small>{cpu?.name}</small></h2>
        <div className="panel chart-box">
          {hist.ts.length > 1 ? <Plot ts={hist.ts} series={series} height={200} yFormat={v => ghz(v, 1)} /> : <div className="empty">{t('Собираю данные…')}</div>}
        </div>
      </section>
      <p className="hint-line">{t('Если частота падает ниже базовой под нагрузкой, а ограничение меньше 100%, процессор сбрасывает её из-за нагрева или энергосбережения. Температура ядер точнее у самого производителя (например, Armoury Crate на ASUS): Windows без отдельного драйвера видит только тепловые зоны платы.')}</p>
    </>
  );
}

// ── Сбои ──────────────────────────────────────────────────

const CRASH_DAYS = [7, 30, 90];

function CrashesTab({ onApp }: { onApp: (name: string) => void }) {
  const [days, setDays] = useState(30);
  const data = useLoad(() => api.crashes(days), [days]);
  const c = data.data?.counts ?? {};
  return (
    <>
      <div className="filters" style={{ marginBottom: 14 }}>
        <div className="seg" role="group" aria-label={t('Период')}>
          {CRASH_DAYS.map(d => <button key={d} aria-pressed={days === d} onClick={() => setDays(d)}>{t('{0} дней', d)}</button>)}
        </div>
        {data.loading && <span className="dim" style={{ fontSize: 13 }}>{t('Читаю журналы…')}</span>}
      </div>
      {data.error && <div className="error">{t('Не удалось прочитать журналы Windows: {0}', data.error)}</div>}
      {data.data && (
        <div className="facts" style={{ marginBottom: 16 }}>
          <div className="fact"><b style={{ color: c.bsod ? 'var(--warn)' : 'var(--ok)' }}>{c.bsod ?? 0}</b><span>{t('синих экранов')}</span></div>
          <div className="fact"><b style={{ color: c.power ? 'var(--warn)' : 'var(--ok)' }}>{c.power ?? 0}</b><span>{t('внезапных выключений')}</span></div>
          <div className="fact"><b style={{ color: c.gpu ? 'var(--cpu)' : 'var(--ok)' }}>{c.gpu ?? 0}</b><span>{t('сбоев видеодрайвера')}</span></div>
          <div className="fact"><b>{(c.app ?? 0) + (c.hang ?? 0)}</b><span>{t('падений и зависаний программ')}</span></div>
        </div>
      )}
      <CrashView data={data.data} onApp={name => onApp(name.split('\\').pop() ?? name)} />
      <p className="hint-line">{t('Источник — журналы «Система» и «Приложение» Windows. Ошибки диска и аппаратные ошибки (WHEA) стоит воспринимать всерьёз: они бывают предвестниками отказа.')}</p>
    </>
  );
}
