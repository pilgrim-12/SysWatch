import { useCallback, useEffect, useRef, useState } from 'react';

export interface Loaded<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  reload: () => void;
}

/** Загружает данные при смене deps; при refreshMs > 0 — периодически обновляет. */
export function useLoad<T>(fn: () => Promise<T>, deps: unknown[], refreshMs = 0): Loaded<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const fnRef = useRef(fn);
  fnRef.current = fn;

  const run = useCallback(() => {
    let alive = true;
    fnRef.current()
      .then(d => { if (alive) { setData(d); setError(null); } })
      .catch(e => { if (alive) setError(String(e?.message ?? e)); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, []);

  useEffect(() => {
    setLoading(true);
    const cancel = run();
    if (!refreshMs) return cancel;
    const id = setInterval(run, refreshMs);
    return () => { cancel(); clearInterval(id); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return { data, error, loading, reload: run };
}

export type SortDir = 'asc' | 'desc';

export function useSort<K extends string>(initial: K) {
  const [key, setKey] = useState<K>(initial);
  const [dir, setDir] = useState<SortDir>('desc');
  const toggle = (k: K) => {
    if (k === key) setDir(d => (d === 'desc' ? 'asc' : 'desc'));
    else { setKey(k); setDir('desc'); }
  };
  const sort = <T,>(rows: T[], get: (r: T, k: K) => number | string) =>
    [...rows].sort((a, b) => {
      const va = get(a, key), vb = get(b, key);
      const c = typeof va === 'string' ? va.localeCompare(vb as string) : (va as number) - (vb as number);
      return dir === 'desc' ? -c : c;
    });
  const thProps = (k: K) => ({
    className: 'sortable',
    onClick: () => toggle(k),
    'aria-sort': (k === key ? (dir === 'desc' ? 'descending' : 'ascending') : undefined) as 'descending' | 'ascending' | undefined,
  });
  return { key, dir, sort, thProps };
}
