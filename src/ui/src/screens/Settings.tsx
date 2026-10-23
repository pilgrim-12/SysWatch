import { useEffect, useState } from 'react';
import { api, errorText, type AlertSettings, type SettingsView } from '../api';
import { bytes } from '../format';
import { locale, t } from '../i18n';

const RETENTION = [30, 90, 180, 365];

const date = (ts: number) => new Date(ts * 1000).toLocaleDateString(locale(), { day: 'numeric', month: 'long', year: 'numeric' });

export function Settings() {
  const [s, setS] = useState<SettingsView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  useEffect(() => { api.settings().then(setS).catch(e => setError(errorText(e))); }, []);

  const save = async (u: Parameters<typeof api.saveSettings>[0], what: string) => {
    setError(null); setSaved(null);
    try { setS(await api.saveSettings(u)); setSaved(what); }
    catch (e) { setError(errorText(e)); }
  };

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Настройки')}</h1>
          <p>{t('Сколько хранить историю и когда предупреждать. Настройки хранятся только на этом компьютере.')}</p>
        </div>
      </div>

      {error && <div className="error">{error}</div>}
      {saved && <div className="note ok" role="status">{t('Сохранено: {0}', saved)}</div>}
      {!s ? <div className="panel"><div className="empty">{t('Загрузка…')}</div></div> : (
        <>
          <Retention s={s} onSave={days => save({ retentionDays: days }, t('срок хранения'))} />
          <Alerts value={s.alerts} onSave={a => save({ alerts: a }, t('оповещения'))} />
        </>
      )}
    </>
  );
}

function Retention({ s, onSave }: { s: SettingsView; onSave: (days: number) => void }) {
  const st = s.storage;
  // Облегчённый день (без соединений) примерно вдвое меньше полного — грубая оценка для подсказки.
  const estimate = (days: number) => st.avgDayBytes * Math.min(days, 30) + st.avgDayBytes * 0.5 * Math.max(0, days - 30);
  return (
    <section className="block">
      <h2>{t('Хранение истории')}</h2>
      <div className="panel settings-panel">
        <div className="settings-row">
          <div>
            <b>{t('Сколько дней хранить')}</b>
            <p>{t('Сетевые подробности (соединения, DNS) хранятся 30 дней. Дальше остаются нагрузка, память и диск по программам — этого хватает для отчётов и графиков.')}</p>
          </div>
          <div className="seg" role="group" aria-label={t('Сколько дней хранить')}>
            {RETENTION.map(d => (
              <button key={d} aria-pressed={s.retentionDays === d} onClick={() => onSave(d)}>{t('{0} дн.', d)}</button>
            ))}
          </div>
        </div>
        <div className="settings-facts">
          <span>{t('Сейчас: {0} дн., {1}', st.days, bytes(st.bytes))}</span>
          {st.oldest != null && <span>{t('самый старый день — {0}', date(st.oldest))}</span>}
          {st.avgDayBytes > 0 && <span>{t('при {0} дн. займёт примерно {1}', s.retentionDays, bytes(estimate(s.retentionDays)))}</span>}
        </div>
        <p className="hint-line" style={{ margin: 0 }}>{t('Изменение срока агент применит в течение часа. Удаляются только дни старше срока.')}</p>
      </div>
    </section>
  );
}

function Num({ label, value, min, max, unit, onChange }: { label: string; value: number; min: number; max: number; unit: string; onChange: (v: number) => void }) {
  return (
    <label className="num-field">
      <span>{label}</span>
      <span className="num-input">
        <input type="number" min={min} max={max} value={value} onChange={e => onChange(Number(e.target.value))} />
        <i>{unit}</i>
      </span>
    </label>
  );
}

function Alerts({ value, onSave }: { value: AlertSettings; onSave: (a: AlertSettings) => void }) {
  const [a, setA] = useState(value);
  useEffect(() => setA(value), [value]);
  const set = (p: Partial<AlertSettings>) => setA(x => ({ ...x, ...p }));
  const changed = JSON.stringify(a) !== JSON.stringify(value);
  return (
    <section className="block">
      <h2>{t('Оповещения')}</h2>
      <div className="panel settings-panel">
        <div className="settings-row">
          <div>
            <b>{t('Уведомлять, когда компьютеру тяжело')}</b>
            <p>{t('Уведомление Windows из трея. Каждое — не чаще раза в час, о месте на диске — раз в полдня.')}</p>
          </div>
          <label className="check"><input type="checkbox" checked={a.enabled} onChange={e => set({ enabled: e.target.checked })} />{t('Включено')}</label>
        </div>
        <fieldset className="num-grid" disabled={!a.enabled}>
          <Num label={t('Процессор выше')} value={a.cpuPct} min={50} max={100} unit="%" onChange={v => set({ cpuPct: v })} />
          <Num label={t('Память выше')} value={a.memPct} min={50} max={100} unit="%" onChange={v => set({ memPct: v })} />
          <Num label={t('Дольше чем')} value={a.minutes} min={1} max={60} unit={t('мин')} onChange={v => set({ minutes: v })} />
          <Num label={t('Свободно на диске меньше')} value={a.diskFreePct} min={1} max={50} unit="%" onChange={v => set({ diskFreePct: v })} />
          <Num label={t('Видеокарта горячее')} value={a.gpuTemp} min={60} max={110} unit="°C" onChange={v => set({ gpuTemp: v })} />
        </fieldset>
        <div className="report-toolbar" style={{ margin: 0 }}>
          <button className="btn primary" disabled={!changed} onClick={() => onSave(a)}>{t('Сохранить')}</button>
          {changed && <button className="btn" onClick={() => setA(value)}>{t('Отменить')}</button>}
        </div>
      </div>
    </section>
  );
}
