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

/** Как на сервере: не больше 720 точек на период. */
function minutes(range: number) {
  const step = Math.max(1, Math.ceil(range / 720)) * 60;
  const n = Math.ceil((range * 60) / step);
  const end = Math.floor(now() / step) * step;
  return Array.from({ length: n }, (_, i) => end - (n - 1 - i) * step);
}

/** Агент в тестовых данных «работает» только последние сутки — проверяем, как окно показывает неполный период. */
const AGENT_SINCE = () => now() - 20 * 3600;

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
      cpu: mins.map((t, k) => t < AGENT_SINCE() ? null : Math.min(100, 5 + apps.reduce((s, a) => s + a.cpu[k], 0))),
      mem: mins.map(t => t < AGENT_SINCE() ? null : 72 + 10 * noise(t / 1800)),
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
  return { apps, minutes: tl.minutes, agentCpu: tl.minutes.map(t => 0.35 + 0.3 * noise(t)), agentParts: { kernel: 0.21, dns: 0.01, sampler: 0.09, writer: 0.03, other: 0.14 } };
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

function whyMock(from: number, to: number, kind: 'cpu' | 'mem' | 'none') {
  if (kind === 'none') return {
    from, to, hasData: true, avgCpu: 21, peakCpu: 34, avgMem: 71, peakMem: 73, kinds: [],
    verdict: 'Перегрузки не видно: процессор в среднем 21% (пик 34%), память 73%. Причина могла быть там, куда агент не смотрит: видеокарта, перегрев и сброс частоты, ожидание диска или сети, зависшее окно программы.',
    details: [], culprits: [{ app: 'chrome.exe', cpu: 9, cpuUsual: 7, mem: 3.1 * GB, memGrowth: 0, io: 0, hint: null }], started: [],
  };
  if (kind === 'mem') return {
    from, to, hasData: true, avgCpu: 38, peakCpu: 52, avgMem: 91, peakMem: 94, kinds: ['mem'],
    verdict: 'Не хватало ресурсов: память. Главный виновник — vmmem (виртуальная машина (WSL или Docker)).',
    details: [
      'Память была заполнена на 94%. В таком состоянии Windows выгружает данные программ на диск, и всё начинает отвечать с задержкой. Больше всего памяти держали: vmmem (виртуальная машина (WSL или Docker)) — 8,2 ГБ, chrome.exe — 4,6 ГБ, devenv.exe — 2,1 ГБ.',
      'vmmem вырос на 3,4 ГБ за полчаса до этого.',
    ],
    culprits: [
      { app: 'vmmem', cpu: 12, cpuUsual: 4, mem: 8.2 * GB, memGrowth: 3.4 * GB, io: 0, hint: 'виртуальная машина (WSL или Docker)' },
      { app: 'chrome.exe', cpu: 8, cpuUsual: 7, mem: 4.6 * GB, memGrowth: 0.2 * GB, io: 0, hint: null },
      { app: 'devenv.exe', cpu: 3, cpuUsual: 2, mem: 2.1 * GB, memGrowth: 0, io: 0, hint: null },
    ],
    started: [{ app: 'docker.exe', ts: from - 60, hint: null }],
  };
  return {
    from, to, hasData: true, avgCpu: 81, peakCpu: 96, avgMem: 78, peakMem: 81, kinds: ['cpu', 'io'],
    verdict: 'Не хватало ресурсов: процессор и диск. Главный виновник — MsMpEng.exe (проверка Защитником Windows).',
    details: [
      'Процессор был занят на 96% (в среднем 81%). Больше всего его загружали: MsMpEng.exe (проверка Защитником Windows) — 41%, TiWorker.exe (установка обновлений Windows) — 22%, chrome.exe — 9%.',
      'MsMpEng.exe обычно берёт около 1%, здесь — 41%: это всплеск, а не постоянный фон.',
      'Активно читали и писали данные: MsMpEng.exe (проверка Защитником Windows) — 84 МБ/с. Если диск жёсткий (HDD), такой поток сам по себе тормозит остальные программы.',
      'Незадолго до этого запустились: TiWorker.exe (установка обновлений Windows), MoUsoCoreWorker.exe (Центр обновления Windows).',
    ],
    culprits: [
      { app: 'MsMpEng.exe', cpu: 41, cpuUsual: 1, mem: 0.4 * GB, memGrowth: 0, io: 84 * MB, hint: 'проверка Защитником Windows' },
      { app: 'TiWorker.exe', cpu: 22, cpuUsual: 0, mem: 0.3 * GB, memGrowth: 0, io: 12 * MB, hint: 'установка обновлений Windows' },
      { app: 'chrome.exe', cpu: 9, cpuUsual: 7, mem: 3.4 * GB, memGrowth: 0, io: 0, hint: null },
    ],
    started: [
      { app: 'TiWorker.exe', ts: from - 120, hint: 'установка обновлений Windows' },
      { app: 'MoUsoCoreWorker.exe', ts: from - 100, hint: 'Центр обновления Windows' },
    ],
  };
}

