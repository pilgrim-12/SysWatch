import { t } from './i18n';

/** Что это за папка простыми словами и можно ли её чистить. */
export interface PathInfo { label: string; hint: string; kind: 'update' | 'cache' | 'temp' | 'system' | 'dev' | 'app' | 'user' | 'game' }

type Rule = { re: RegExp; kind: PathInfo['kind']; label: (v: string) => string; hint: () => string };

/** Номер версии в последнем сегменте пути: …\Application\156.0.4314.42 */
const versionOf = (p: string) => /\\(\d+(?:\.\d+){2,})\\?$/.exec(p)?.[1] ?? '';
const withVer = (s: string, v: string) => (v ? `${s} ${v}` : s);

// Порядок важен: более точные правила раньше общих.
const RULES: Rule[] = [
  { re: /\\microsoft\\edgewebview\\application\\\d/, kind: 'update', label: v => withVer(t('Обновление WebView2'), v),
    hint: () => t('Встроенный Edge, которым рисуют окна Teams, Outlook, виджеты и SysWatch. Старая версия удалится сама после перезапуска программ.') },
  { re: /\\microsoft\\edge\\application\\\d/, kind: 'update', label: v => withVer(t('Обновление Microsoft Edge'), v),
    hint: () => t('Edge поставил новую версию рядом со старой. Старая удалится сама после перезапуска браузера или компьютера.') },
  { re: /\\microsoft\\edgecore\\\d/, kind: 'update', label: v => withVer(t('Общие файлы Edge и WebView2'), v),
    hint: () => t('Часть обновления Edge. Старые версии удаляются автоматически.') },
  { re: /\\microsoft\\edgeupdate/, kind: 'update', label: () => t('Служба обновления Edge'), hint: () => t('Скачанные обновления Edge. Удалять не нужно.') },
  { re: /\\google\\chrome\\application\\\d/, kind: 'update', label: v => withVer(t('Обновление Google Chrome'), v),
    hint: () => t('Новая версия Chrome. Старая удалится после перезапуска браузера.') },
  { re: /\\windows\\softwaredistribution/, kind: 'update', label: () => t('Загрузки обновлений Windows'),
    hint: () => t('Скачанные обновления Windows. Можно очистить: «Очистка диска» → «Очистить системные файлы».') },
  { re: /\\windows\\winsxs/, kind: 'system', label: () => t('Хранилище компонентов Windows'),
    hint: () => t('Системные файлы Windows. Вручную не удалять; уменьшается через «Очистку диска».') },
  { re: /\\windows\\installer/, kind: 'system', label: () => t('Установщики программ (MSI)'),
    hint: () => t('Нужны для удаления и исправления программ. Вручную не удалять.') },
  { re: /\\windows\.old/, kind: 'system', label: () => t('Предыдущая версия Windows'),
    hint: () => t('Остаётся после большого обновления на 10 дней для отката. Можно удалить через «Очистку диска».') },
  { re: /\\system volume information/, kind: 'system', label: () => t('Точки восстановления Windows'),
    hint: () => t('Размер настраивается в «Защите системы». Вручную не удалять.') },
  { re: /\\\$recycle\.bin/, kind: 'temp', label: () => t('Корзина'), hint: () => t('Удалённые файлы. Освобождается очисткой корзины.') },
  { re: /\\(pagefile|swapfile)\.sys$/, kind: 'system', label: () => t('Файл подкачки'), hint: () => t('Windows выгружает сюда память. Размер меняет сама.') },
  { re: /\\hiberfil\.sys$/, kind: 'system', label: () => t('Файл гибернации'), hint: () => t('Нужен для гибернации и быстрого запуска. Отключается командой powercfg /h off.') },
  { re: /\\(windows\\temp|appdata\\local\\temp)(\\|$)/, kind: 'temp', label: () => t('Временные файлы'),
    hint: () => t('Можно очистить — в разделе «Можно очистить» ниже.') },
  { re: /\\(crashdumps|minidump)(\\|$)|\\memory\.dmp$/, kind: 'temp', label: () => t('Дампы сбоев'),
    hint: () => t('Снимки памяти упавших программ. Можно удалить, если не отправляете их разработчикам.') },
  { re: /\\microsoft\\windows\\wer(\\|$)/, kind: 'temp', label: () => t('Отчёты об ошибках Windows'),
    hint: () => t('Сведения о сбоях программ для Microsoft. Можно очистить через «Очистку диска».') },
  { re: /\\\.ollama\\models/, kind: 'dev', label: () => t('Модели Ollama'), hint: () => t('Языковые модели. Ненужные удаляются командой ollama rm <имя>.') },
  { re: /\\appdata\\local\\microsoft\\windows\\inetcache/, kind: 'cache', label: () => t('Кэш интернета Windows'), hint: () => t('Можно очистить через «Очистку диска».') },
  { re: /\\windows\\logs/, kind: 'system', label: () => t('Журналы Windows'), hint: () => t('Обычно небольшие. Большой размер — признак повторяющихся ошибок.') },
  { re: /\\programdata\\syswatch/, kind: 'app', label: () => t('История SysWatch'), hint: () => t('Срок хранения меняется в «Настройках».') },
  { re: /\\programdata\\package cache/, kind: 'cache', label: () => t('Кэш установщиков (Visual Studio, .NET)'),
    hint: () => t('Нужен для обновления и удаления Visual Studio и .NET. Вручную лучше не трогать.') },
  { re: /\\nvidia( corporation)?\\installer2/, kind: 'cache', label: () => t('Установщики драйверов NVIDIA'),
    hint: () => t('Старые установщики драйверов. Можно удалить папки старых версий.') },
  { re: /\\windowsapps(\\|$)/, kind: 'app', label: () => t('Приложения Microsoft Store'), hint: () => t('Удаляются через «Параметры» → «Приложения».') },
  { re: /\\google\\chrome\\user data\\.*\\(cache|code cache|gpucache|service worker)/, kind: 'cache', label: () => t('Кэш Google Chrome'),
    hint: () => t('Можно очистить в настройках Chrome: «Удалить данные о работе в браузере» → «Кэш».') },
  { re: /\\google\\chrome\\user data/, kind: 'app', label: () => t('Профиль Google Chrome'), hint: () => t('История, кэш, расширения, вход на сайты.') },
  { re: /\\microsoft\\edge\\user data\\.*\\(cache|code cache|gpucache|service worker)/, kind: 'cache', label: () => t('Кэш Microsoft Edge'),
    hint: () => t('Можно очистить в настройках Edge: «Удалить данные браузера» → «Кэш».') },
  { re: /\\microsoft\\edge\\user data/, kind: 'app', label: () => t('Профиль Microsoft Edge'), hint: () => t('История, кэш, расширения, вход на сайты.') },
  { re: /\\mozilla\\firefox\\profiles/, kind: 'app', label: () => t('Профиль Firefox'), hint: () => t('История, кэш, расширения, вход на сайты.') },
  { re: /\\(docker_data\.vhdx|docker\\wsl|appdata\\local\\docker)/, kind: 'dev', label: () => t('Диск Docker'),
    hint: () => t('Образы и контейнеры Docker. Чистится командой docker system prune (раздел «Разработка» → Docker).') },
  { re: /\\ext4\.vhdx$|\\packages\\canonicalgroup/, kind: 'dev', label: () => t('Диск WSL (Linux)'), hint: () => t('Файлы Linux в WSL. Сам не уменьшается после удаления файлов внутри.') },
  { re: /\\\.nuget\\packages/, kind: 'dev', label: () => t('Пакеты NuGet (.NET)'), hint: () => t('Кэш пакетов .NET. Можно очистить: dotnet nuget locals all --clear.') },
  { re: /\\npm-cache/, kind: 'dev', label: () => t('Кэш npm'), hint: () => t('Можно очистить: npm cache clean --force.') },
  { re: /\\node_modules(\\|$)/, kind: 'dev', label: () => t('Зависимости Node.js (node_modules)'), hint: () => t('Восстанавливаются командой npm install. Старые проекты можно чистить в «Разработке».') },
  { re: /\\pip\\cache/, kind: 'dev', label: () => t('Кэш pip (Python)'), hint: () => t('Можно очистить: pip cache purge.') },
  { re: /\\\.gradle(\\|$)/, kind: 'dev', label: () => t('Кэш Gradle'), hint: () => t('Сборки Android и Java. Скачается заново при необходимости.') },
  { re: /\\\.m2\\repository/, kind: 'dev', label: () => t('Пакеты Maven'), hint: () => t('Скачается заново при необходимости.') },
  { re: /\\\.android\\avd/, kind: 'dev', label: () => t('Эмуляторы Android'), hint: () => t('Ненужные удаляются в Android Studio → Device Manager.') },
  { re: /\\android\\sdk/, kind: 'dev', label: () => t('Android SDK'), hint: () => t('Лишние версии удаляются в Android Studio → SDK Manager.') },
  { re: /\\\.vscode\\extensions/, kind: 'dev', label: () => t('Расширения VS Code'), hint: () => t('Старые версии расширений VS Code удаляет сам.') },
  { re: /\\microsoft visual studio/, kind: 'dev', label: () => t('Visual Studio'), hint: () => t('Компоненты меняются в Visual Studio Installer.') },
  { re: /\\dotnet(\\|$)/, kind: 'dev', label: () => t('.NET SDK и среды'), hint: () => t('Старые версии видны в разделе «Разработка».') },
  { re: /\\jetbrains/, kind: 'dev', label: () => t('JetBrains (IDE и кэш)'), hint: () => t('Старые версии IDE и кэш индексов.') },
  { re: /\\steamapps/, kind: 'game', label: () => t('Игры Steam'), hint: () => t('Удаляются в Steam.') },
  { re: /\\epic games/, kind: 'game', label: () => t('Игры Epic Games'), hint: () => t('Удаляются в Epic Games Launcher.') },
  { re: /\\(microsoft\\teams|packages\\msteams)/, kind: 'cache', label: () => t('Microsoft Teams'), hint: () => t('Кэш и файлы Teams.') },
  { re: /\\telegram desktop/, kind: 'app', label: () => t('Telegram: загрузки и кэш'), hint: () => t('Чистится в Telegram: Настройки → Продвинутые → Управление памятью.') },
  { re: /\\onedrive(\\|$| -)/, kind: 'user', label: () => t('OneDrive'), hint: () => t('Файлы, скачанные из облака. Можно освободить место через «Освободить место» в меню OneDrive.') },
  { re: /\\users\\[^\\]+\\downloads(\\|$)/, kind: 'user', label: () => t('Загрузки'), hint: () => t('Ваши скачанные файлы.') },
];

export function describePath(path: string | null | undefined): PathInfo | null {
  if (!path) return null;
  const p = path.toLowerCase();
  for (const r of RULES) if (r.re.test(p)) return { label: r.label(versionOf(path)), hint: r.hint(), kind: r.kind };
  return null;
}
