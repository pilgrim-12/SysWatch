import { describePath } from '../pathLabel';
import { t } from '../i18n';

/** Путь с понятной подписью: «Обновление Microsoft Edge 156.0…» крупно, сам путь — мелко под ней. */
export function PathName({ path, isNew }: { path: string; isNew?: boolean }) {
  const info = describePath(path);
  if (!info) return <span className="grow-path" title={path}>{path}{isNew && <span className="tag new">{t('новая')}</span>}</span>;
  return (
    <span className="path-name" title={`${info.hint}\n${path}`}>
      <span className="path-label">
        <b>{info.label}</b>
        {isNew && <span className="tag new">{t('новая')}</span>}
        <span className={`path-kind k-${info.kind}`}>{KIND[info.kind]()}</span>
      </span>
      <span className="path-sub">{path}</span>
      <span className="path-hint">{info.hint}</span>
    </span>
  );
}

const KIND: Record<string, () => string> = {
  update: () => t('обновление'), cache: () => t('кэш'), temp: () => t('можно чистить'), system: () => t('система'),
  dev: () => t('разработка'), app: () => t('программа'), user: () => t('ваши файлы'), game: () => t('игры'),
};
