import { t, lang } from './i18n';
import { aiAnonymize, aiService, type AiService } from './prefs';

/**
 * «Спросить ИИ»: SysWatch сам ничего не отправляет. Собираем понятный вопрос, показываем его,
 * и по кнопке открываем браузер с готовым запросом (или копируем текст) — отправляет человек.
 */
export const AI_LABEL: Record<AiService, string> = {
  claude: 'Claude', chatgpt: 'ChatGPT', deepseek: 'DeepSeek', qwen: 'Qwen', perplexity: 'Perplexity', google: 'Google', copy: '',
};

const URLS: Record<Exclude<AiService, 'copy'>, (q: string) => string> = {
  claude: q => `https://claude.ai/new?q=${encodeURIComponent(q)}`,
  chatgpt: q => `https://chatgpt.com/?q=${encodeURIComponent(q)}`,
  // DeepSeek и Qwen не принимают вопрос в ссылке: открываем чат, а вопрос уже в буфере обмена — Ctrl+V.
  deepseek: () => 'https://chat.deepseek.com/',
  qwen: () => 'https://chat.qwen.ai/',
  perplexity: q => `https://www.perplexity.ai/search?q=${encodeURIComponent(q)}`,
  google: q => `https://www.google.com/search?q=${encodeURIComponent(q)}`,
};

/** Имя пользователя и компьютера в путях меняем на нейтральные: в чужой сервис они не нужны. */
export function anonymize(text: string) {
  const users = new Set<string>();
  for (const m of text.matchAll(/\\Users\\([^\\"'\s]+)/gi)) if (!/^(public|default|all users)$/i.test(m[1])) users.add(m[1]);
  let out = text.replace(/([A-Z]:)?\\Users\\([^\\"'\s]+)/gi, (all, _d, u) => (/^(public|default|all users)$/i.test(u) ? all : '%USERPROFILE%'));
  for (const u of users) out = out.replace(new RegExp(`\\b${u.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'gi'), '<user>');
  // ДОМЕН\пользователь
  out = out.replace(/\b[A-Z0-9-]{2,15}\\(<user>)/g, '<pc>\\$1');
  return out;
}

const LANG_NAME: Record<string, string> = { ru: 'на русском', en: 'in English', es: 'en español', fr: 'en français' };

/** Вопрос целиком: что случилось, подробности и что хотим услышать. */
export function buildQuestion(title: string, details: string[]) {
  const body = [
    t('Мой монитор Windows (SysWatch) показал предупреждение: «{0}».', title),
    '',
    ...details.filter(Boolean),
    '',
    t('Объясни простыми словами: это нормально или опасно? Что это за программа или настройка и что мне сделать? Ответь {0}.', LANG_NAME[lang()] ?? LANG_NAME.ru),
  ].join('\n');
  return aiAnonymize() ? anonymize(body) : body;
}

export function copyText(text: string): boolean {
  try {
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    const ok = document.execCommand('copy');
    ta.remove();
    if (!ok) navigator.clipboard?.writeText(text).catch(() => {});
    return true;
  } catch { return false; }
}

/** Сервисы, которые сами подставляют вопрос из ссылки. Остальным вопрос вставляют из буфера обмена. */
export const PREFILLS: ReadonlySet<AiService> = new Set<AiService>(['claude', 'chatgpt', 'perplexity', 'google']);

/**
 * Открыть вопрос в выбранном сервисе. Вопрос всегда копируется в буфер обмена — на случай, если сервис
 * не подставит его сам. Для Google — только заголовок: длинный текст поиску мешает.
 */
export function ask(question: string, service: AiService = aiService(), shortQuery?: string) {
  copyText(question);
  if (service === 'copy') return true;
  const q = service === 'google' ? (shortQuery ?? question.split('\n')[0]) : question;
  window.open(URLS[service](q.slice(0, 6000)), '_blank');
  return true;
}
