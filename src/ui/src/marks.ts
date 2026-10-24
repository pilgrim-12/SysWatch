/** Отметки найденных файлов на карте диска: какая плитка или здание содержит файл. */
const norm = (p: string) => p.toLowerCase().replace(/\\+$/, '');

/** Лежит ли путь m внутри папки p (или это она сама). */
export function under(m: string, p: string) {
  const a = norm(m), b = norm(p);
  return a === b || a.startsWith(b + '\\');
}

export const countUnder = (marks: readonly string[], p: string) => marks.reduce((n, m) => n + (under(m, p) ? 1 : 0), 0);

/** Сколько отметок внутри base, но не попало ни в одну крупную плитку — они в «мелком». */
export const countRest = (marks: readonly string[], base: string, paths: readonly string[]) =>
  marks.filter(m => under(m, base) && !paths.some(p => under(m, p))).length;
