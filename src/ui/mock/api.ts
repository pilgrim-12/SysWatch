// Мок-API для разработки интерфейса без Windows. Имена и порядки величин — с реальной машины.
import type { IncomingMessage, ServerResponse } from 'node:http';

const GB = 1024 ** 3, MB = 1024 ** 2, KB = 1024;
const now = () => Math.floor(Date.now() / 1000);

const APPS = [
  { name: 'msedge.exe', count: 15, cpu: 8, mem: 1.9 * GB, net: 2 * MB, io: 3 * MB },
  { name: 'chrome.exe', count: 38, cpu: 3, mem: 2.4 * GB, net: 400 * KB, io: 2 * MB },
  { name: 'svchost.exe', count: 106, cpu: 1.8, mem: 1.1 * GB, net: 60 * KB, io: 4 * MB },
  { name: 'sqlservr.exe', count: 1, cpu: 1.9, mem: 64 * MB, net: 0, io: 6 * MB },
  { name: 'dwm.exe', count: 1, cpu: 1.2, mem: 190 * MB, net: 0, io: 0 },
  { name: 'Telegram.exe', count: 1, cpu: 0.4, mem: 310 * MB, net: 30 * KB, io: 200 * KB },
  { name: 'claude.exe', count: 8, cpu: 0.6, mem: 420 * MB, net: 40 * KB, io: 300 * KB },
  { name: 'Code.exe', count: 12, cpu: 0.9, mem: 860 * MB, net: 10 * KB, io: 1 * MB },
  { name: 'MsMpEng.exe', count: 1, cpu: 0.7, mem: 240 * MB, net: 5 * KB, io: 9 * MB },
  { name: 'NVIDIA Share.exe', count: 3, cpu: 0.6, mem: 56 * MB, net: 0, io: 0 },
  { name: 'mbbService.exe', count: 1, cpu: 0.5, mem: 5 * MB, net: 0, io: 0 },
  { name: 'Viber.exe', count: 1, cpu: 0.2, mem: 180 * MB, net: 8 * KB, io: 50 * KB },
  { name: 'System', count: 1, cpu: 0.8, mem: 10 * MB, net: 0, io: 12 * MB },
  { name: 'SearchIndexer.exe', count: 1, cpu: 0.3, mem: 70 * MB, net: 0, io: 5 * MB },
  { name: 'AppleMobileDeviceService.exe', count: 1, cpu: 0.05, mem: 12 * MB, net: 1 * KB, io: 0 },
];

const DOMAINS: Record<string, string[]> = {
  'msedge.exe': ['www.youtube.com', 'rr3---sn-4g5e6nsz.googlevideo.com', 'i.ytimg.com', 'edge.microsoft.com', 'login.live.com'],
  'chrome.exe': ['github.com', 'api.github.com', 'claude.ai', 'fonts.gstatic.com', 'www.google.com', 'stackoverflow.com'],
  'svchost.exe': ['settings-win.data.microsoft.com', 'v10.events.data.microsoft.com', 'ctldl.windowsupdate.com', 'time.windows.com'],
  'Telegram.exe': ['149.154.167.51', 'venus.web.telegram.org'],
  'claude.exe': ['api.anthropic.com', 'claude.ai', 'statsig.anthropic.com'],
  'Code.exe': ['marketplace.visualstudio.com', 'update.code.visualstudio.com', 'mobile.events.data.microsoft.com'],
  'MsMpEng.exe': ['wdcp.microsoft.com', 'go.microsoft.com'],
  'Viber.exe': ['content.cdn.viber.com', 'dl-media.viber.com'],
  'AppleMobileDeviceService.exe': ['init.itunes.apple.com'],
};

// Детерминированный шум, чтобы картинка не прыгала между запросами.
function noise(seed: number) {
  const x = Math.sin(seed * 12.9898) * 43758.5453;
  return x - Math.floor(x);
}

function wave(i: number, t: number, base: number, k = 0.6) {
  const burst = noise(i * 7.1 + Math.floor(t / 300)) > 0.86 ? 3 + noise(t) * 4 : 1;
  return Math.max(0, base * (1 - k / 2 + k * noise(i * 31.7 + t / 60)) * burst);
}

function minutes(range: number) {
  const end = Math.floor(now() / 60) * 60;
  return Array.from({ length: range }, (_, i) => end - (range - 1 - i) * 60);
}

