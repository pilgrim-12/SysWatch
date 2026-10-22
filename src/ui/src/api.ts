// Контракт между окном (C#) и интерфейсом. Все ts — Unix-секунды UTC.
// Байты — в байтах, CPU — в процентах от всех ядер.

export interface LiveApp {
  name: string;
  count: number;      // сколько процессов с этим именем
  cpu: number;        // %
  mem: number;        // частная память, байты
  ioRead: number;     // байт/с
  ioWrite: number;    // байт/с
}

export interface Live {
  ts: number;
  cpu: number;
  memUsed: number;
  memTotal: number;
  diskRead: number;   // байт/с, вся система
  diskWrite: number;
  netRx: number;      // байт/с, все интерфейсы
  netTx: number;
  apps: LiveApp[];
  agent: AgentStatus;
}

export interface AgentStatus {
  running: boolean;   // писал ли агент данные за последние 3 минуты
  lastWrite: number | null;
  cpu: number | null; // % CPU самого агента в последнем интервале
  ram: number | null;
  eventsLost: number | null;
}

export interface AppTotals {
  name: string;
  cpuPct: number;     // средний % CPU за период
  memMax: number;
  ioRead: number;     // байт за период
  ioWrite: number;
  netTx: number;
  netRx: number;
  domains: number;
}

export interface TimelineApp {
  name: string;
  cpu: number[];      // % по минутам, длина = minutes.length
  mem: number[];
  net: number[];      // байт за минуту (tx+rx)
}

export interface Timeline {
  minutes: number[];
  system: { cpu: number[]; mem: number[] };
  apps: TimelineApp[];
}

export interface Flow {
  app: string;
  domain: string;     // домен или IP, если домен неизвестен
  known: boolean;     // true — домен из DNS
  tx: number;
  rx: number;
  connects: number;
  ports: number[];
}

export interface DomainRow {
  domain: string;
  tx: number;
  rx: number;
  apps: string[];
  firstSeen: number;
  newToday: boolean;
}

export interface Network {
  flows: Flow[];
  domains: DomainRow[];
  totals: { tx: number; rx: number; domains: number; apps: number };
}

export interface ProcRow {
  pid: number;
  path: string | null;
  cmdline: string | null;
  parent: string | null;
  started: number;
  exited: number | null;
}

export interface AppDetail {
  name: string;
  path: string | null;
  signer: string | null;      // кем подписан exe
  signature: 'signed' | 'unsigned' | 'system' | 'unknown';
  series: { ts: number[]; cpu: number[]; mem: number[]; netTx: number[]; netRx: number[]; io: number[] };
  processes: ProcRow[];
  domains: { domain: string; tx: number; rx: number; connects: number }[];
}

export interface SysEvent {
  ts: number;
  kind: 'start' | 'stop' | 'dns';
  app: string;
  pid: number;
  detail: string | null;
}

export interface Overview {
  apps: AppTotals[];
  agentCpu: number[];   // по минутам, для полоски «стоимость мониторинга»
  minutes: number[];
}

export type Severity = 'high' | 'medium' | 'low';

export interface Anomaly {
  id: number;
  ts: number;
  rule: 'unsigned_net' | 'temp_exec' | 'new_program' | 'new_domain' | 'autostart' | 'cpu' | 'memory' | string;
  severity: Severity;
  app: string;
  title: string;
  detail: string;
  seen: boolean;
}

export interface AnomalyList {
  items: Anomaly[];
  unseen: number;
  learning: boolean;   // нет истории прошлых дней — правила «новое» пока молчат
}

// ── Диск ──

export interface CleanupItem { path: string; size: number; files: number; modified: number }

export interface CleanupCategory {
  id: string;
  group: 'system' | 'dev' | 'review';
  title: string;
  description: string;
  mode: 'delete' | 'recycle' | 'none';
  defaultOn: boolean;
  itemsSelectable: boolean;
  hint: string | null;
  size: number;
  files: number;
  items: CleanupItem[];
}

export interface DiskState {
  status: 'idle' | 'scanning' | 'cleaning';
  progress: string | null;
  drive: { root: string; total: number; free: number };
  snapshot: { id: number; ts: number; scanned: number; files: number; seconds: number; shadow: number | null; hidden: number } | null;
  compareTs: number | null;
  cleanup: { scannedAt: number; categories: CleanupCategory[] } | null;
  lastClean: { ts: number; freed: number; deleted: number; recycled: number; skipped: number } | null;
  error: string | null;
}

export interface TreeItem { path: string; name: string; isDir: boolean; size: number; files: number; delta: number | null }
export interface DiskTree { path: string; size: number; delta: number | null; items: TreeItem[]; other: number }
export interface Growth { since: number | null; items: { path: string; size: number; delta: number; isNew: boolean }[]; driveDelta: number }

async function post(path: string, body?: unknown): Promise<void> {
  const r = await fetch(path, {
    method: 'POST',
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) throw new Error(`${r.status} ${await r.text()}`);
}

async function get<T>(path: string): Promise<T> {
  const r = await fetch(path);
  if (!r.ok) throw new Error(`${r.status} ${await r.text()}`);
  return r.json() as Promise<T>;
}

export const api = {
  live: () => get<Live>('/api/live'),
  overview: (range: number) => get<Overview>(`/api/overview?range=${range}`),
  timeline: (range: number) => get<Timeline>(`/api/timeline?range=${range}`),
  network: (range: number) => get<Network>(`/api/network?range=${range}`),
  app: (name: string, range: number) => get<AppDetail>(`/api/app?name=${encodeURIComponent(name)}&range=${range}`),
  events: (range: number) => get<SysEvent[]>(`/api/events?range=${range}`),
  anomalies: (range: number) => get<AnomalyList>(`/api/anomalies?range=${range}`),
  anomaliesSeen: () => post('/api/anomalies/seen'),
  disk: () => get<DiskState>('/api/disk'),
  diskTree: (path: string | null) => get<DiskTree | null>(`/api/disk/tree${path ? `?path=${encodeURIComponent(path)}` : ''}`),
  diskGrowth: () => get<Growth>('/api/disk/growth'),
  diskScan: () => post('/api/disk/scan'),
  diskCleanupScan: () => post('/api/disk/cleanup-scan'),
  diskCancel: () => post('/api/disk/cancel'),
  diskClean: (categories: string[], exclude: string[]) => post('/api/disk/clean', { categories, exclude }),
  anomalyIgnore: (rule: string, app: string) =>
    post(`/api/anomalies/ignore?rule=${encodeURIComponent(rule)}&app=${encodeURIComponent(app)}`),
};