function reportMock(days: number) {
  const t = now(), from = t - days * 86400, step = days > 2 ? 3600 : 600;
  const points = [];
  for (let x = from - from % step; x < t; x += step) {
    const h = new Date(x * 1000).getHours();
    const night = h < 8;
    points.push({ ts: x, cpu: night ? 4 + Math.random() * 3 : 18 + Math.random() * 22, mem: night ? 55 : 68 + Math.random() * 14 });
  }
  const app = (name: string, cpu: number, mem: number, io: number, net: number) => ({ name, cpu, mem, io, net });
  return {
    from, to: t, hours: days * 24 * 0.82, avgCpu: 17.4, peakCpu: 97, avgMem: 69, peakMem: 94,
    netTx: 3.2 * GB, netRx: 41.7 * GB, ioRead: 212 * GB, ioWrite: 96 * GB, points,
    topCpu: [app('chrome.exe', 6.1, 4.6 * GB, 22 * GB, 30 * GB), app('devenv.exe', 3.4, 2.1 * GB, 9 * GB, 0.2 * GB), app('MsMpEng.exe', 1.9, 0.4 * GB, 61 * GB, 0), app('dwm.exe', 1.2, 0.3 * GB, 0, 0), app('Telegram.exe', 0.6, 0.5 * GB, 1 * GB, 2.1 * GB)],
    topMem: [app('vmmem', 1.1, 8.2 * GB, 4 * GB, 1 * GB), app('chrome.exe', 6.1, 4.6 * GB, 22 * GB, 30 * GB), app('devenv.exe', 3.4, 2.1 * GB, 9 * GB, 0.2 * GB)],
    topIo: [app('MsMpEng.exe', 1.9, 0.4 * GB, 61 * GB, 0), app('chrome.exe', 6.1, 4.6 * GB, 22 * GB, 30 * GB), app('SearchIndexer.exe', 0.4, 0.1 * GB, 18 * GB, 0)],
    topNet: [app('chrome.exe', 6.1, 4.6 * GB, 22 * GB, 30 * GB), app('steam.exe', 0.3, 0.3 * GB, 9 * GB, 7.4 * GB), app('Telegram.exe', 0.6, 0.5 * GB, 1 * GB, 2.1 * GB)],
    episodes: [whyMock(t - 3 * 3600, t - 3 * 3600 + 420, 'cpu'), whyMock(t - 30 * 3600, t - 30 * 3600 + 960, 'mem')],
    slowMinutes: 23,
    anomalies: [{ rule: 'new_domain', count: 14 }, { rule: 'new_program', count: 5 }, { rule: 'exposed_port', count: 2 }, { rule: 'unsigned_net', count: 1 }],
    notable: [{ id: 1, ts: t - 7200, rule: 'unsigned_net', severity: 'high', app: 'sideloadlydaemon.exe', title: 'Программа без подписи выходит в сеть', detail: '', seen: true }],
    newPrograms: ['ollama.exe', 'Sideloadly.exe', 'node.exe', 'rufus-4.6.exe', 'OBS64.exe'],
    systemDrive: { root: 'C:\\', total: 476 * GB, free: 61 * GB },
    diskGrowth: { since: t - 7 * 86400, driveDelta: 18.6 * GB, items: [
      { path: 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\156.0.4314.42', size: 0.88 * GB, delta: 0.88 * GB, isNew: true },
      { path: 'C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\Application\\156.0.4314.42', size: 0.87 * GB, delta: 0.87 * GB, isNew: true },
      { path: 'C:\\Users\\ANTRARES\\.ollama\\models', size: 9.1 * GB, delta: 9.1 * GB, isNew: true },
      { path: 'C:\\Users\\ANTRARES\\AppData\\Local\\Docker', size: 41 * GB, delta: 5.2 * GB, isNew: false },
      { path: 'C:\\Windows\\SoftwareDistribution', size: 6.3 * GB, delta: 2.4 * GB, isNew: false },
    ] },
  };
}

function devMock() {
  const t = now();
  const d = (y: number, m: number, day: number) => Math.floor(new Date(y, m - 1, day).getTime() / 1000);
  const P = 'C:\\Program Files\\dotnet';
  const it = (name: string, version: string, path: string | null, size: number | null, support: string | null, until: number | null, note: string | null, uninstallId: string | null = null) =>
    ({ name, version, path, size, support, until, note, uninstallId });
  return {
    busy: false,
    data: {
      scannedAt: t - 40, seconds: 6.2,
      groups: [
        { id: 'dotnet', title: '.NET', hint: 'Для каждой основной версии обычно хватает последнего SDK: более старые патчи можно удалить.', items: [
          it('SDK', '10.0.100', P + '\\sdk\\10.0.100', 0.62 * GB, 'ok', d(2028, 11, 14), null, 'x'),
          it('SDK', '9.0.305', P + '\\sdk\\9.0.305', 0.55 * GB, 'soon', d(2026, 11, 10), null, 'x'),
          it('SDK', '8.0.414', P + '\\sdk\\8.0.414', 0.51 * GB, 'soon', d(2026, 11, 10), null, 'x'),
          it('SDK', '6.0.428', P + '\\sdk\\6.0.428', 0.43 * GB, 'eol', d(2024, 11, 12), null, 'x'),
          it('SDK', '5.0.408', P + '\\sdk\\5.0.408', 0.39 * GB, 'eol', d(2022, 5, 10), null, 'x'),
          it('SDK', '3.1.426', P + '\\sdk\\3.1.426', 0.33 * GB, 'eol', d(2022, 12, 13), null, 'x'),
          it('Runtime', '10.0.0', P + '\\shared\\Microsoft.NETCore.App\\10.0.0', 72 * MB, 'ok', d(2028, 11, 14), null),
          it('Runtime', '8.0.20', P + '\\shared\\Microsoft.NETCore.App\\8.0.20', 68 * MB, 'soon', d(2026, 11, 10), null),
          it('ASP.NET Core', '8.0.20', P + '\\shared\\Microsoft.AspNetCore.App\\8.0.20', 21 * MB, 'soon', d(2026, 11, 10), null),
          it('Desktop Runtime', '10.0.0', P + '\\shared\\Microsoft.WindowsDesktop.App\\10.0.0', 94 * MB, 'ok', d(2028, 11, 14), null),
          it('Packs и workloads', '', P + '\\packs', 2.8 * GB, null, null, 'MAUI, Android, iOS, WebAssembly и другие нагрузки'),
        ] },
        { id: 'netfx', title: '.NET Framework', hint: null, items: [it('.NET Framework 4.x', '4.8.1', null, null, 'ok', null, 'Входит в Windows, удалить нельзя')] },
        { id: 'node', title: 'Node.js', hint: null, items: [
          it('Node.js', '22.20.0', 'C:\\Users\\ANTRARES\\AppData\\Roaming\\nvm\\v22.20.0', 92 * MB, 'ok', d(2027, 4, 30), 'nvm'),
          it('Node.js', '20.18.0', 'C:\\Users\\ANTRARES\\AppData\\Roaming\\nvm\\v20.18.0', 88 * MB, 'eol', d(2026, 4, 30), 'nvm'),
          it('Node.js', '16.20.2', 'C:\\Users\\ANTRARES\\AppData\\Roaming\\nvm\\v16.20.2', 71 * MB, 'eol', d(2023, 9, 11), 'nvm'),
        ] },
        { id: 'npm', title: 'Глобальные пакеты npm', hint: null, items: [
          it('@anthropic-ai/claude-code', '2.4.1', null, null, null, null, null), it('expo-cli', '6.3.10', null, null, null, null, null),
          it('typescript', '5.9.3', null, null, null, null, null), it('vercel', '48.2.0', null, null, null, null, null),
        ] },
        { id: 'python', title: 'Python', hint: null, items: [
          it('Python', '3.12.10', 'C:\\Users\\ANTRARES\\AppData\\Local\\Programs\\Python\\Python312', 0.41 * GB, 'ok', d(2028, 10, 31), 'установщик, пакетов: 86', 'x'),
          it('Python', '3.10.11', 'C:\\Users\\ANTRARES\\AppData\\Local\\Programs\\Python\\Python310', 0.18 * GB, 'soon', d(2026, 10, 31), 'установщик, пакетов: 12', 'x'),
        ] },
        { id: 'java', title: 'Java (JDK)', hint: 'Без поддержки — версии не-LTS: на них не выходят исправления безопасности.', items: [
          it('JDK', '17.0.16', 'C:\\Program Files\\Eclipse Adoptium\\jdk-17.0.16.8-hotspot', 0.31 * GB, 'lts', null, 'Eclipse Adoptium', 'x'),
          it('JDK', '21.0.8', 'C:\\Program Files\\Android\\Android Studio\\jbr', 0.36 * GB, 'lts', null, 'JetBrains s.r.o., встроен в Android Studio'),
        ] },
        { id: 'android', title: 'Android SDK', hint: 'Нужен для React Native и Android Studio. Старые платформы и build-tools — частый источник лишних гигабайт.', items: [
          it('Платформа', 'android-35', null, 0.2 * GB, null, null, null), it('Платформа', 'android-33', null, 0.18 * GB, null, null, null),
          it('Build-tools', '35.0.0', null, 0.13 * GB, null, null, null), it('Build-tools', '33.0.1', null, 0.12 * GB, null, null, null),
          it('NDK', '27.1.12297006', null, 2.4 * GB, null, null, null), it('Образы эмулятора', 'android-34', null, 7.9 * GB, null, null, null),
          it('Эмулятор (AVD)', 'Pixel_7_API_34', null, 6.1 * GB, null, null, null),
        ] },
        { id: 'ide', title: 'IDE и редакторы', hint: null, items: [
          it('Visual Studio Community 2026', '18.0.1', 'C:\\Program Files\\Microsoft Visual Studio\\18\\Community', null, null, null, 'Компоненты меняются в Visual Studio Installer'),
          it('Visual Studio Code', '1.105.1', null, null, null, null, 'расширений: 47', 'x'),
          it('Android Studio', '2025.1.3', null, 2.9 * GB, null, null, 'последний запуск 2 сент. 2026', 'x'),
        ] },
        { id: 'containers', title: 'WSL и Docker', hint: 'Диски WSL и Docker сами не уменьшаются после удаления данных внутри — их нужно сжимать отдельно.', items: [
          it('WSL: Ubuntu-24.04', 'WSL 2', null, 14.2 * GB, null, null, 'размер файла-диска'),
          it('Docker Desktop', '4.47.0', null, 18.4 * GB, null, null, 'размер диска с образами и томами', 'x'),
        ] },
        { id: 'tools', title: 'Инструменты командной строки', hint: null, items: [
          it('git', '2.51.0', 'C:\\Program Files\\Git\\cmd\\git.exe', null, null, null, null, 'x'), it('gh', '2.80.0', 'C:\\Program Files\\GitHub CLI\\gh.exe', null, null, null, null),
          it('kubectl', '', 'C:\\Program Files\\Docker\\Docker\\resources\\bin\\kubectl.exe', null, null, null, null), it('az', '', 'C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin\\az.cmd', null, null, null, null),
        ] },
        { id: 'pm', title: 'Менеджеры пакетов', hint: null, items: [
          it('winget', '', null, null, null, null, 'Обновить всё: winget upgrade --all'),
          it('Кэш NuGet', '', 'C:\\Users\\ANTRARES\\.nuget\\packages', 6.7 * GB, null, null, 'Очистка: dotnet nuget locals all --clear'),
          it('Кэш npm', '', null, 2.2 * GB, null, null, 'Очистка: npm cache clean --force'),
          it('Кэш Gradle', '', null, 4.9 * GB, null, null, 'Очистка: удалить папку при закрытой IDE'),
        ] },
      ],
      services: [
        { name: 'MSSQLSERVER', display: 'SQL Server (MSSQLSERVER)', state: 'Running', startMode: 'Auto', pid: 4120, memory: 612 * MB, path: null },
        { name: 'postgresql-x64-17', display: 'postgresql-x64-17 - PostgreSQL Server 17', state: 'Running', startMode: 'Auto', pid: 5188, memory: 74 * MB, path: null },
        { name: 'com.docker.service', display: 'Docker Desktop Service', state: 'Stopped', startMode: 'Manual', pid: null, memory: null, path: null },
        { name: 'SQLBrowser', display: 'SQL Server Browser', state: 'Stopped', startMode: 'Disabled', pid: null, memory: null, path: null },
      ],
      commands: [
        { command: 'dotnet', path: 'C:\\Program Files\\dotnet\\dotnet.exe', version: '10.0.0', shadowed: [], note: null },
        { command: 'node', path: 'C:\\nvm4w\\nodejs\\node.exe', version: '22.20.0', shadowed: [], note: null },
        { command: 'python', path: 'C:\\Users\\ANTRARES\\AppData\\Local\\Microsoft\\WindowsApps\\python.exe', version: null, shadowed: ['C:\\Users\\ANTRARES\\AppData\\Local\\Programs\\Python\\Python312\\python.exe'], note: 'Это заглушка Microsoft Store, а не Python: она откроет Store. Отключите её в «Параметры → Приложения → Псевдонимы выполнения» или поставьте нужный Python выше в PATH.' },
        { command: 'java', path: 'C:\\Program Files\\Common Files\\Oracle\\Java\\javapath\\java.exe', version: '8.0.4510', shadowed: ['C:\\Program Files\\Eclipse Adoptium\\jdk-17.0.16.8-hotspot\\bin\\java.exe'], note: 'Найдено копий: 2. Запускается первая, остальные заслонены.' },
        { command: 'git', path: 'C:\\Program Files\\Git\\cmd\\git.exe', version: '2.51.0', shadowed: [], note: null },
      ],
      docker: {
        installed: true, running: true, version: '28.4.0', note: null, buildCache: 3.4e9, diskFile: 61.2 * GB,
        images: [
          { id: 'sha256:1a2b3c4d5e6f7a8b', repository: 'mcr.microsoft.com/mssql/server', tag: '2022-latest', size: 1.61e9, unique: 1.61e9, containers: 1, created: '5 weeks ago' },
          { id: 'sha256:2b3c4d5e6f7a8b9c', repository: 'postgres', tag: '17', size: 4.38e8, unique: 4.38e8, containers: 1, created: '3 weeks ago' },
          { id: 'sha256:3c4d5e6f7a8b9c0d', repository: 'ollama/ollama', tag: 'latest', size: 3.9e9, unique: 3.9e9, containers: 0, created: '2 months ago' },
          { id: 'sha256:4d5e6f7a8b9c0d1e', repository: 'rabbitmq', tag: '3-management', size: 2.55e8, unique: 2.55e8, containers: 1, created: '4 months ago' },
          { id: 'sha256:5e6f7a8b9c0d1e2f', repository: '<none>', tag: '<none>', size: 8.9e8, unique: 8.9e8, containers: 0, created: '6 months ago' },
          { id: 'sha256:6f7a8b9c0d1e2f3a', repository: 'shipnbuy-api', tag: 'dev', size: 2.31e8, unique: 1.1e7, containers: 1, created: '2 days ago' },
        ],
        containers: [
          { id: 'a1', name: 'sql2022', image: 'mcr.microsoft.com/mssql/server:2022-latest', state: 'running', status: 'Up 3 hours', size: 1.2e8 },
          { id: 'b2', name: 'pg17', image: 'postgres:17', state: 'running', status: 'Up 3 hours', size: 6.3e4 },
          { id: 'c3', name: 'rabbit', image: 'rabbitmq:3-management', state: 'exited', status: 'Exited (0) 9 days ago', size: 2.1e5 },
          { id: 'd4', name: 'shipnbuy-api-1', image: 'shipnbuy-api:dev', state: 'exited', status: 'Exited (137) 2 days ago', size: 1.4e6 },
        ],
        volumes: [
          { name: 'sql2022-data', size: 2.9e9, links: 1 }, { name: 'pg17-data', size: 6.1e8, links: 1 },
          { name: '3f9a0c2d7e8b1a4c5d6e', size: 1.4e8, links: 0 },
        ],
      },
      projects: [
        { path: 'C:\\GIT\\lotralogistics-app-next', name: 'lotralogistics-app-next', dependencies: 34, devDependencies: 18, modulesSize: 1.21 * GB, lastModified: t - 86400 * 40 },
        { path: 'C:\\GIT\\glissando', name: 'glissando', dependencies: 22, devDependencies: 15, modulesSize: 0.62 * GB, lastModified: t - 86400 * 61 },
        { path: 'C:\\GIT\\SysWatch\\src\\ui', name: 'syswatch-ui', dependencies: 5, devDependencies: 6, modulesSize: 0.19 * GB, lastModified: t - 600 },
        { path: 'C:\\GIT\\knoblet', name: 'knoblet', dependencies: 12, devDependencies: 9, modulesSize: 0.48 * GB, lastModified: t - 86400 * 25 },
      ],
      path: [
        { dir: 'C:\\Program Files\\Common Files\\Oracle\\Java\\javapath', scope: 'system', exists: true, duplicate: false },
        { dir: '%SystemRoot%\\system32', scope: 'system', exists: true, duplicate: false },
        { dir: 'C:\\Program Files\\dotnet\\', scope: 'system', exists: true, duplicate: false },
        { dir: 'C:\\Program Files\\Git\\cmd', scope: 'system', exists: true, duplicate: false },
        { dir: 'C:\\Program Files (x86)\\Razer\\ChromaBroadcast\\bin', scope: 'system', exists: false, duplicate: false },
        { dir: 'C:\\Users\\ANTRARES\\AppData\\Local\\Microsoft\\WindowsApps', scope: 'user', exists: true, duplicate: false },
        { dir: 'C:\\Users\\ANTRARES\\AppData\\Local\\Programs\\Python\\Python312\\', scope: 'user', exists: true, duplicate: false },
        { dir: 'C:\\Program Files\\dotnet', scope: 'user', exists: true, duplicate: true },
      ],
    },
  };
}

function historyMock(days: number, q: string, source: string, kind: string) {
  const t = now();
  const S = [
    ['как отключить заглушку python microsoft store', 'Google'], ['dotnet 10 release notes', 'Google'], ['ETW TcpIpSend payload layout', 'Google'],
    ['батуми погода выходные', 'Яндекс'], ['react native new architecture fabric', 'Google'], ['legion pro 5 gen 10 цена грузия', 'Google'],
    ['uplot time axis format', 'GitHub'], ['postgres 17 logical replication', 'Google'], ['lofi coding mix', 'YouTube'], ['ollama qwen3 coder windows', 'Google'],
  ];
  const V = [
    ['github.com', 'pilgrim-12/SysWatch: Windows system monitor'], ['learn.microsoft.com', 'Event Tracing for Windows (ETW) - Win32 apps'],
    ['stackoverflow.com', 'c# - Read ETW event payload without allocation'], ['claude.ai', 'Claude'], ['youtube.com', 'Lofi coding mix — YouTube'],
    ['habr.com', 'Как устроен ETW: разбор изнутри'], ['nuget.org', 'NuGet Gallery | Microsoft.Diagnostics.Tracing.TraceEvent 3.1.20'],
    ['mail.google.com', 'Входящие — Gmail'], ['web.telegram.org', 'Telegram Web'], ['github.com', 'Pull requests · pilgrim-12/glissando'],
  ];
  const sources = ['Chrome', 'Edge', 'Firefox'];
  const items: any[] = [];
  for (let i = 0; i < 160; i++) {
    const ts = t - Math.floor(i * (days * 86400 / 170)) - 300;
    const src = sources[i % 7 === 0 ? 1 : i % 11 === 0 ? 2 : 0];
    if (i % 4 === 0) {
      const [term, engine] = S[(i / 4) % S.length];
      items.push({ ts, source: src, kind: 'search', url: 'https://www.google.com/search?q=' + encodeURIComponent(term), title: term, domain: 'google.com', term, engine });
    } else {
      const [domain, title] = V[i % V.length];
      items.push({ ts, source: src, kind: 'visit', url: `https://${domain}/`, title, domain, term: null, engine: null });
    }
  }
  const f = items.filter(i => (!source || i.source === source) && (!q || `${i.url} ${i.title} ${i.term ?? ''}`.toLowerCase().includes(q.toLowerCase())));
  const dom = new Map<string, any>();
  for (const i of f.filter(i => i.kind === 'visit')) {
    const d = dom.get(i.domain) ?? { domain: i.domain, visits: 0, last: 0, sources: [] as string[] };
    d.visits++; d.last = Math.max(d.last, i.ts); if (!d.sources.includes(i.source)) d.sources.push(i.source);
    dom.set(i.domain, d);
  }
  return {
    from: t - days * 86400,
    profiles: [
      { id: 'Chrome', browser: 'Chrome', profile: 'Основной', ok: true, error: null, visits: 98, searches: 31 },
      { id: 'Edge', browser: 'Edge', profile: '', ok: true, error: null, visits: 14, searches: 5 },
      { id: 'Firefox', browser: 'Firefox', profile: 'default-release', ok: true, error: null, visits: 8, searches: 4 },
    ],
    items: f.filter(i => !kind || i.kind === kind), total: f.length,
    searches: f.filter(i => i.kind === 'search').length, visits: f.filter(i => i.kind === 'visit').length,
    domains: [...dom.values()].sort((a, b) => b.visits - a.visits),
  };
}

function gpuMock(range: number) {
  const t = now();
  const step = 60 * Math.max(1, Math.ceil(range / 720));
  const first = t - range * 60 - ((t - range * 60) % step);
  const n = Math.floor((t - first) / step) + 1;
  const ts = Array.from({ length: n }, (_, i) => first + i * step);
  const wave = (i: number, base: number, amp: number) => Math.max(0, base + amp * Math.sin(i / 9) + (Math.random() - 0.5) * amp * 0.6);
  const util = ts.map((_, i) => wave(i, 18, 15));
  return {
    hasData: true, ts,
    adapters: [
      { key: '0x00000000_0x0000D1C1', name: 'NVIDIA GeForce RTX 4060 Laptop GPU', memTotal: 8 * GB, util: 23, temp: 61, power: 34, fan: 2150, memUsed: 2.7 * GB, lastTs: t - 40, avgUtil: 17.2, peakUtil: 97, peakTemp: 84, avgTemp: 56 },
      { key: '0x00000000_0x0000C9A0', name: 'Intel(R) Iris(R) Xe Graphics', memTotal: 0, util: 6, temp: null, power: null, fan: null, memUsed: 0.4 * GB, lastTs: t - 40, avgUtil: 5.1, peakUtil: 41, peakTemp: null, avgTemp: null },
    ],
    series: [
      { adapter: '0x00000000_0x0000D1C1', util, util3D: util.map(v => v * 0.8), video: util.map(v => v * 0.25), compute: util.map((v, i) => (i % 40 < 6 ? 60 : 0)),
        mem: ts.map((_, i) => (2.2 + Math.sin(i / 30) * 0.6) * GB), temp: util.map(v => 42 + v * 0.5) },
      { adapter: '0x00000000_0x0000C9A0', util: util.map(v => v * 0.3), util3D: util.map(v => v * 0.2), video: util.map(v => v * 0.1), compute: util.map(() => 0),
        mem: ts.map(() => 0.4 * GB), temp: ts.map(() => null) },
    ],
    apps: [
      { name: 'chrome.exe', adapter: '0x00000000_0x0000D1C1', avgUtil: 6.2, peakUtil: 41, memMax: 0.9 * GB, minutes: 600 },
      { name: 'Code.exe', adapter: '0x00000000_0x0000D1C1', avgUtil: 2.1, peakUtil: 18, memMax: 0.31 * GB, minutes: 700 },
      { name: 'ollama.exe', adapter: '0x00000000_0x0000D1C1', avgUtil: 4.8, peakUtil: 97, memMax: 5.1 * GB, minutes: 35 },
      { name: 'dwm.exe', adapter: '0x00000000_0x0000C9A0', avgUtil: 3.4, peakUtil: 22, memMax: 0.21 * GB, minutes: 1440 },
      { name: 'Telegram.exe', adapter: '0x00000000_0x0000C9A0', avgUtil: 0.6, peakUtil: 9, memMax: 0.08 * GB, minutes: 900 },
    ],
  };
}

function crashMock(days: number) {
  const t = now();
  const items = [
    { ts: t - 3600 * 5, kind: 'gpu', title: 'Видеодрайвер перестал отвечать и был восстановлен', detail: 'Экран мог погаснуть на пару секунд. Частая причина — перегрев, разгон или нестабильный драйвер.', app: null, source: 'Display', eventId: 4101 },
    { ts: t - 3600 * 5 + 40, kind: 'gpu', title: 'Сбой видеодрайвера (LiveKernelEvent)', detail: 'Windows сохранила отчёт о сбросе видеокарты.', app: null, source: 'Windows Error Reporting', eventId: 1001 },
    { ts: t - 86400 * 2, kind: 'app', title: 'Программа аварийно завершилась', detail: 'Модуль: ntdll.dll, код 0xc0000374', app: 'Sideloadly.exe', source: 'Application Error', eventId: 1000 },
    { ts: t - 86400 * 3, kind: 'hang', title: 'Программа зависла и была закрыта', detail: 'Окно не отвечало, Windows завершило программу.', app: 'devenv.exe', source: 'Application Hang', eventId: 1002 },
    { ts: t - 86400 * 6, kind: 'power', title: 'Внезапное выключение', detail: 'Компьютер выключился без завершения работы: пропало питание, зависание или удерживали кнопку питания.', app: null, source: 'Microsoft-Windows-Kernel-Power', eventId: 41 },
    { ts: t - 86400 * 12, kind: 'bsod', title: 'Синий экран (BSOD)', detail: 'Код остановки 0x00000116. Дамп памяти: C:\\Windows\\MEMORY.DMP', app: null, source: 'Microsoft-Windows-WER-SystemErrorReporting', eventId: 1001 },
    { ts: t - 86400 * 20, kind: 'update', title: 'Обновление Windows не установилось', detail: '2026-09 Cumulative Update for Windows 11 Version 24H2 (KB5065426)', app: null, source: 'Microsoft-Windows-WindowsUpdateClient', eventId: 20 },
  ].filter(c => c.ts >= t - days * 86400);
  const counts: Record<string, number> = {};
  for (const c of items) counts[c.kind] = (counts[c.kind] ?? 0) + 1;
  return { days, items, counts };
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

const ROOT = 'C:\\';
const TREE: Record<string, [string, number, boolean, number | null][]> = {
  'C:\\': [
    ['Users', 168 * GB, true, 9.4 * GB], ['Windows', 41 * GB, true, 0.3 * GB], ['Program Files', 38 * GB, true, 0.8 * GB],
    ['ProgramData', 22 * GB, true, 2.1 * GB], ['Program Files (x86)', 14 * GB, true, 0], ['hiberfil.sys', 12.6 * GB, false, 0],
    ['pagefile.sys', 9.5 * GB, false, 0], ['GIT', 7.2 * GB, true, 0.6 * GB], ['swapfile.sys', 0.25 * GB, false, null],
  ],
  'C:\\Users': [['ANTRARES', 166 * GB, true, 9.4 * GB], ['Public', 1.2 * GB, true, 0]],
  'C:\\Users\\ANTRARES': [
    ['AppData', 71 * GB, true, 6.8 * GB], ['.nuget', 14.8 * GB, true, 0.9 * GB], ['Downloads', 21 * GB, true, 1.4 * GB],
    ['AndroidStudioProjects', 9.1 * GB, true, 0], ['.android', 8.4 * GB, true, 0], ['Videos', 12 * GB, true, 0],
    ['Documents', 6.3 * GB, true, 0.2 * GB], ['source', 5.9 * GB, true, 0.1 * GB], ['npm-cache', 3.8 * GB, true, 0.4 * GB],
    ['.platformio', 2.9 * GB, true, 0], ['docker', 2.1 * GB, true, null],
  ],
};

function diskState(root: string | null) {
  const t = now();
  const scanning = root === 'D:\\';
  return {
    status: scanning ? 'scanning' : 'idle', progress: null, error: null, busyRoot: scanning ? 'D:\\' : null, root: root || ROOT,
    op: scanning ? { phase: 'disk', bytes: 312 * GB, target: 519 * GB, files: 812_344, dirs: 61_207, started: t - 83, current: 'D:\\Projects\\video-archive\\2024\\raw\\clip_0412.mov', step: 1, steps: 1 } : null,
    drives: [
      { root: 'C:\\', label: 'Локальный диск', format: 'NTFS', kind: 'fixed', total: 476 * GB, free: 61.7 * GB, isSystem: true, lastScan: t - 3 * 3600 },
      { root: 'D:\\', label: 'Data', format: 'NTFS', kind: 'fixed', total: 931 * GB, free: 412 * GB, isSystem: false, lastScan: null },
      { root: 'E:\\', label: 'Samsung T7', format: 'exFAT', kind: 'removable', total: 1863 * GB, free: 96 * GB, isSystem: false, lastScan: t - 9 * 86400 },
    ],
    snapshot: { id: 3, ts: t - 3 * 3600, scanned: 384 * GB, files: 1_284_311, seconds: 94, shadow: 11.2 * GB, hidden: 30.3 * GB },
    compareTs: t - 7 * 86400,
    lastClean: null,
    cleanup: {
      scannedAt: t - 3 * 3600,
      categories: [
        { id: 'temp', group: 'system', title: 'Временные файлы', description: 'Папки Temp пользователя и Windows. Файлы моложе суток не трогаем.', mode: 'delete', defaultOn: true, itemsSelectable: false, hint: null, size: 6.4 * GB, files: 48211, items: [{ path: 'C:\\Users\\ANTRARES\\AppData\\Local\\Temp', size: 5.9 * GB, files: 47001, modified: t - 86400 * 2 }, { path: 'C:\\Windows\\Temp', size: 0.5 * GB, files: 1210, modified: t - 86400 * 4 }] },
        { id: 'windows_update', group: 'system', title: 'Загрузки обновлений Windows', description: 'Уже установленные обновления, которые Windows скачала заранее.', mode: 'delete', defaultOn: true, itemsSelectable: false, hint: null, size: 3.1 * GB, files: 812, items: [{ path: 'C:\\Windows\\SoftwareDistribution\\Download', size: 3.1 * GB, files: 812, modified: t - 86400 * 9 }] },
        { id: 'browser_cache', group: 'system', title: 'Кэш браузеров', description: 'Chrome, Edge, Firefox. Сайты загрузятся заново. Открытые браузеры держат часть файлов — их пропустим.', mode: 'delete', defaultOn: true, itemsSelectable: false, hint: null, size: 2.7 * GB, files: 31022, items: [] },
        { id: 'crash_dumps', group: 'system', title: 'Дампы падений и отчёты об ошибках', description: 'Снимки памяти упавших программ и отчёты для Microsoft.', mode: 'delete', defaultOn: true, itemsSelectable: false, hint: null, size: 1.9 * GB, files: 77, items: [] },
        { id: 'thumbnails', group: 'system', title: 'Кэш миниатюр', description: 'Превью картинок в Проводнике. Пересоздастся сам.', mode: 'delete', defaultOn: true, itemsSelectable: false, hint: null, size: 0.4 * GB, files: 12, items: [] },
        { id: 'recycle_bin', group: 'system', title: 'Корзина', description: 'Удалённые файлы, которые ещё можно восстановить. Очистка безвозвратна.', mode: 'delete', defaultOn: false, itemsSelectable: false, hint: null, size: 4.2 * GB, files: 341, items: [{ path: 'Корзина', size: 4.2 * GB, files: 341, modified: 0 }] },
        { id: 'nuget', group: 'dev', title: 'Пакеты NuGet', description: 'Скачанные .NET-пакеты. При следующей сборке скачаются заново.', mode: 'delete', defaultOn: false, itemsSelectable: false, hint: null, size: 14.8 * GB, files: 210334, items: [] },
        { id: 'npm', group: 'dev', title: 'Кэш npm, pnpm и Yarn', description: 'Скачанные JavaScript-пакеты. При установке скачаются заново.', mode: 'delete', defaultOn: false, itemsSelectable: false, hint: null, size: 6.1 * GB, files: 402110, items: [] },
        { id: 'gradle', group: 'dev', title: 'Кэш Gradle', description: 'Зависимости Android- и Java-проектов. При сборке скачаются заново.', mode: 'delete', defaultOn: false, itemsSelectable: false, hint: null, size: 5.3 * GB, files: 61022, items: [] },
        { id: 'old_builds', group: 'dev', title: 'Старые node_modules, bin, obj, .next', description: 'Папки сборок в проектах, которые не менялись 30+ дней. Уйдут в Корзину; вернуть — npm install или dotnet build.', mode: 'recycle', defaultOn: false, itemsSelectable: true, hint: null, size: 11.6 * GB, files: 380221,
          items: [['C:\\GIT\\stellix-tv\\node_modules', 1.9], ['C:\\GIT\\harmonic-wave-studio\\node_modules', 1.4], ['C:\\Users\\ANTRARES\\source\\repos\\OrderSystem\\OrderSystem.Api\\bin', 0.6], ['C:\\GIT\\birdwatch\\.next', 0.4]].map(([p, g], i) => ({ path: p, size: (g as number) * GB, files: 20000, modified: t - 86400 * (40 + i * 20) })) },
        { id: 'downloads', group: 'review', title: 'Большие старые файлы в «Загрузках»', description: 'Файлы от 50 МБ, которые не открывали 90+ дней. Ничего не отмечено — выбери сам. Уйдут в Корзину.', mode: 'recycle', defaultOn: false, itemsSelectable: true, hint: null, size: 9.2 * GB, files: 14, items: [{ path: 'C:\\Users\\ANTRARES\\Downloads\\ubuntu-24.04-desktop-amd64.iso', size: 5.7 * GB, files: 1, modified: t - 86400 * 210 }, { path: 'C:\\Users\\ANTRARES\\Downloads\\android-studio-2025.exe', size: 1.2 * GB, files: 1, modified: t - 86400 * 150 }] },
        { id: 'docker', group: 'review', title: 'Docker', description: 'Образы, контейнеры и тома живут внутри одного файла-диска. Удалять сам файл нельзя.', mode: 'none', defaultOn: false, itemsSelectable: false, hint: 'docker system prune -a --volumes   — удалит неиспользуемое', size: 18.4 * GB, files: 1, items: [{ path: 'C:\\Users\\ANTRARES\\AppData\\Local\\Docker\\wsl\\disk\\docker_data.vhdx', size: 18.4 * GB, files: 1, modified: t - 3600 }] },
        { id: 'windows_old', group: 'review', title: 'Предыдущая версия Windows', description: 'Остаётся после крупного обновления, нужна только для отката.', mode: 'none', defaultOn: false, itemsSelectable: false, hint: null, size: 0, files: 0, items: [] },
      ],
    },
  };
}

function diskTree(path: string | null) {
  const p = path || ROOT;
  const rows = TREE[p] ?? [];
  const sub = (n: string, size: number) => {
    const kids = TREE[(p.endsWith('\\') ? p : p + '\\') + n];
    if (kids) return kids.slice(0, 8).map(([cn, cs, cd]) => ({ name: cn, isDir: cd, size: cs }));
    const parts = [0.34, 0.22, 0.14, 0.09, 0.06];
    return parts.map((f, i) => ({ name: ['packages', 'cache', 'Microsoft', 'data', 'logs'][i], isDir: true, size: size * f }));
  };
  const items = rows.map(([name, size, isDir, delta]) => {
    const children = isDir ? sub(name, size) : [];
    return { path: (p.endsWith('\\') ? p : p + '\\') + name, name, isDir, size, files: 1000, delta, children, other: Math.max(0, size - children.reduce((s, c) => s + c.size, 0)) };
  });
  const total = p === ROOT ? 384 * GB : items.reduce((s, i) => s + i.size, 0) * 1.12;
  return { path: p, size: total, delta: p === ROOT ? 13.5 * GB : null, items, other: total - items.reduce((s, i) => s + i.size, 0) };
}

// ── Поиск, содержимое папки, корзина ──
const HITS = [
  ['C:\\Users\\ANTRARES\\Videos\\Captures\\demo-2026-10-12.mp4', 3.9 * GB],
  ['C:\\Users\\ANTRARES\\Videos\\trip\\GOPR0412.mp4', 2.4 * GB],
  ['C:\\Users\\ANTRARES\\Downloads\\ubuntu-24.04-desktop.iso', 5.7 * GB],
  ['C:\\Users\\ANTRARES\\Downloads\\lecture-ml-net.mp4', 812 * MB],
  ['C:\\Users\\ANTRARES\\Documents\\Zoom\\2026-09-30 standup\\video1.mp4', 418 * MB],
  ['C:\\GIT\\SysWatch\\docs\\screen-record.mp4', 96 * MB],
  ['C:\\Users\\ANTRARES\\AppData\\Local\\Temp\\tmp81A2.mp4', 12 * MB],
] as const;
let searchAt = 0;
function searchState() {
  const el = (now() - searchAt);
  const running = searchAt > 0 && el < 3;
  const n = searchAt === 0 ? 0 : running ? Math.min(HITS.length, Math.floor(el * 3)) : HITS.length;
  return {
    running, query: searchAt ? { text: '*.mp4', root: 'all', kind: '', minSize: 0, days: 0, folders: false } : null,
    dirs: running ? Math.floor(el * 20000) : 61_207, files: running ? Math.floor(el * 250000) : 812_344,
    current: running ? 'C:\\Users\\ANTRARES\\AppData\\Local\\Packages' : null, started: searchAt, seconds: running ? el : 3.4,
    truncated: false, error: null,
    hits: HITS.slice(0, n).map(([p, size], i) => ({ path: p, name: p.slice(p.lastIndexOf('\\') + 1), isDir: false, size, modified: now() - i * 86400 * 3 })),
  };
}
function folderList(path: string) {
  const p = path || ROOT;
  const rows = TREE[p] ?? [];
  const base = p.endsWith('\\') ? p : p + '\\';
  const dirs = rows.filter(r => r[2]).map(([n, size]) => ({ path: base + n, name: n, isDir: true, size, modified: now() - 86400 * 2, hidden: n.startsWith('.') }));
  const files = [
    ...rows.filter(r => !r[2]).map(([n, size]) => ({ path: base + n, name: n, isDir: false, size, modified: now() - 3600, hidden: n.endsWith('.sys') })),
    ...['report-2026-10.pdf', 'notes.txt', 'setup.log', 'photo_0412.jpg', 'budget.xlsx', 'desktop.ini'].map((n, i) => ({ path: base + n, name: n, isDir: false, size: [4.2 * MB, 3 * KB, 180 * KB, 6.1 * MB, 92 * KB, 282][i], modified: now() - i * 86400 * 5, hidden: n === 'desktop.ini' })),
    ...Array.from({ length: 60 }, (_, i) => { const ext = ['mp4', 'jpg', 'pdf', 'zip', 'docx', 'png'][i % 6]; const n = `file_${String(i).padStart(3, '0')}.${ext}`; return { path: base + n, name: n, isDir: false, size: Math.round(90 * MB / (1 + i * 0.6)), modified: now() - i * 7200, hidden: false }; }),
  ].sort((a, b) => b.size - a.size);
  return { path: p, exists: true, total: dirs.length + files.length, filesSize: files.reduce((s, f) => s + f.size, 0), truncated: false, entries: [...dirs, ...files] };
}
let recycleItems = [
  ['C:\\Users\\ANTRARES\\Downloads\\Win11_24H2_Russian_x64.iso', 5.8 * GB, false, 1],
  ['C:\\GIT\\old-prototype', 1.3 * GB, true, 2],
  ['C:\\Users\\ANTRARES\\Videos\\Captures\\obs-2026-09-02.mkv', 2.1 * GB, false, 5],
  ['C:\\Users\\ANTRARES\\Desktop\\черновик договора.docx', 48 * KB, false, 9],
  ['D:\\Photos\\2019\\дубли', 740 * MB, true, 12],
  ['C:\\Users\\ANTRARES\\Documents\\счёт-сентябрь.pdf', 220 * KB, false, 20],
] as [string, number, boolean, number][];
function recycle() {
  const items = recycleItems.map(([p, size, isDir, d], i) => ({ id: `${p.slice(0, 3)}$Recycle.Bin\\S-1-5-21\\$R${i}`, name: p.slice(p.lastIndexOf('\\') + 1), original: p, isDir, size, deleted: now() - d * 86400, drive: p.slice(0, 3) }));
  return { total: items.reduce((s, x) => s + x.size, 0), count: items.length, truncated: false, items };
}

function diskGrowth() {
  return {
    since: now() - 7 * 86400,
    driveDelta: 13.5 * GB,
    items: [
      { path: 'C:\\Users\\ANTRARES\\AppData\\Local\\Docker\\wsl\\disk', size: 18.4 * GB, delta: 4.1 * GB, isNew: false },
      { path: 'C:\\ProgramData\\Microsoft\\Windows\\WER\\ReportArchive', size: 1.6 * GB, delta: 1.5 * GB, isNew: true },
      { path: 'C:\\Users\\ANTRARES\\Downloads', size: 21 * GB, delta: 1.4 * GB, isNew: false },
      { path: 'C:\\Users\\ANTRARES\\AppData\\Local\\Temp', size: 5.9 * GB, delta: 1.2 * GB, isNew: false },
      { path: 'C:\\Users\\ANTRARES\\.nuget\\packages', size: 14.8 * GB, delta: 0.9 * GB, isNew: false },
      { path: 'C:\\GIT\\SysWatch\\src\\ui\\node_modules', size: 0.4 * GB, delta: 0.4 * GB, isNew: true },
    ],
  };
}

const CONNS = [
  ['out', 'chrome.exe', 'TCP', '142.250.185.78', 443, 'www.youtube.com', 'установлено'],
  ['out', 'chrome.exe', 'TCP', '140.82.112.25', 443, 'github.com', 'установлено'],
  ['out', 'claude.exe', 'TCP', '160.79.104.10', 443, 'api.anthropic.com', 'установлено'],
  ['out', 'Telegram.exe', 'TCP', '149.154.167.51', 443, null, 'установлено'],
  ['out', 'msedge.exe', 'TCP', '13.107.21.200', 443, 'edge.microsoft.com', 'установлено'],
  ['out', 'svchost.exe', 'TCP', '20.42.73.29', 443, 'v10.events.data.microsoft.com', 'подключается'],
  ['out', 'Code.exe', 'TCP', '13.107.6.175', 443, 'marketplace.visualstudio.com', 'установлено'],
  ['out', 'Viber.exe', 'TCP', '52.0.252.0', 4244, 'content.cdn.viber.com', 'установлено'],
  ['in', 'sqlservr.exe', 'TCP', '192.168.1.24', 52114, null, 'установлено'],
  ['listen', 'sqlservr.exe', 'TCP', '0.0.0.0', 0, null, 'слушает'],
  ['listen', 'mongod.exe', 'TCP', '127.0.0.1', 0, null, 'слушает'],
  ['listen', 'svchost.exe', 'UDP', '0.0.0.0', 0, null, 'слушает'],
  ['listen', 'sideloadlydaemon.exe', 'TCP', '127.0.0.1', 0, null, 'слушает'],
] as const;

function connections() {
  const t = now();
  const rows = CONNS.map(([dir, app, proto, remote, rport, domain, state], i) => ({
    key: `k${i}`, proto, dir, pid: 1000 + i * 37, app, path: `C:\\Program Files\\${app}`,
    local: dir === 'listen' ? remote : '192.168.1.10', localPort: dir === 'listen' ? [1433, 27017, 5353, 26750][i - 9] ?? 0 : dir === 'in' ? 1433 : 50000 + i,
    remote: dir === 'listen' ? '0.0.0.0' : remote, remotePort: rport, domain, state,
    firstSeen: t - (i === 5 ? 1 : 30 + i * 140), closedAt: i === 7 ? t - 2 : null,
    exposed: dir !== 'out' && remote !== '127.0.0.1', country: dir === 'out' ? ['US', 'US', 'US', 'NL', 'IE', 'US', 'US', 'DE'][i] ?? null : dir === 'in' ? (i % 2 ? 'LAN' : 'NL') : null,
  }));
  return { ts: t, rows, out: 7, in: 1, listen: 4, exposed: 2, geoAvailable: true, geoStatus: null };
}

function programs() {
  const t = now(), d = 86400;
  const P = (name: string, pub: string, size: number, last: number | null, by: string | null, kind = 'app', installedAgo = 400) =>
    ({ id: name, name, publisher: pub, version: '1.0', installed: t - installedAgo * d, size: size * MB, lastUsed: last == null ? null : t - last * d, lastUsedBy: by, location: `C:\\Program Files\\${name}`, kind, canUninstall: true });
  return {
    scannedAt: t - 60, prefetchAvailable: true,
    items: [
      P('Sideloadly', 'Sideloadly', 210, 190, 'prefetch'),
      P('Android Studio', 'Google LLC', 3900, 140, 'prefetch'),
      P('Fiddler Classic', 'Progress Software', 120, 95, 'prefetch'),
      P('Muse Hub', 'Muse Group', 340, null, null),
      P('Icecream PDF Editor', 'Icecream Apps', 180, 230, 'prefetch'),
      P('Azure Data Studio', 'Microsoft Corporation', 620, 75, 'prefetch'),
      P('Docker Desktop', 'Docker Inc.', 2100, 2, 'prefetch'),
      P('Visual Studio Code', 'Microsoft Corporation', 410, 0, 'running'),
      P('Telegram Desktop', 'Telegram FZ-LLC', 160, 0, 'running'),
      P('Microsoft Visual C++ 2015-2022 Redistributable', 'Microsoft Corporation', 23, null, null, 'runtime'),
      P('Microsoft .NET Runtime - 8.0.11', 'Microsoft Corporation', 70, null, null, 'runtime'),
    ],
  };
}

function healthMock() {
  const t0 = now() - 86400;
  const points = Array.from({ length: 144 }, (_, i) => ({ ts: t0 + i * 600, cpu: Math.round(8 + 40 * noise(i / 7) ** 2), mem: Math.round(55 + 20 * noise(i / 30 + 3)) }));
  return {
    ts: now() - 40, score: 71, grade: 'Нормально', gradeKey: 'fair',
    findings: [
      { id: 'space-C', severity: 'warn', title: 'Мало места на диске C:', detail: 'Свободно 18,4 ГБ из 237 ГБ (8%). Когда место кончится, Windows начнёт тормозить и перестанет ставить обновления.', action: '#disk', actionLabel: 'Освободить место', penalty: 15 },
      { id: 'power', severity: 'warn', title: 'Внезапных выключений за неделю: 1', detail: 'Компьютер выключился без завершения работы — пропало питание или зависание.', action: '#system', actionLabel: 'Сбои', penalty: 8 },
      { id: 'slow', severity: 'info', title: 'Перегрузки за неделю: 2', detail: 'Чаще всего виноват chrome. Разбор каждого эпизода — в отчёте «Неделя».', action: '#report', actionLabel: 'Почему тормозило', penalty: 5 },
      { id: 'dev-eol', severity: 'info', title: 'Компонентов без поддержки: 2', detail: '.NET 6.0, Node.js 16 больше не получают исправлений безопасности.', action: '#dev', actionLabel: 'Разработка', penalty: 4 },
    ],
    facts: { agentRunning: true, agentCpu: 0.04, cpuAvg: 17.3, memAvg: 63.1, memPeak: 88.4, systemDrive: 'C:\\', driveFree: 18.4e9, driveTotal: 237e9, uptimeSeconds: 3 * 86400 + 5 * 3600, slowdowns7d: 2, crashes7d: 1, anomalies7d: 3, gpuPeakTemp: 71, dataHours: 160 },
    points,
  };
}

const TEMPLATES = [
  { id: 'health', title: 'Здоровье ПК', description: 'Оценка, проблемы и что с ними делать', defaultDays: 7, hasPeriod: false },
  { id: 'week', title: 'Неделя', description: 'Нагрузка, кто грузил, перегрузки', defaultDays: 7, hasPeriod: true },
  { id: 'security', title: 'Безопасность', description: 'Автозагрузка, брандмауэр, аномалии', defaultDays: 7, hasPeriod: true },
  { id: 'network', title: 'Сеть', description: 'Трафик, страны, новые адреса', defaultDays: 7, hasPeriod: true },
  { id: 'dev', title: 'Разработка', description: 'SDK, версии, устаревшее', defaultDays: 7, hasPeriod: false },
];
const schedules = [{ template: 'health', every: 'week', format: 'pdf', lastRun: now() - 3 * 86400 }];
const docHtml = (t: string) => `<!doctype html><html><body style="font:14px Segoe UI,sans-serif;color:#1b2533;padding:24px 32px"><h1>${TEMPLATES.find(x => x.id === t)?.title}</h1><p style="color:#667">Компьютер ANTRARES · 16–23 октября 2026</p><h2>Итог</h2><p>Оценка 71 из 100. Найдено 4 проблемы.</p><table border=1 cellpadding=6 style="border-collapse:collapse"><tr><th>Проверка</th><th>Результат</th></tr><tr><td>Место</td><td>8% свободно</td></tr><tr><td>Сбои</td><td>1</td></tr></table></body></html>`;

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
      case '/api/disk': return diskState(u.searchParams.get('root'));
      case '/api/connections': return connections();
      case '/api/geo/home': return { home: 'GE' };
      case '/api/dev': return devMock();
      case '/api/gpu': return gpuMock(range);
      case '/api/system/crashes': return crashMock(Number(u.searchParams.get('days') ?? 30));
      case '/api/dev/docker': return { ok: true, message: 'Готово.' };
      case '/api/dev/node-modules': return { ok: true, message: 'node_modules удалена.' };
      case '/api/history/delete': return { ok: true, message: 'Edge сейчас открыт — удалю автоматически, как только вы его закроете. Из этого списка записи уже скрыты.', deleted: 0, running: ['Edge'], pending: ['Edge'] };
      case '/api/history/pending': return ['Edge'];
      case '/api/history': return historyMock(Number(u.searchParams.get('days') ?? 7), u.searchParams.get('q') ?? '', u.searchParams.get('source') ?? '', u.searchParams.get('kind') ?? '');
      case '/api/report': return reportMock(Number(u.searchParams.get('days') ?? 7));
      case '/api/settings': return { retentionDays: 90, detailDays: 30, alerts: { enabled: true, cpuPct: 90, memPct: 90, minutes: 5, diskFreePct: 10, gpuTemp: 85 }, storage: { dir: 'C:\\ProgramData\\SysWatch\\hot', bytes: 412e6, days: 7, oldest: now() - 6 * 86400, avgDayBytes: 58e6 } };
      case '/api/updates': return { available: true, error: null, checkedAt: now() - 300, checking: false, items: [
        { name: 'Google Chrome', id: 'Google.Chrome', version: '129.0.6668.90', available: '130.0.6723.70', source: 'winget' },
        { name: 'Visual Studio Code', id: 'Microsoft.VisualStudioCode', version: '1.94.0', available: '1.95.1', source: 'winget' },
        { name: 'Node.js LTS', id: 'OpenJS.NodeJS.LTS', version: '20.17.0', available: '22.11.0', source: 'winget' },
        { name: 'Docker Desktop', id: 'Docker.DockerDesktop', version: '4.33.1', available: '4.35.1', source: 'winget' },
      ], job: { running: true, current: 'Google.Chrome', done: ['7zip.7zip'], failed: [], log: null } };
      case '/api/updates/install': return true;
      case '/api/geo': {
        const C = (code: string, mb: number, apps: string[], domains: string[]) => ({ code, tx: mb * 1e6 * 0.15, rx: mb * 1e6 * 0.85, connects: Math.round(mb * 3), ips: Math.max(1, Math.round(mb / 40)), apps: apps.map((n, i) => ({ name: n, bytes: mb * 1e6 / (i + 1.5) })), domains });
        return { from: now() - 86400, home: 'GE', geoAvailable: true, geoStatus: null, unknownBytes: 42e6, lanBytes: 120e6, countries: [
          C('US', 2400, ['chrome.exe', 'Code.exe', 'Teams.exe'], ['www.youtube.com', 'github.com', 'api.openai.com']),
          C('DE', 860, ['chrome.exe', 'Telegram.exe'], ['web.telegram.org', 'hetzner.com']),
          C('NL', 520, ['msedge.exe', 'svchost.exe'], ['login.microsoftonline.com', 'update.microsoft.com']),
          C('IE', 310, ['OneDrive.exe'], ['onedrive.live.com']),
          C('GE', 190, ['chrome.exe'], ['myhome.ge', 'tbcbank.ge']),
          C('SG', 95, ['Discord.exe'], ['discord.gg']),
          C('FI', 80, ['chrome.exe'], ['googlevideo.com']),
          C('TR', 60, ['WhatsApp.Root.exe'], ['web.whatsapp.com']),
          C('GB', 54, ['Spotify.exe'], ['spotify.com']),
          C('FR', 31, ['chrome.exe'], ['ovh.net']),
          C('JP', 12, ['steam.exe'], ['steamcontent.com']),
          C('UA', 9, ['Telegram.exe'], ['rozetka.com.ua']),
          C('BR', 4, ['chrome.exe'], ['globo.com']),
          C('AU', 2, ['chrome.exe'], ['abc.net.au']),
          C('IN', 1.5, ['chrome.exe'], ['akamai.net']),
          C('CA', 1, ['chrome.exe'], ['cbc.ca']),
        ], flows: [
          { app: 'chrome.exe', dest: 'rr3---sn-4g5e6nsz.googlevideo.com', ip: '173.194.182.8', country: 'US', tx: 22e6, rx: 1.4e9, connects: 120 },
          { app: 'chrome.exe', dest: 'web.telegram.org', ip: '149.154.167.99', country: 'DE', tx: 41e6, rx: 380e6, connects: 60 },
          { app: 'Telegram.exe', dest: '149.154.167.51', ip: '149.154.167.51', country: 'DE', tx: 96e6, rx: 230e6, connects: 14 },
          { app: 'OneDrive.exe', dest: 'onedrive.live.com', ip: '13.107.42.13', country: 'IE', tx: 180e6, rx: 21e6, connects: 40 },
          { app: 'Code.exe', dest: 'api.github.com', ip: '140.82.121.6', country: 'US', tx: 4.1e6, rx: 18e6, connects: 30 },
          { app: 'Code.exe', dest: 'marketplace.visualstudio.com', ip: '13.107.6.175', country: 'NL', tx: 0.6e6, rx: 40e6, connects: 8 },
          { app: 'msedge.exe', dest: 'login.microsoftonline.com', ip: '20.190.159.4', country: 'NL', tx: 1.2e6, rx: 3e6, connects: 22 },
          { app: 'svchost.exe', dest: 'download.windowsupdate.com', ip: '2.21.20.40', country: 'NL', tx: 0.4e6, rx: 198e6, connects: 6 },
          { app: 'WhatsApp.Root.exe', dest: 'web.whatsapp.com', ip: '157.240.9.53', country: 'TR', tx: 9e6, rx: 38e6, connects: 18 },
          { app: 'Discord.exe', dest: 'gateway.discord.gg', ip: '162.159.135.234', country: 'SG', tx: 3e6, rx: 60e6, connects: 9 },
          { app: 'chrome.exe', dest: 'cdn.taboola.com', ip: '151.101.1.44', country: 'IL', tx: 0.18e6, rx: 0.3e6, connects: 8 },
        ] };
      }
      case '/api/health': return healthMock();
      case '/api/report/templates': return TEMPLATES;
      case '/api/report/doc': { const t = u.searchParams.get('template') ?? 'health'; return { template: t, title: t, html: docHtml(t) }; }
      case '/api/report/schedules': return schedules;
      case '/api/report/folder': return true;
      case '/api/report/export': return { ok: true, message: 'Сохранено', path: 'C:\\Users\\ANTRARES\\Documents\\SysWatch\\Отчёт SysWatch 2026-10-16 — 2026-10-23 10-40.html' };
      case '/api/why': { const f = Number(u.searchParams.get('from')); return whyMock(f, Number(u.searchParams.get('to')), 'none'); }
      case '/api/system/startup': return [
        { id: 'a', name: 'Telegram Desktop', where: 'Для этого пользователя', command: '"C:\\Users\\ANTRARES\\AppData\\Roaming\\Telegram Desktop\\Telegram.exe" -autostart', path: 'C:\\Telegram.exe', enabled: true, signature: 'signed', signer: 'Telegram FZ-LLC', canToggle: true },
        { id: 'b', name: 'Sideloadly', where: 'Для этого пользователя', command: '"C:\\Users\\ANTRARES\\AppData\\Local\\Sideloadly\\sideloadlydaemon.exe"', path: 'C:\\sideloadlydaemon.exe', enabled: true, signature: 'unsigned', signer: null, canToggle: true },
        { id: 'c', name: 'SecurityHealth', where: 'Для всех пользователей', command: '%windir%\\system32\\SecurityHealthSystray.exe', path: 'C:\\Windows\\system32\\SecurityHealthSystray.exe', enabled: true, signature: 'system', signer: 'Microsoft Windows', canToggle: true },
        { id: 'd', name: 'Docker Desktop', where: 'Для этого пользователя', command: 'C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe -Autostart', path: 'C:\\Docker Desktop.exe', enabled: false, signature: 'signed', signer: 'Docker Inc', canToggle: true },
      ];
      case '/api/system/disks': return [
        { name: 'Samsung SSD 980 PRO 500GB', media: 'SSD', bus: 'NVMe', size: 500 * GB, health: 'healthy', wear: 7, temperature: 41, temperatureMax: 68, powerOnHours: 6120, readErrors: 0, writeErrors: 0, startStops: 2100 },
        { name: 'WDC WD10SPZX-80Z10T2', media: 'HDD', bus: 'SATA', size: 1000 * GB, health: 'warning', wear: null, temperature: 36, temperatureMax: 52, powerOnHours: 18233, readErrors: 14, writeErrors: 0, startStops: 9811 },
      ];
      case '/api/system/info': return { ts: now() - 20, sections: [
        { id: 'computer', title: 'Компьютер', note: null, tables: [], items: [
          { label: 'Производитель', value: 'LENOVO', tone: null }, { label: 'Модель', value: 'Legion 5 15IMH05 (81Y6)', tone: null },
          { label: 'Тип', value: 'ноутбук', tone: null }, { label: 'Серийный номер', value: 'PF2A1B3C', tone: null },
          { label: 'Материнская плата', value: 'LENOVO LNVNB161216', tone: null }, { label: 'BIOS', value: 'LENOVO EFCN54WW', tone: null },
          { label: 'Дата BIOS', value: '12 марта 2023', tone: null }, { label: 'Режим загрузки', value: 'UEFI', tone: null },
          { label: 'Имя компьютера', value: 'ANTRARES-PC', tone: null }, { label: 'Рабочая группа', value: 'WORKGROUP', tone: null } ] },
        { id: 'windows', title: 'Windows', note: null, tables: [], items: [
          { label: 'Система', value: 'Windows 11 Pro', tone: null }, { label: 'Версия', value: '24H2', tone: null }, { label: 'Сборка', value: '26100.6899', tone: null },
          { label: 'Разрядность', value: '64-разрядная', tone: null }, { label: 'Установлена', value: '14 января 2025', tone: null },
          { label: 'Последняя загрузка', value: '24 октября 2026, 00:12', tone: null }, { label: 'Работает без перезагрузки', value: '11 ч 13 мин', tone: null },
          { label: 'Язык системы', value: 'English (United States)', tone: null }, { label: 'Часовой пояс', value: '(UTC+04:00) Tbilisi', tone: null },
          { label: 'Схема питания', value: 'Сбалансированная', tone: null } ] },
        { id: 'security', title: 'Безопасность', note: null, tables: [], items: [
          { label: 'Secure Boot', value: 'включён', tone: 'ok' }, { label: 'TPM', value: 'есть, версия 2.0', tone: 'ok' },
          { label: 'BitLocker C:', value: 'не зашифрован', tone: null }, { label: 'Защитник Windows', value: 'работает', tone: 'ok' },
          { label: 'Базы Защитника', value: '24 октября 2026, 08:41', tone: null }, { label: 'Контроль учётных записей (UAC)', value: 'включён', tone: 'ok' } ] },
        { id: 'cpu', title: 'Процессор', note: null, tables: [], items: [
          { label: 'Модель', value: 'Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz', tone: null }, { label: 'Ядер / потоков', value: '6 / 12', tone: null },
          { label: 'Базовая частота', value: '2.59 GHz', tone: null }, { label: 'Кэш L2 / L3', value: '1,5 МБ / 12 МБ', tone: null },
          { label: 'Виртуализация', value: 'используется (Hyper-V / WSL / Docker)', tone: null } ] },
        { id: 'account', title: 'Учётная запись', note: null, items: [
          { label: 'Пользователь', value: 'ANTRARES-PC\\Yurii', tone: null }, { label: 'Вход', value: 'учётная запись Microsoft: yurii@outlook.com', tone: null },
          { label: 'Администратор', value: 'да', tone: null }, { label: 'Папка пользователя', value: 'C:\\Users\\ANTRARES', tone: null } ],
          tables: [{ title: 'Пользователи этого компьютера', columns: ['Имя', 'Полное имя', 'Состояние', ''], rows: [['Yurii', 'Yurii', 'активна', ''], ['Administrator', '', 'отключена', ''], ['Guest', '', 'отключена', 'без пароля']] }] },
        { id: 'memory', title: 'Оперативная память', note: null, items: [
          { label: 'Всего', value: '15,8 ГБ', tone: null }, { label: 'Слотов занято', value: '2 / 2', tone: null }, { label: 'Максимум по плате', value: '32 ГБ', tone: null } ],
          tables: [{ title: null, columns: ['Слот', 'Объём', 'Тип', 'Скорость', 'Производитель', 'Модель'], rows: [['DIMM 0', '8 ГБ', 'DDR4', '2933 MT/s', 'Samsung', 'M471A1K43DB1-CWE'], ['DIMM 1', '8 ГБ', 'DDR4', '2933 MT/s', 'Samsung', 'M471A1K43DB1-CWE']] }] },
        { id: 'gpu', title: 'Видеокарты', note: null, items: [], tables: [{ title: null, columns: ['Модель', 'Видеопамять', 'Драйвер', 'Дата драйвера', 'Режим'], rows: [
          ['NVIDIA GeForce GTX 1650 with Max-Q Design', '4 ГБ', '32.0.15.6094', '14 августа 2024', ''], ['Intel(R) UHD Graphics', '1 ГБ', '31.0.101.2125', '9 мая 2023', '1920×1080, 144 Hz']] }] },
        { id: 'displays', title: 'Экраны', note: null, items: [], tables: [
          { title: null, columns: ['Модель', 'Производитель', 'Диагональ', 'Подключение', 'Год'], rows: [['', 'BOE', '15.6"', 'встроенный экран', '2020'], ['DELL U2719D', 'DEL', '27"', 'HDMI', '2021']] },
          { title: 'Режимы', columns: ['Экран', 'Разрешение', 'Частота', ''], rows: [['DISPLAY1', '1920×1080', '144 Hz', 'основной'], ['DISPLAY2', '2560×1440', '60 Hz', '']] }] },
        { id: 'ports', title: 'Порты и USB', note: 'Список разъёмов есть не у всех ноутбуков: многие производители не заполняют его в BIOS.', items: [
          { label: 'Контроллеры USB', value: 'Intel(R) USB 3.1 eXtensible Host Controller – 1.10 (Microsoft)', tone: null }, { label: 'Thunderbolt / USB4', value: 'не найден', tone: null } ],
          tables: [{ title: 'Подключено сейчас по USB', columns: ['Устройство', 'Класс'], rows: [['Focusrite USB Audio', 'MEDIA'], ['Logitech USB Receiver', 'HIDClass'], ['Integrated Camera', 'Camera'], ['Intel(R) Wireless Bluetooth(R)', 'Bluetooth']] }] },
      ] };
      case '/api/system/restore': return { available: true, note: null, points: [
        { sequence: 41, description: 'Windows Modules Installer', ts: now() - 3 * 86400, type: 'установка программы' },
        { sequence: 40, description: 'Scheduled Checkpoint', ts: now() - 10 * 86400, type: 'изменение настроек' } ] };
      case '/api/system/restore/create': return { ok: false, message: 'Windows не создала новую точку: по умолчанию она делает не больше одной точки в сутки, а последняя была недавно.' };
      case '/api/system/battery': return { present: true, charge: 76, onAc: false, designCapacity: 76000, fullCapacity: 61200, cycles: 312, dischargeRate: 14200, chargeRate: null, wearPct: 19, minutesLeft: 196 };
      case '/api/system/wakes': return { events: [
        { ts: now() - 3600 * 7, sleepTs: now() - 3600 * 9, source: 'Таймер: Windows Update Orchestrator', sourceType: 'таймер пробуждения' },
        { ts: now() - 3600 * 20, sleepTs: now() - 3600 * 22, source: 'Устройство: HID-совместимая мышь', sourceType: 'устройство' },
        { ts: now() - 3600 * 31, sleepTs: now() - 3600 * 38, source: 'Кнопка питания', sourceType: 'кнопка питания' },
      ], armedDevices: ['HID-совместимая мышь', 'Клавиатура HID', 'Intel(R) Wi-Fi 6 AX201 160MHz'], timers: ['Таймер, установленный [SERVICE] \\Device\\HarddiskVolume3\\Windows\\System32\\svchost.exe (SystemEventsBroker)'] };
      case '/api/system/cpu': return { name: 'Intel(R) Core(TM) i7-1165G7 @ 2.80GHz', cores: 4, threads: 8, baseMhz: 2803, currentMhz: 1500 + 2500 * noise(now()), performancePct: 90, limitPct: noise(now() / 10) > 0.7 ? 64 : 100, throttled: noise(now() / 10) > 0.7, thermal: [{ name: 'THRM', celsius: 64 }] };
      case '/api/firewall': return [{ path: 'C:\\Users\\ANTRARES\\AppData\\Local\\Sideloadly\\sideloadlydaemon.exe', since: now() - 3600 }];
      case '/api/firewall/status': return false;
      case '/api/programs': return programs();
      case '/api/disk/tree': return diskTree(u.searchParams.get('path'));
      case '/api/disk/growth': return diskGrowth();
      case '/api/disk/list': return folderList(u.searchParams.get('path') ?? '');
      case '/api/disk/locate': { const f = u.searchParams.get('path') ?? ''; const d = f.slice(0, f.lastIndexOf('\\')); return { path: TREE[d] ? d : (TREE[d.slice(0, d.lastIndexOf('\\'))] ? d.slice(0, d.lastIndexOf('\\')) : 'C:\\Users\\ANTRARES') }; }
      case '/api/disk/reveal': return { ok: true, message: 'Открыто.' };
      case '/api/search': if (req.method === 'POST') { searchAt = now(); return { started: true }; } return searchState();
      case '/api/search/cancel': return true;
      case '/api/recycle': return recycle();
      case '/api/recycle/empty': { const n = recycleItems.length; recycleItems = []; return { ok: true, message: 'Корзина очищена, освобождено 10,2 ГБ.', done: n, failed: 0 }; }
      case '/api/recycle/delete': case '/api/recycle/restore': { const n = recycleItems.length; recycleItems = recycleItems.slice(1); return { ok: true, message: u.pathname.endsWith('restore') ? 'Восстановлено: 1.' : 'Удалено навсегда: 1.', done: n - recycleItems.length, failed: 0 }; }
      case '/api/anomalies/ignore': return true;
      default: return null;
    }
  })();
  if (!body) { res.statusCode = 404; res.end('not found'); return; }
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify(body));
}