function live() {
  const t = now();
  const apps = APPS.map((a, i) => ({
    name: a.name,
    count: a.count,
    cpu: wave(i, t * 60, a.cpu, 0.9),
    mem: a.mem * (0.97 + 0.06 * noise(i + t / 30)),
    ioRead: wave(i + 50, t * 60, a.io * 0.6),
    ioWrite: wave(i + 90, t * 60, a.io * 0.3),
  })).sort((a, b) => b.cpu - a.cpu);
  const cpu = Math.min(100, 6 + apps.reduce((s, a) => s + a.cpu, 0) + 6 * noise(t));
  return {
    ts: t,
    cpu,
    memUsed: 26.1 * GB + 1.2 * GB * noise(t / 20),
    memTotal: 31.7 * GB,
    diskRead: 4 * MB * noise(t * 1.3) + (noise(t / 7) > 0.8 ? 60 * MB : 0),
    diskWrite: 2 * MB * noise(t * 1.7),
    netRx: 1.5 * MB * noise(t * 0.7) + (noise(t / 11) > 0.75 ? 18 * MB : 0),
    netTx: 120 * KB * noise(t * 0.9),
    apps,
    agent: { running: true, lastWrite: t - 20, cpu: 0.48, ram: 31 * MB, eventsLost: 0 },
  };
}

function timeline(range: number) {
  const mins = minutes(range);
  const apps = APPS.slice(0, 14).map((a, i) => ({
    name: a.name,
    cpu: mins.map(t => wave(i, t, a.cpu)),
    mem: mins.map(t => a.mem * (0.9 + 0.2 * noise(i + t / 900))),
    net: mins.map(t => wave(i + 20, t, a.net * 60)),
  }));
  return {
    minutes: mins,
    system: {
      cpu: mins.map((t, k) => Math.min(100, 5 + apps.reduce((s, a) => s + a.cpu[k], 0))),
      mem: mins.map(t => 72 + 10 * noise(t / 1800)),
    },
    apps,
  };
}

function overview(range: number) {
  const tl = timeline(range);
  const apps = APPS.map((a, i) => ({
    name: a.name,
    cpuPct: a.cpu * (0.8 + 0.4 * noise(i)),
    memMax: a.mem,
    ioRead: a.io * 60 * range * 0.6,
    ioWrite: a.io * 60 * range * 0.3,
    netTx: a.net * 60 * range * 0.08,
    netRx: a.net * 60 * range,
    domains: (DOMAINS[a.name] ?? []).length,
  }));
  return { apps, minutes: tl.minutes, agentCpu: tl.minutes.map(t => 0.35 + 0.3 * noise(t)) };
}

function network(range: number) {
  const flows = [] as any[];
  const t0 = now() - range * 60;
  const seen = new Map<string, any>();
  APPS.forEach((a, i) => {
    (DOMAINS[a.name] ?? []).forEach((d, j) => {
      const share = 1 / (j + 1.4);
      const rx = a.net * 60 * range * share;
      const known = !/^\d/.test(d);
      flows.push({ app: a.name, domain: d, known, rx, tx: rx * (0.03 + 0.1 * noise(i + j)), connects: 1 + Math.floor(40 * noise(i * j + 3) * share), ports: d.includes('time') ? [123] : [443] });
      const row = seen.get(d) ?? { domain: d, tx: 0, rx: 0, apps: [], firstSeen: t0 + Math.floor(noise(i + j) * range * 60), newToday: noise(i * 3 + j) > 0.8 };
      row.tx += flows.at(-1).tx; row.rx += rx; row.apps.push(a.name); seen.set(d, row);
    });
  });
  flows.sort((a, b) => b.rx + b.tx - a.rx - a.tx);
  const domains = [...seen.values()].sort((a, b) => b.rx + b.tx - a.rx - a.tx);
  return {
    flows,
    domains,
    totals: { tx: flows.reduce((s, f) => s + f.tx, 0), rx: flows.reduce((s, f) => s + f.rx, 0), domains: domains.length, apps: new Set(flows.map(f => f.app)).size },
  };
}

function app(name: string, range: number) {
  const i = Math.max(0, APPS.findIndex(a => a.name === name));
  const a = APPS[i];
  const mins = minutes(range);
  const sys = ['svchost.exe', 'dwm.exe', 'System', 'MsMpEng.exe', 'SearchIndexer.exe'].includes(a.name);
  const path = a.name === 'System' ? null
    : sys ? `C:\\Windows\\System32\\${a.name}`
    : a.name === 'msedge.exe' ? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe'
    : a.name === 'chrome.exe' ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'
    : `C:\\Users\\ANTRARES\\AppData\\Local\\Programs\\${a.name.replace('.exe', '')}\\${a.name}`;
  return {
    name: a.name,
    path,
    signer: sys ? 'Microsoft Windows' : a.name === 'mbbService.exe' ? null : a.name.startsWith('msedge') ? 'Microsoft Corporation' : a.name.startsWith('chrome') ? 'Google LLC' : 'Publisher Ltd',
    signature: sys ? 'system' : a.name === 'mbbService.exe' ? 'unsigned' : 'signed',
    series: {
      ts: mins,
      cpu: mins.map(t => wave(i, t, a.cpu)),
      mem: mins.map(t => a.mem * (0.85 + 0.25 * (t - mins[0]) / (range * 60) + 0.05 * noise(t))),
      netTx: mins.map(t => wave(i + 5, t, a.net * 6)),
      netRx: mins.map(t => wave(i + 9, t, a.net * 60)),
      io: mins.map(t => wave(i + 13, t, a.io * 60)),
    },
    processes: Array.from({ length: Math.min(a.count, 12) }, (_, k) => ({
      pid: 1000 + i * 97 + k * 13,
      path,
      cmdline: path ? `"${path}" ${k === 0 ? '' : `--type=renderer --renderer-client-id=${k + 4} --lang=ru`}`.trim() : null,
      parent: k === 0 ? 'explorer.exe' : a.name,
      started: now() - Math.floor(noise(i + k) * range * 60),
      exited: k % 5 === 4 ? now() - 120 : null,
    })),
    domains: (DOMAINS[a.name] ?? []).map((d, j) => ({ domain: d, rx: a.net * 60 * range / (j + 1.4), tx: a.net * 6 * range / (j + 1.4), connects: 3 + j * 4 })),
  };
}

