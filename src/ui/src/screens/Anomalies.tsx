import { useEffect, useMemo, useState } from 'react';
import { api, type Anomaly, type Severity } from '../api';
import { useLoad } from '../hooks';
import { RANGES, appLabel, dayTime } from '../format';

const SEVERITY: Record<Severity, { label: string; cls: string }> = {
  high: { label: 'Важно', cls: 'bad' },
  medium: { label: 'Внимание', cls: 'warn' },
  low: { label: 'К сведению', cls: '' },
};

/** Одна строка объяснения, зачем правило вообще срабатывает. */
const WHY: Record<string, string> = {
  unsigned_net: 'Подпись подтверждает, кто выпустил программу. Без неё нельзя проверить происхождение файла.',
  temp_exec: 'Из временных папок и «Загрузок» обычно стартуют установщики и вложения из писем.',
  new_program: 'Эта программа не запускалась ни разу за всю историю наблюдений.',
  new_domain: 'Программа раньше никогда не обращалась к этому адресу.',
  autostart: 'Теперь это будет запускаться само при каждом старте Windows.',
  cpu: 'Нагрузка держится несколько минут подряд, это не короткий всплеск.',
  memory: 'Память растёт час без снижения. Так обычно выглядит утечка.',
};

const FILTERS: { key: Severity | 'all'; label: string }[] = [
  { key: 'all', label: 'Все' },
  { key: 'high', label: 'Важные' },
  { key: 'medium', label: 'Внимание' },
  { key: 'low', label: 'К сведению' },
];

export function Anomalies({ onApp, onSeen }: { onApp: (name: string) => void; onSeen: () => void }) {
  // У аномалий свой период: по умолчанию сутки, иначе за час их почти всегда ноль.
  const [range, setRange] = useState(1440);
  const list = useLoad(() => api.anomalies(range), [range], 30_000);
  const [filter, setFilter] = useState<(typeof FILTERS)[number]['key']>('all');
  const [hidden, setHidden] = useState<Set<string>>(new Set());

  // Открыл раздел — значит, увидел: снимаем отметку «новое» у всех.
  useEffect(() => {
    if (list.data && list.data.unseen > 0) api.anomaliesSeen().then(onSeen).catch(() => {});
  }, [list.data, onSeen]);

  const items = useMemo(() => (list.data?.items ?? [])
    .filter(a => !hidden.has(`${a.rule}|${a.app}`))
    .filter(a => filter === 'all' || a.severity === filter), [list.data, filter, hidden]);

  const ignore = async (a: Anomaly) => {
    setHidden(h => new Set(h).add(`${a.rule}|${a.app}`));
    try { await api.anomalyIgnore(a.rule, a.app); } catch { /* вернётся при следующей загрузке */ }
  };

  const count = (s: Severity) => (list.data?.items ?? []).filter(a => a.severity === s && !hidden.has(`${a.rule}|${a.app}`)).length;

  return (
    <>
      <div className="head">
        <div>
          <h1>Аномалии</h1>
          <p>Необычное в поведении программ: проверка идёт раз в минуту, о важном приходит уведомление.</p>
        </div>
        <div className="head-tools">
          <div className="seg" role="group" aria-label="Период">
            {RANGES.map(r => (
              <button key={r.minutes} aria-pressed={range === r.minutes} onClick={() => setRange(r.minutes)}>{r.label}</button>
            ))}
          </div>
        </div>
      </div>

      {list.data?.learning && (
        <div className="note">
          SysWatch пока изучает, что для этого компьютера нормально. Правила «новая программа» и «новый адрес»
          начнут работать завтра, когда появится история за прошлый день. Остальные правила уже работают.
        </div>
      )}

      {list.error && <div className="error">Не удалось загрузить аномалии: {list.error}</div>}

      <div className="filters" style={{ marginBottom: 12 }}>
        <div className="seg" role="group" aria-label="Важность">
          {FILTERS.map(f => (
            <button key={f.key} aria-pressed={filter === f.key} onClick={() => setFilter(f.key)}>
              {f.label}{f.key !== 'all' && list.data ? ` ${count(f.key)}` : ''}
            </button>
          ))}
        </div>
      </div>

      <div className="panel">
        {!items.length ? (
          <div className="empty">{list.loading ? 'Загружаю…' : 'За этот период ничего необычного не найдено.'}</div>
        ) : items.map(a => (
          <article key={a.id} className={`anomaly sev-${a.severity}`}>
            <div className="anomaly-mark" aria-hidden />
            <div className="anomaly-body">
              <div className="anomaly-top">
                <span className={`tag ${SEVERITY[a.severity].cls}`}>{SEVERITY[a.severity].label}</span>
                <h3>{a.title}</h3>
                {!a.seen && <span className="tag new">новое</span>}
                <time>{dayTime(a.ts)}</time>
              </div>
              <p className="anomaly-detail">{a.detail}</p>
              {WHY[a.rule] && <p className="anomaly-why">{WHY[a.rule]}</p>}
              <div className="anomaly-actions">
                <button className="link" onClick={() => onApp(a.app)}>Открыть {appLabel(a.app)}</button>
                <button className="link dim" onClick={() => ignore(a)}>Не предупреждать об этом для {appLabel(a.app)}</button>
              </div>
            </div>
          </article>
        ))}
      </div>
    </>
  );
}
