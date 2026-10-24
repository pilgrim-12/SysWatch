# Архитектура

## Компоненты

```
┌──────────────────────────────── SysWatchAgent (служба Windows, LocalSystem) ───────────────────────────────┐
│  KernelEtwCollector ─ запуск/завершение процессов, TCP/UDP-трафик по процессам (ETW ядра)                  │
│  DnsEtwCollector ──── какой процесс какой домен разрешил → кэш IP → домен                                  │
│  ProcessSampler ───── процессор, память, диск по процессам раз в 5 с (Win32)                               │
│  GpuSampler ───────── счётчики «GPU Engine», видеопамять, температура (D3DKMT / nvidia-smi)                │
│  Aggregator → Flusher ─ раз в минуту: свёртки в SQLite одной транзакцией + замер своей стоимости           │
│  SqliteStore ──────── файл на день, срок хранения, «прореживание» старых дней                              │
└──────────────────────────────────────────────┬─────────────────────────────────────────────────────────────┘
                                               │ C:\ProgramData\SysWatch\hot\YYYY-MM-DD.db (только чтение)
┌──────────────────────────────── SysWatch.exe (WinForms + WebView2, пользователь) ──────────────────────────┐
│  TrayContext ─ иконка в трее, оповещения, суточный снимок диска, поиск аномалий                            │
│  MainForm ──── WebView2 → https://syswatch.local/  (статические файлы из wwwroot)                          │
│  Api ───────── запросы /api/* перехватываются в C# — HTTP-сервера и открытых портов нет                    │
│  LiveSampler ─ живые цифры раз в секунду, только пока окно открыто (процессы, сеть, диск, видеокарты)      │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Почему два процесса: ETW ядра требует прав администратора и должен работать всегда, а окну права администратора не нужны и в памяти оно живёт только пока открыто.

### Агент (`src/SysWatch.Agent`)

- Сырые события на диск не пишутся — только минутные агрегаты. Сетевой путь ETW без выделений памяти.
- Процесс идентифицируется парой (PID, время создания): Windows переиспользует PID.
- Фоновый режим: низкий приоритет процессора и ввода-вывода, EcoQoS на Windows 11.
- Стоимость самого агента пишется в `self_1m` и видна в разделе «Сейчас». Цель — меньше 0,5% процессора и 50 МБ памяти.

### Окно (`src/SysWatch.App`)

| Папка | Что внутри |
|---|---|
| `Data` | запросы к дневным файлам: обзор, таймлайн, сеть, страны (`GeoTraffic`), видеокарта |
| `Sampling` | живые замеры (`LiveSampler`, `GpuLive`), соединения, GeoIP, действия с процессами |
| `Disk` | снимок диска (`DiskScanner`, `DiskStore`), очистка, поиск файлов, файлы в папке, Корзина |
| `Anomalies` | правила (`Detector`) и хранилище (`AnomalyStore`) |
| `Reports` | оценка здоровья (`Health`), отчёты, PDF через WebView2 |
| `Sys` | сведения о компьютере, сбои, автозагрузка, точки восстановления |
| `Programs` | установленные программы, winget |
| `Browsing` | история браузеров (копия базы, только чтение) |
| `Dev` | SDK, Docker, `node_modules` |
| `Settings` | `settings.json`, оповещения |

### Интерфейс (`src/ui`)

React 18 + TypeScript + Vite, графики — uPlot, карта мира — d3-geo + world-atlas, 3D-диск — three.js (грузится отдельно, только при открытии 3D).

- `src/api.ts` — типы и вызовы; зеркало C#-записей (`Data/Dto.cs` и др.), JSON в camelCase.
- `mock/api.ts` — тестовые данные: `npm run dev` работает в браузере без Windows и агента.
- `src/i18n` — строки в коде на русском, `t('…')` / `tp(n, …)`; словари `en/es/fr.json` с ключом-русской строкой. Тот же словарь использует C# (`L.T`). Тест `i18n-coverage` падает, если строка не переведена.
- Сборка кладётся в `src/SysWatch.App/wwwroot` и коммитится: собрать установщик можно без Node.js.

## Данные

| Файл | Что |
|---|---|
| `hot\YYYY-MM-DD.db` | данные агента за день |
| `disk.db` | снимки диска |
| `anomalies.db` | найденные аномалии |
| `settings.json` | срок хранения, пороги оповещений (читает и агент) |
| `geo\dbip-country-lite.csv.gz` | база «IP → страна», если скачана |

Таблицы дневного файла:

| Таблица | Что внутри |
|---|---|
| `process` | pid, время создания, имя, путь, командная строка, родитель, время выхода |
| `proc_1m` | по минутам: процессор (мс), пик частной памяти, чтение/запись диска, сеть ↑/↓ |
| `conn_1m` | по минутам: процесс → IP:порт (+ домен), байты ↑/↓, число подключений |
| `dns` | DNS-запросы: процесс, домен, ответы |
| `system_1m` | общая загрузка процессора и памяти |
| `gpu_1m` | видеокарта: загрузка по движкам, память, температура, мощность, вентилятор |
| `gpu_proc_1m` | загрузка видеокарты и видеопамять по процессам |
| `self_1m` | стоимость самого агента |

Хранение: `RetentionDays` (по умолчанию 90) — сколько дней хранить файлы; `DetailDays` (30) — после этого из файла удаляются `conn_1m`, `dns`, `gpu_proc_1m` и командные строки, файл сжимается (`PRAGMA user_version = 1` — признак прореживания).

Пример: кто больше всех скачал сегодня.

```sql
SELECT p.image, SUM(m.net_rx)/1048576.0 AS mb_in, SUM(m.net_tx)/1048576.0 AS mb_out
FROM proc_1m m JOIN process p ON p.id = m.proc_id
GROUP BY p.image ORDER BY mb_in DESC LIMIT 10;
```

## Настройки агента

`src/SysWatch.Agent/appsettings.json`, секция `Agent`:

| Параметр | По умолчанию | |
|---|---|---|
| `DataDir` | `C:\ProgramData\SysWatch\hot` | где дневные файлы |
| `SampleIntervalSeconds` | 5 | опрос процессора, памяти, диска |
| `FlushIntervalSeconds` | 60 | запись на диск |
| `BackgroundMode` | true | низкий приоритет |
| `EcoQoS` | true | энергоэффективные ядра (Windows 11) |
| `StoreCommandLines` | true | хранить командные строки |
| `RetentionDays` | 90 | сколько дней хранить |
| `DetailDays` | 30 | сколько дней хранить сетевые подробности |

Срок хранения из окна («Настройки») записывается в `settings.json` и важнее `appsettings.json`.

## API окна

Все запросы — `https://syswatch.local/api/...` внутри WebView2; параметр `lang` выбирает язык текстов, которые формирует C#.

