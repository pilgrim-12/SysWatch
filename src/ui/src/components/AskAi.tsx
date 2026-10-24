import { useState } from 'react';
import { AI_LABEL, PREFILLS, ask, buildQuestion, copyText } from '../ai';
import { aiService, AI_SERVICES, type AiService } from '../prefs';
import { t } from '../i18n';

/**
 * Кнопка «Спросить ИИ» с предпросмотром: человек видит и может поправить текст, прежде чем отправить его сам.
 * Ничего не уходит, пока не нажата кнопка сервиса.
 */
export function AskAi({ title, details, search }: { title: string; details: string[]; search?: string }) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState('');
  const [note, setNote] = useState<string | null>(null);
  const main = aiService();

  const start = () => { setText(buildQuestion(title, details)); setNote(null); setOpen(o => !o); };
  const send = (s: AiService) => {
    if (s === 'copy') { setNote(copyText(text) ? t('Скопировано — вставьте в любой чат ИИ.') : t('Не удалось скопировать.')); return; }
    ask(text, s, search ?? title);
    setNote(PREFILLS.has(s)
      ? t('Открыто в браузере: {0}. Проверьте вопрос и отправьте.', AI_LABEL[s])
      : t('Открыт {0}, вопрос скопирован — вставьте его в чат (Ctrl+V) и отправьте.', AI_LABEL[s]));
  };

  return (
    <>
      <button className="link ai-link" onClick={start} aria-expanded={open}>
        ✦ {main === 'copy' ? t('Спросить ИИ') : t('Спросить {0}', AI_LABEL[main])}
      </button>
      {open && (
        <div className="ai-box">
          <div className="dim">{t('Этот текст уйдёт в выбранный сервис только по вашей кнопке. Имя пользователя и компьютера заменены — включается в «Настройках».')}</div>
          <textarea className="ai-text" value={text} onChange={e => setText(e.target.value)} rows={Math.min(12, text.split('\n').length + 1)} spellCheck={false} />
          <div className="ai-actions">
            {main !== 'copy' && <button className="btn primary" onClick={() => send(main)}>{t('Открыть в {0}', AI_LABEL[main])}</button>}
            {AI_SERVICES.filter(s => s !== main && s !== 'copy').map(s => (
              <button key={s} className="btn" onClick={() => send(s)}>{AI_LABEL[s]}</button>
            ))}
            <button className="btn" onClick={() => send('copy')}>{t('Копировать')}</button>
            <button className="link dim" onClick={() => setOpen(false)}>{t('Закрыть')}</button>
          </div>
          {note && <div className="note ok">{note}</div>}
        </div>
      )}
    </>
  );
}
