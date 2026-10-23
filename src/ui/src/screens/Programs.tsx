import { useMemo, useState } from 'react';
import { api, type ProgramRow } from '../api';
import { useLoad, useSort } from '../hooks';
import { ago, bytes, dayTime } from '../format';
import { t } from '../i18n';

type Filter = 'unused' | 'all' | 'runtime';
type Key = 'name' | 'lastUsed' | 'installed' | 'size';

const DAY = 86400;
const UNUSED_DAYS = 60;

const FILTERS: { key: Filter; label: string }[] = [
  { key: 'unused', label: t('Не запускались {0}+ дней', UNUSED_DAYS) },
  { key: 'all', label: t('Все программы') },
  { key: 'runtime', label: t('Библиотеки и драйверы') },
];

const SOURCE: Record<string, string> = {
  running: t('запущена сейчас'),
  prefetch: t('по данным Windows'),
  history: t('по истории SysWatch'),
};

function isUnused(p: ProgramRow, now: number) {
  return p.kind === 'app' && (p.lastUsed == null || now - p.lastUsed > UNUSED_DAYS * DAY);
}

export function Programs() {
  const [refresh, setRefresh] = useState(0);
  const list = useLoad(() => api.programs(refresh > 0), [refresh]);
  const [filter, setFilter] = useState<Filter>('unused');
  const [q, setQ] = useState('');
  const [confirm, setConfirm] = useState<string | null>(null);
  const [started, setStarted] = useState<Set<string>>(new Set());
  const { sort, thProps } = useSort<Key>('size');

  const now = Math.floor(Date.now() / 1000);
  const items = list.data?.items ?? [];

  const rows = useMemo(() => {
    const needle = q.toLowerCase();
    const filtered = items.filter(p =>
      (filter === 'all' ? p.kind !== 'runtime' : filter === 'runtime' ? p.kind === 'runtime' : isUnused(p, now)) &&
      (!needle || p.name.toLowerCase().includes(needle) || (p.publisher ?? '').toLowerCase().includes(needle)));
    return sort(filtered, (p, k) => k === 'name' ? p.name : k === 'lastUsed' ? (p.lastUsed ?? 0) : k === 'installed' ? (p.installed ?? 0) : p.size);
  }, [items, filter, q, sort, now]);

  const unused = items.filter(p => isUnused(p, now));
  const unusedSize = unused.reduce((s, p) => s + p.size, 0);

  const uninstall = async (p: ProgramRow) => {
    setConfirm(null);
    try {
      if (await api.uninstall(p.id)) setStarted(s => new Set(s).add(p.id));
    } catch { /* сообщение ниже не покажем — строка останется как была */ }
  };

  return (
    <>
      <div className="head">
        <div>
          <h1>{t('Программы')}</h1>
          <p>{t('Что установлено и когда каждая программа последний раз запускалась. Давно не используемые — кандидаты на удаление.')}</p>
        </div>
        <div className="head-tools">
          <input className="search" placeholder={t('Название или издатель')} value={q} onChange={e => setQ(e.target.value)} />
          <button className="btn" onClick={() => setRefresh(r => r + 1)}>{t('Обновить')}</button>
        </div>
      </div>

      {list.error && <div className="error">{t('Не удалось получить список программ: {0}', list.error)}</div>}
      {list.data && !list.data.prefetchAvailable && (
        <div className="note">{t('Prefetch в Windows выключен, поэтому последний запуск известен только за дни работы SysWatch. Через неделю картина станет точнее.')}</div>
      )}

      {list.data && (
        <div className="facts">
          <div className="fact"><b>{items.filter(p => p.kind !== 'runtime').length}</b><span>{t('программ установлено')}</span></div>
          <div className="fact"><b style={{ color: unused.length ? 'var(--cpu)' : undefined }}>{unused.length}</b><span>{t('не запускались {0}+ дней', UNUSED_DAYS)}</span></div>
          <div className="fact"><b>{bytes(unusedSize)}</b><span>{t('занимают неиспользуемые')}</span></div>
          <div className="fact"><b>{ago(list.data.scannedAt)}</b><span>{t('список обновлён')}</span></div>
        </div>
      )}

      <div className="filters" style={{ marginBottom: 12 }}>
        <div className="seg" role="group" aria-label={t('Фильтр')}>
          {FILTERS.map(f => <button key={f.key} aria-pressed={filter === f.key} onClick={() => setFilter(f.key)}>{f.label}</button>)}
        </div>
      </div>

      <div className="panel scroll" style={{ maxHeight: 'calc(100vh - 400px)', minHeight: 300 }}>
        {!rows.length ? <div className="empty">{list.loading ? t('Собираю список программ…') : t('Под этот фильтр ничего нет.')}</div> : (
          <table className="table">
            <thead>
              <tr>
                <th {...thProps('name')}>{t('Программа')}</th>
                <th {...thProps('lastUsed')} style={{ textAlign: 'left' }}>{t('Последний запуск')}</th>
                <th {...thProps('installed')}>{t('Установлена')}</th>
                <th {...thProps('size')}>{t('Размер')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.map(p => (
                <tr key={p.id} style={{ cursor: 'default' }}>
                  <td>
                    <div className="name" title={p.location ?? ''}>{p.name}</div>
                    <div className="dim" style={{ fontSize: 12 }}>{[p.publisher, p.version].filter(Boolean).join(', ')}</div>
                  </td>
                  <td style={{ textAlign: 'left' }}>
                    {p.lastUsed ? (
                      <>
                        <span style={{ color: isUnused(p, now) ? 'var(--cpu)' : undefined }}>{p.lastUsedBy === 'running' ? t('сейчас') : ago(p.lastUsed)}</span>
                        <div className="dim" style={{ fontSize: 12 }}>{SOURCE[p.lastUsedBy ?? ''] ?? ''}</div>
                      </>
                    ) : (
                      <span className="dim">{p.kind === 'app' ? t('запуск не замечен') : t('не запускается сама')}</span>
                    )}
                  </td>
                  <td className="dim">{p.installed ? dayTime(p.installed).split(',')[0] : '—'}</td>
                  <td>{p.size ? bytes(p.size) : '—'}</td>
                  <td style={{ width: 230 }}>
                    {started.has(p.id) ? (
                      <span className="dim">{t('открылся деинсталлятор')}</span>
                    ) : confirm === p.id ? (
                      <span className="confirm">
                        <button className="btn danger" onClick={() => uninstall(p)}>{t('Удалить')}</button>
                        <button className="btn" onClick={() => setConfirm(null)}>{t('Отмена')}</button>
                      </span>
                    ) : p.canUninstall && p.kind !== 'runtime' ? (
                      <button className="link" onClick={() => setConfirm(p.id)}>{t('Удалить…')}</button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <p className="dim" style={{ marginTop: 10, fontSize: 13, color: 'var(--muted)' }}>
        {t('«Удалить» запускает штатный деинсталлятор программы — дальше всё подтверждается в его окне, как при удалении через «Параметры».')}
      </p>
    </>
  );
}
