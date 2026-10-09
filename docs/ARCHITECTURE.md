# Архитектура

```
JARVIS.sln
├─ src/JARVIS.Core        — модели и логика без зависимостей от Windows (net10.0)
│    Commands/   разбор русских команд (CommandParser, CommandLexicon, WakePhraseDetector)
│    Apps/       каталог программ, встроенные приложения, нечёткое сопоставление названий
│    Text/       нормализация, транслитерация, числа прописью, нечёткое сравнение
│    Security/   категории опасных действий, политика подтверждений
│    Execution/  StopController (мгновенная остановка), UndoJournal
│    Performance/ каталог параметров, профили (PerformanceProfileService), сравнение,
│                 BenchmarkService (один сеанс), RecommendationService, отчёты, мониторинг,
│                 BackgroundGate, StorageLocations, PerformanceSettingsViewModel
│    Settings/, Storage/ (атомарная запись JSON + карантин повреждённых файлов), Logging/
├─ src/JARVIS.Templates   — модель шаблонов, каталог блоков, валидатор, интерпретатор,
│                            хранилище, голосовой конструктор шаблонов (net10.0)
├─ src/JARVIS.Automation  — CommandRouter (команда → действие), ActionRunner,
│                            AppLaunchCoordinator (запуск + ожидание окна) (net10.0)
├─ src/JARVIS.Voice       — VAD, сессия голоса (активация/окно команды/стоп-слово),
│                            PocketSphinx через P/Invoke, грамматика JSGF, русская G2P (net10.0)
├─ src/JARVIS.Windows     — Win32: окна, SendInput, корзина, громкость, автозапуск,
│                            поиск программ, микрофон (NAudio), SAPI (net10.0-windows)
├─ src/JARVIS.Vision      — UI Automation + Tesseract OCR, поиск элементов (net10.0-windows)
├─ src/JARVIS.App         — WPF: главное окно, HUD, трей, редактор шаблонов,
│                            JarvisController (композиция), самопроверка --selftest
└─ tests/JARVIS.Tests     — xUnit-тесты платформенно-независимой части + интеграция PocketSphinx
```

## Поток голосовой команды

1. `AudioCapture` (NAudio WaveIn, 16 кГц моно) → `VoiceSession`.
2. `EnergyVad` выделяет фразу (порог в дБ, тайм-аут конца речи).
3. `PocketSphinxEngine` декодирует фразу **по ограниченной грамматике JSGF** (активационные фразы,
   команды, названия программ и шаблонов, стоп-слово, «мусорные» слова для отсева).
   Неизвестные слова получают произношение через `RussianG2P` (правила) и транслитерацию.
4. `VoiceSession`: стоп-слово обрабатывается первым в любом состоянии; иначе ищется фраза
   активации, затем текст команды (в той же фразе или в течение «окна команды»).
5. `JarvisController.HandleCommandTextAsync` → `CommandParser` → `CommandRouter` →
   `ActionRunner`/`TemplateExecutor` → сервисы Windows. Тот же путь используется для текстового ввода.
6. Состояние отражается в HUD, трее и журнале; ответы озвучиваются SAPI.

## Шаблоны

Шаблон — это данные (JSON), а не код. `TemplateExecutor` интерпретирует блоки, проверяет
условия, ограничивает число повторов/вложенность/попыток, перед опасными шагами запрашивает
подтверждение через `SecurityPolicy` и в любой момент прерывается `StopController`
(с отпусканием всех зажатых клавиш).

## Поиск элемента интерфейса

`VisionService.LocateAsync`: сохранённый якорь → UI Automation (AutomationId, затем имя
точно/нечётко) → OCR по скриншоту окна → относительные координаты внутри окна → абсолютные.
Найденный элемент активируется паттернами UIA (Invoke/Toggle/SelectionItem), иначе — кликом мыши.

## Данные

`%LOCALAPPDATA%\JARVIS\`: `settings.json`, `apps.json`, `anchors.json`, `templates\*.json`,
`profiles\`, `reports\`, `exports\`, `benchmark-audio\`, `models\` (модель речи), `tessdata\`, `logs\`, `corrupt\` (повреждённые файлы переносятся сюда,
а не удаляются). Скриншоты — `Изображения\JARVIS`.

## Производительность и бенчмарк

Проверки бенчмарка — независимые `IBenchmarkProbe` в своих сборках: `WindowsSystemProbe` (JARVIS.Windows),
`RuntimeSystemProbe`, `SelfLoadProbe`, `StorageProbe` (Core), `VoiceBenchmarkProbe` (Voice), `OcrBenchmarkProbe` (Vision),
`TemplateBenchmarkProbe` (Templates, песочница), `UiaBenchmarkProbe` (App, собственное тестовое окно).
`BenchmarkService` запускает их последовательно в одном сеансе, затем `RecommendationService` анализирует замеры и
сопоставляет с профилями. GUI только показывает результат и вызывает `JarvisController.ApplyProfileAsync` /
`ApplyValuesAsync` по явному выбору пользователя. Подробнее — PERFORMANCE.md, BENCHMARK.md, PROFILES.md.
