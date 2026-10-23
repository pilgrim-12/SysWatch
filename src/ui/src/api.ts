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

export interface AgentParts { kernel: number; dns: number; sampler: number; writer: number; other: number }

export interface Overview {
  apps: AppTotals[];
  agentCpu: number[];   // по минутам, для полоски «стоимость мониторинга»
  minutes: number[];
  agentParts: AgentParts | null;
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

export interface DriveView {
  root: string; label: string; format: string; kind: 'fixed' | 'removable';
  total: number; free: number; isSystem: boolean; lastScan: number | null;
}

export interface OpProgress {
  phase: 'start' | 'disk' | 'cleanup' | 'clean';
  bytes: number; target: number; files: number; dirs: number;
  started: number; current: string | null; step: number; steps: number;
}

export interface DiskState {
  status: 'idle' | 'scanning' | 'cleaning';
  progress: string | null;
  busyRoot: string | null;
  op: OpProgress | null;
  drives: DriveView[];
  root: string;
  snapshot: { id: number; ts: number; scanned: number; files: number; seconds: number; shadow: number | null; hidden: number } | null;
  compareTs: number | null;
  cleanup: { scannedAt: number; categories: CleanupCategory[] } | null;
  lastClean: { ts: number; freed: number; deleted: number; recycled: number; skipped: number } | null;
  error: string | null;
}

export interface TreeChild { name: string; isDir: boolean; size: number }
export interface TreeItem { path: string; name: string; isDir: boolean; size: number; files: number; delta: number | null; children: TreeChild[]; other: number }
export interface DiskTree { path: string; size: number; delta: number | null; items: TreeItem[]; other: number }
export interface Growth { since: number | null; items: { path: string; size: number; delta: number; isNew: boolean }[]; driveDelta: number }

// ── Соединения, программы, действия ──

export interface ConnRow {
  key: string; proto: 'TCP' | 'UDP'; dir: 'out' | 'in' | 'listen'; pid: number; app: string; path: string | null;
  local: string; localPort: number; remote: string; remotePort: number; domain: string | null;
  state: string; firstSeen: number; closedAt: number | null;
  exposed: boolean;          // порт доступен из сети, а не только с этого компьютера
  country: string | null;    // ISO-код страны, 'LAN' — локальная сеть
}
export interface ConnSnapshot {
  ts: number; rows: ConnRow[]; out: number; in: number; listen: number; exposed: number;
  geoAvailable: boolean; geoStatus: string | null;
}
export interface BlockedApp { path: string; since: number }

export interface ProgramRow {
  id: string; name: string; publisher: string | null; version: string | null; installed: number | null;
  size: number; lastUsed: number | null; lastUsedBy: 'prefetch' | 'history' | 'running' | null;
  location: string | null; kind: 'app' | 'runtime' | 'unknown'; canUninstall: boolean;
}
export interface ProgramList { scannedAt: number; prefetchAvailable: boolean; items: ProgramRow[] }
export interface ActionResult { ok: boolean; message: string }

// ── Система ──

export interface StartupItem {
  id: string; name: string; where: string; command: string; path: string | null;
  enabled: boolean; signature: 'signed' | 'unsigned' | 'system' | 'unknown'; signer: string | null; canToggle: boolean;
}
export interface DiskHealth {
  name: string; media: string; bus: string; size: number; health: 'healthy' | 'warning' | 'unhealthy' | 'unknown';
  wear: number | null; temperature: number | null; temperatureMax: number | null; powerOnHours: number | null;
  readErrors: number | null; writeErrors: number | null; startStops: number | null;
}
export interface BatteryInfo {
  present: boolean; charge: number | null; onAc: boolean | null; designCapacity: number | null; fullCapacity: number | null;
  cycles: number | null; dischargeRate: number | null; chargeRate: number | null; wearPct: number | null; minutesLeft: number | null;
}
export interface WakeInfo { events: { ts: number; sleepTs: number | null; source: string; sourceType: string | null }[]; armedDevices: string[]; timers: string[] }
export interface CpuInfo {
  name: string; cores: number; threads: number; baseMhz: number; currentMhz: number; performancePct: number;
  limitPct: number; throttled: boolean; thermal: { name: string; celsius: number }[];
}

async function postJson<T>(path: string): Promise<T> {
  const r = await fetch(path, { method: 'POST' });
  if (!r.ok) throw new Error(`${r.status} ${await r.text()}`);
  return r.json() as Promise<T>;
}

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


// ── Отчёты и «Почему тормозило?» ──

export interface Culprit {
  app: string;
  cpu: number;        // % всех ядер в среднем за отрезок
  cpuUsual: number;   // то же за полчаса до
  mem: number;        // пик частной памяти, байты
  memGrowth: number;
  io: number;         // байт/с чтения и записи
  hint: string | null;
}

export interface StartedProc { app: string; ts: number; hint: string | null }

export interface Why {
  from: number;
  to: number;
  hasData: boolean;
  avgCpu: number;
  peakCpu: number;
  avgMem: number;
  peakMem: number;
  kinds: ('cpu' | 'mem' | 'io')[];
  verdict: string;
  details: string[];
  culprits: Culprit[];
  started: StartedProc[];
}

export interface ReportApp { name: string; cpu: number; mem: number; io: number; net: number }

export interface Report {
  from: number;
  to: number;
  hours: number;
  avgCpu: number;
  peakCpu: number;
  avgMem: number;
  peakMem: number;
  netTx: number;
  netRx: number;
  ioRead: number;
  ioWrite: number;
  points: { ts: number; cpu: number | null; mem: number | null }[];
  topCpu: ReportApp[];
  topMem: ReportApp[];
  topIo: ReportApp[];
  topNet: ReportApp[];
  episodes: Why[];
  slowMinutes: number;
  anomalies: { rule: string; count: number }[];
  notable: Anomaly[];
  newPrograms: string[];
  systemDrive: { root: string; total: number; free: number } | null;
  diskGrowth: { since: number | null; driveDelta: number; items: { path: string; size: number; delta: number; isNew: boolean }[] } | null;
}


// ── Среда разработки ──

export interface DevItem {
  name: string;
  version: string;
  path: string | null;
  size: number | null;
  support: 'eol' | 'soon' | 'ok' | 'lts' | null;
  until: number | null;
  note: string | null;
  uninstallId: string | null;
}
export interface DevGroup { id: string; title: string; hint: string | null; items: DevItem[] }
export interface DevService { name: string; display: string; state: string; startMode: string; pid: number | null; memory: number | null; path: string | null }
export interface CommandResolution { command: string; path: string | null; version: string | null; shadowed: string[]; note: string | null }
export interface PathEntry { dir: string; scope: 'system' | 'user'; exists: boolean; duplicate: boolean }
export interface DevEnvironment {
  scannedAt: number; seconds: number; groups: DevGroup[]; services: DevService[];
  commands: CommandResolution[]; path: PathEntry[];
}
export interface DevState { busy: boolean; data: DevEnvironment | null }


// ── История браузеров ──

export interface BrowserProfile { id: string; browser: string; profile: string; ok: boolean; error: string | null; visits: number; searches: number }
export interface HistoryItem { ts: number; source: string; kind: 'visit' | 'search'; url: string; title: string | null; domain: string; term: string | null; engine: string | null }
export interface DomainStat { domain: string; visits: number; last: number; sources: string[] }
export interface HistoryResult {
  from: number; profiles: BrowserProfile[]; items: HistoryItem[]; total: number;
  searches: number; visits: number; domains: DomainStat[];
}

export const api = {
  history: (days: number, q: string, source: string, kind: string) =>
    get<HistoryResult>(`/api/history?days=${days}&q=${encodeURIComponent(q)}&source=${encodeURIComponent(source)}&kind=${kind}`),
  dev: (refresh = false) => get<DevState>(`/api/dev${refresh ? '?refresh=1' : ''}`),
  devService: (name: string, action: 'start' | 'stop' | 'auto' | 'manual' | 'disabled') => postJson<ActionResult>(`/api/dev/service?name=${encodeURIComponent(name)}&action=${action}`),
  devOpen: (path: string) => postJson<ActionResult>(`/api/dev/open?path=${encodeURIComponent(path)}`),
  report: (days: number) => get<Report>(`/api/report?days=${days}`),
  reportExport: (days: number) => postJson<{ ok: boolean; path: string }>(`/api/report/export?days=${days}`),
  why: (from: number, to: number) => get<Why>(`/api/why?from=${from}&to=${to}`),
  slowdowns: (range: number) => get<Why[]>(`/api/slowdowns?range=${range}`),
  live: () => get<Live>('/api/live'),
  overview: (range: number) => get<Overview>(`/api/overview?range=${range}`),
  timeline: (range: number) => get<Timeline>(`/api/timeline?range=${range}`),
  network: (range: number) => get<Network>(`/api/network?range=${range}`),
  app: (name: string, range: number) => get<AppDetail>(`/api/app?name=${encodeURIComponent(name)}&range=${range}`),
  events: (range: number) => get<SysEvent[]>(`/api/events?range=${range}`),
  anomalies: (range: number) => get<AnomalyList>(`/api/anomalies?range=${range}`),
  anomaliesSeen: () => post('/api/anomalies/seen'),
  connections: () => get<ConnSnapshot>('/api/connections'),
  startup: () => get<StartupItem[]>('/api/system/startup'),
  startupToggle: (id: string, enabled: boolean) => postJson<boolean>(`/api/system/startup/toggle?id=${encodeURIComponent(id)}&enabled=${enabled ? 1 : 0}`),
  disksHealth: () => get<DiskHealth[]>('/api/system/disks'),
  battery: () => get<BatteryInfo>('/api/system/battery'),
  wakes: () => get<WakeInfo>('/api/system/wakes'),
  cpu: () => get<CpuInfo>('/api/system/cpu'),
  geoDownload: () => postJson<boolean>('/api/geo/download'),
  blocked: () => get<BlockedApp[]>('/api/firewall'),
  isBlocked: (path: string) => get<boolean>(`/api/firewall/status?path=${encodeURIComponent(path)}`),
  block: (path: string) => postJson<ActionResult>(`/api/firewall/block?path=${encodeURIComponent(path)}`),
  unblock: (path: string) => postJson<ActionResult>(`/api/firewall/unblock?path=${encodeURIComponent(path)}`),
  programs: (refresh = false) => get<ProgramList>(`/api/programs${refresh ? '?refresh=1' : ''}`),
  uninstall: (id: string) => postJson<boolean>(`/api/programs/uninstall?id=${encodeURIComponent(id)}`),
  kill: (name: string) => postJson<ActionResult>(`/api/process/kill?name=${encodeURIComponent(name)}`),
  priority: (name: string, level: 'low' | 'normal') =>
    postJson<ActionResult>(`/api/process/priority?name=${encodeURIComponent(name)}&level=${level}`),
  reveal: (path: string) => postJson<ActionResult>(`/api/process/reveal?path=${encodeURIComponent(path)}`),
  disk: (root: string | null) => get<DiskState>(`/api/disk${root ? `?root=${encodeURIComponent(root)}` : ''}`),
  diskTree: (root: string, path: string | null) =>
    get<DiskTree | null>(`/api/disk/tree?root=${encodeURIComponent(root)}${path ? `&path=${encodeURIComponent(path)}` : ''}`),
  diskGrowth: (root: string) => get<Growth>(`/api/disk/growth?root=${encodeURIComponent(root)}`),
  diskScan: (root: string) => post(`/api/disk/scan?root=${encodeURIComponent(root)}`),
  diskCleanupScan: () => post('/api/disk/cleanup-scan'),
  diskCancel: () => post('/api/disk/cancel'),
  diskClean: (categories: string[], exclude: string[]) => post('/api/disk/clean', { categories, exclude }),
  anomalyIgnore: (rule: string, app: string) =>
    post(`/api/anomalies/ignore?rule=${encodeURIComponent(rule)}&app=${encodeURIComponent(app)}`),
};
