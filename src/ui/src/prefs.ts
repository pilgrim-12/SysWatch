/** Настройки интерфейса этого окна: тема и помощник ИИ. Хранятся в браузере окна, на компьютер не влияют. */
export type Theme = 'dark' | 'light' | 'system';
export type AiService = 'claude' | 'chatgpt' | 'deepseek' | 'qwen' | 'perplexity' | 'google' | 'copy';

function read<T extends string>(key: string, fallback: T, allowed: readonly T[]): T {
  try { const v = localStorage.getItem(key) as T | null; return v && allowed.includes(v) ? v : fallback; } catch { return fallback; }
}
function write(key: string, v: string) {
  try { localStorage.setItem(key, v); } catch { /* не страшно */ }
}

export const THEMES = ['dark', 'light', 'system'] as const;
export const AI_SERVICES = ['claude', 'chatgpt', 'deepseek', 'qwen', 'perplexity', 'google', 'copy'] as const;

export const theme = (): Theme => read('theme', 'dark', THEMES);
export const setTheme = (t: Theme) => { write('theme', t); applyTheme(); };

export const aiService = (): AiService => read('ai_service', 'claude', AI_SERVICES);
export const setAiService = (s: AiService) => write('ai_service', s);

export const aiAnonymize = (): boolean => read('ai_anon', '1', ['0', '1']) === '1';
export const setAiAnonymize = (on: boolean) => write('ai_anon', on ? '1' : '0');

/** Какая тема действует сейчас: «как в Windows» смотрит на системную настройку. */
export function effectiveTheme(): 'dark' | 'light' {
  const t = theme();
  if (t !== 'system') return t;
  try { return matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'; } catch { return 'dark'; }
}

export function applyTheme() {
  document.documentElement.dataset.theme = effectiveTheme();
}

/** Цвет из CSS-переменной темы — для графиков на canvas, которым CSS не указ. */
export function cssVar(name: string, fallback: string) {
  try { return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback; } catch { return fallback; }
}

let watching = false;
export function watchSystemTheme(onChange: () => void) {
  if (watching) return;
  watching = true;
  try {
    matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => { if (theme() === 'system') { applyTheme(); onChange(); } });
  } catch { /* старый движок */ }
}