| Группа | Пути |
|---|---|
| Живое | `live`, `connections` |
| История | `overview`, `timeline`, `network`, `app`, `events`, `gpu`, `geo`, `geo/home`, `geo/download` |
| Аномалии | `anomalies`, `anomalies/seen`, `anomalies/ignore` |
| Диск | `disk`, `disk/tree`, `disk/growth`, `disk/scan`, `disk/cancel`, `disk/cleanup-scan`, `disk/clean`, `disk/list`, `disk/locate`, `disk/reveal` |
| Поиск и Корзина | `search` (GET состояние / POST запуск), `search/cancel`, `recycle`, `recycle/restore`, `recycle/delete`, `recycle/empty` |
| Система | `system/info`, `system/restore`, `system/restore/create`, `system/startup`, `system/startup/toggle`, `system/disks`, `system/battery`, `system/wakes`, `system/crashes`, `system/cpu` |
| Программы | `programs`, `programs/uninstall`, `updates`, `updates/install`, `process/kill`, `process/priority`, `process/reveal` |
| Сеть | `firewall`, `firewall/status`, `firewall/block`, `firewall/unblock` |
| Прочее | `history*`, `dev*`, `report/*`, `settings`, `settings/lang` |

История читается под одним семафором (`_history`): кэши запросов простые и не потокобезопасные.

## Сборка и выпуск

- `scripts/build-installer.ps1` — проверяет `wwwroot`, публикует агент и окно (self-contained, ReadyToRun), собирает Inno Setup (`installer/SysWatch.iss`), пишет `.sha256`.
- `.github/workflows/build.yml` — на каждый push: проверка типов и тесты интерфейса, сборка установщика. На тег `v*`: аттестация сборки (`actions/attest-build-provenance`) и релиз с установщиком и контрольной суммой.
- Версия — одна, в `Directory.Build.props`; интерфейс читает её оттуда при сборке.
