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
};
