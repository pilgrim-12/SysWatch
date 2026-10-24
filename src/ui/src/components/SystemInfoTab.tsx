import { useState } from 'react';
import { api, type SysSection } from '../api';
import { useLoad } from '../hooks';
import { ago, dayTime } from '../format';
import { t } from '../i18n';

/** Секции, которые лучше смотрятся во всю ширину (широкие таблицы). */
const WIDE = new Set(['gpu', 'displays', 'storage', 'network', 'memory', 'ports', 'account']);

function Section({ s }: { s: SysSection }) {
  return (
    <section className={`panel si-card${WIDE.has(s.id) ? ' wide' : ''}`}>
      <h3>{s.title}</h3>
      {s.items.length > 0 && (
        <dl className="si-kv">
          {s.items.map(i => (
            <div key={i.label}>
              <dt>{i.label}</dt>
              <dd className={i.tone ? `tone-${i.tone}` : undefined}>{i.value}</dd>
            </div>
          ))}
        </dl>
      )}
      {s.tables.map((tb, k) => tb.rows.length === 0 ? null : (
        <div key={k} className="si-table">
          {tb.title && <h4>{tb.title}</h4>}
          <div className="scroll">
            <table className="table">
              <thead><tr>{tb.columns.map((c, i) => <th key={i}>{c}</th>)}</tr></thead>
              <tbody>{tb.rows.map((r, i) => <tr key={i} style={{ cursor: 'default' }}>{r.map((v, j) => <td key={j} className={j === 0 ? 'name' : 'dim'}>{v}</td>)}</tr>)}</tbody>
            </table>
          </div>
        </div>
      ))}
      {s.note && <p className="hint-line" style={{ margin: '8px 0 0' }}>{s.note}</p>}
    </section>
  );
}

function RestorePoints() {
  const rp = useLoad(() => api.restorePoints(), []);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<{ ok: boolean; message: string } | null>(null);
  const create = async () => {
    setBusy(true); setMsg(null);
    try { setMsg(await api.createRestorePoint()); rp.reload(); }
    catch (e) { setMsg({ ok: false, message: String((e as Error).message ?? e) }); }
    finally { setBusy(false); }
  };
  const d = rp.data;
  return (
    <section className="panel si-card wide">
      <h3>{t('Точки восстановления')}
        <button className="btn" style={{ marginLeft: 'auto' }} disabled={busy} onClick={create}>{busy ? t('Создаю… это до минуты') : t('Создать точку восстановления')}</button>
      </h3>
      <p className="hint-line" style={{ margin: '0 0 10px' }}>{t('Снимок системных файлов и настроек. Если после установки драйвера или программы Windows начнёт сбоить — к нему можно откатиться: Пуск → «Восстановление» → «Запуск восстановления системы». Ваши документы не затрагиваются.')}</p>
      {msg && <div className={`note ${msg.ok ? 'ok' : ''}`}>{msg.message}</div>}
      {!d ? <div className="empty">{t('Загружаю…')}</div> : d.points.length === 0 ? <div className="empty">{d.note ?? t('Точек нет.')}</div> : (
        <table className="table">
          <thead><tr><th>{t('Когда')}</th><th>{t('Описание')}</th><th>{t('Причина')}</th></tr></thead>
          <tbody>{d.points.map(p => (
            <tr key={p.sequence} style={{ cursor: 'default' }}>
              <td>{dayTime(p.ts)} <span className="dim">· {ago(p.ts)}</span></td>
              <td className="name">{p.description}</td>
              <td className="dim">{p.type}</td>
            </tr>
          ))}</tbody>
        </table>
      )}
    </section>
  );
}

export function SystemInfoTab() {
  const [refresh, setRefresh] = useState(0);
  const info = useLoad(() => api.systemInfo(refresh > 0), [refresh]);
  const [copied, setCopied] = useState(false);
  const d = info.data;

  // Всё одним текстом — удобно отправить в поддержку или сохранить.
  const copy = async () => {
    if (!d) return;
    const text = d.sections.map(s => [
      `== ${s.title} ==`,
      ...s.items.map(i => `${i.label}: ${i.value}`),
      ...s.tables.flatMap(tb => [...(tb.title ? [`-- ${tb.title}`] : []), ...tb.rows.map(r => r.filter(Boolean).join(' | '))]),
    ].join('\n')).join('\n\n');
    try { await navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 2000); } catch { /* буфер недоступен */ }
  };

  return (
    <>
      <div className="report-toolbar">
        <span className="dim" style={{ color: 'var(--muted)', fontSize: 13 }}>
          {info.loading && !d ? t('Собираю сведения о компьютере… до 10 секунд.') : d ? t('Собрано {0}', ago(d.ts)) : ''}
        </span>
        <span className="grow" />
        <button className="btn" disabled={!d} onClick={copy}>{copied ? t('Скопировано') : t('Скопировать всё')}</button>
        <button className="btn" disabled={info.loading} onClick={() => setRefresh(r => r + 1)}>{t('Обновить')}</button>
      </div>
      {info.error && <div className="error">{info.error}</div>}
      {d && (
        <div className="si-grid">
          {d.sections.map(s => <Section key={s.id} s={s} />)}
          <RestorePoints />
        </div>
      )}
    </>
  );
}