function events(range: number) {
  const out = [] as any[];
  const t = now();
  for (let k = 0; k < 60; k++) {
    const a = APPS[Math.floor(noise(k * 3.3) * APPS.length)];
    const kind = noise(k) > 0.55 ? 'dns' : noise(k + 1) > 0.5 ? 'start' : 'stop';
    const doms = DOMAINS[a.name] ?? ['example.org'];
    out.push({
      ts: t - Math.floor((k / 60) * range * 60),
      kind,
      app: a.name,
      pid: 1000 + k * 17,
      detail: kind === 'dns' ? doms[k % doms.length] : kind === 'start' ? `"C:\\Program Files\\${a.name}"` : null,
    });
  }
  return out;
}

function anomalies() {
  const t = now();
  const items = [
    { rule: 'unsigned_net', severity: 'high', app: 'mbbService.exe', title: 'Программа без подписи выходит в сеть', detail: 'C:\\Program Files\\Mobile Broadband\\mbbService.exe подключалась к 185.199.108.153, telemetry.mbb-update.net. У файла нет цифровой подписи издателя.', ago: 240 },
    { rule: 'autostart', severity: 'high', app: 'updater.exe', title: 'Новое в автозагрузке', detail: 'Реестр HKCU\\…\\Run: QuickUpdater → "C:\\Users\\ANTRARES\\AppData\\Roaming\\QuickUpdater\\updater.exe" --silent', ago: 1800 },
    { rule: 'cpu', severity: 'medium', app: 'msedgewebview2.exe', title: 'Долгая высокая нагрузка', detail: 'msedgewebview2.exe держит процессор на 41% всех ядер уже 5 минут подряд', ago: 3600 },
    { rule: 'temp_exec', severity: 'medium', app: 'setup_x64.exe', title: 'Запуск из временной папки', detail: 'C:\\Users\\ANTRARES\\Downloads\\setup_x64.exe. Так запускаются установщики, но и вредоносные программы тоже.', ago: 7200 },
    { rule: 'memory', severity: 'medium', app: 'Code.exe', title: 'Память растёт', detail: 'Code.exe: 860 МБ → 2,3 ГБ за час без снижения. Похоже на утечку памяти.', ago: 9000 },
    { rule: 'new_domain', severity: 'low', app: 'Telegram.exe', title: 'Новый адрес у программы', detail: 'Telegram.exe впервые обратилась к cdn5.telesco.pe', ago: 15000 },
    { rule: 'new_program', severity: 'low', app: 'ffmpeg.exe', title: 'Новая программа', detail: 'Впервые запущена: C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe', ago: 30000 },
  ];
  return {
    items: items.map((a, i) => ({ id: i + 1, ts: t - a.ago, rule: a.rule, severity: a.severity, app: a.app, title: a.title, detail: a.detail, seen: i > 2 })),
    unseen: 3,
    learning: false,
  };
}

export function mockApi(req: IncomingMessage, res: ServerResponse, next: () => void) {
  if (!req.url?.startsWith('/api/')) return next();
  const u = new URL(req.url, 'http://x');
  const range = Number(u.searchParams.get('range') ?? 60);
  const body = (() => {
    switch (u.pathname) {
      case '/api/live': return live();
      case '/api/overview': return overview(range);
      case '/api/timeline': return timeline(range);
      case '/api/network': return network(range);
      case '/api/app': return app(u.searchParams.get('name') ?? '', range);
      case '/api/events': return events(range);
      case '/api/anomalies': return anomalies();
      case '/api/anomalies/seen': return true;
      case '/api/anomalies/ignore': return true;
      default: return null;
    }
  })();
  if (!body) { res.statusCode = 404; res.end('not found'); return; }
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify(body));
}
