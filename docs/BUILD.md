# Сборка

## Требования

* Windows 10/11 x64 (для запуска и полной сборки), .NET SDK 10.0.x.
* Для нативной `pocketsphinx.dll`: CMake ≥ 3.14 и Visual Studio 2022 Build Tools (C/C++).
* Платформенно-независимые проекты и тесты собираются и на Linux/macOS
  (WPF-проекты собираются на Linux благодаря `EnableWindowsTargeting`, но запускаются только в Windows).

## Команды

```powershell
# 1. Нативная библиотека распознавания (один раз) → native\win-x64\pocketsphinx.dll
./scripts/build-pocketsphinx.ps1

# 2. Модели (один раз) → %LOCALAPPDATA%\JARVIS\models и \tessdata
./scripts/download-models.ps1

# 3. Сборка и тесты
dotnet build JARVIS.sln -c Release
dotnet test  JARVIS.sln -c Release

# Интеграционный тест настоящего декодера (иначе он пропускается):
$env:JARVIS_POCKETSPHINX_LIB = "$PWD\native\win-x64\pocketsphinx.dll"
$env:JARVIS_PS_MODEL = "$env:LOCALAPPDATA\JARVIS\models\cmusphinx-ru-5.2"
dotnet test JARVIS.sln -c Release

# 4. Публикация без установки .NET
dotnet publish src/JARVIS.App/JARVIS.App.csproj -c Release -r win-x64 --self-contained true -o publish\JARVIS

# 5. Самопроверка собранного приложения
publish\JARVIS\JARVIS.exe --selftest --desktop --report selftest.txt `
    --model "$env:LOCALAPPDATA\JARVIS\models\cmusphinx-ru-5.2" --tessdata "$env:LOCALAPPDATA\JARVIS\tessdata"
```

`--desktop` запускает Блокнот и управляет им (открыть, UIA, свернуть/развернуть, закрыть) —
не трогайте мышь и клавиатуру несколько секунд.

## Иконка

Иконка хранится как текст `src/JARVIS.App/Assets/jarvis.ico.b64`; файл `jarvis.ico`
создаётся автоматически перед сборкой (MSBuild-задача в `JARVIS.App.csproj`).

## GitHub Actions

`.github/workflows/build.yml` (раннер `windows-latest`):
сборка PocketSphinx → загрузка моделей (кэшируется) → restore → build Release → тесты
(включая декодер на Windows) → self-contained publish → упаковка `JARVIS-Windows-x64.zip` →
`JARVIS.exe --selftest --desktop`. Артефакты: `JARVIS-Windows-x64`, `test-reports`
(TRX + отчёт самопроверки), при ошибке — `diagnostics` (binlog, логи CMake).
