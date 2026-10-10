<#
.SYNOPSIS
  Скачивает локальные модели для JARVIS:
   - модель распознавания речи GigaAM v3 (Сбер, MIT, ~225 МБ) — движок по умолчанию;
   - модель распознавания речи Vosk для русского языка (vosk-model-small-ru-0.22, ~45 МБ, лёгкая нейросеть);
   - (по ключу -WithPocketSphinx) акустическую модель CMUSphinx (cmusphinx-ru-5.2, GMM-HMM);
   - классические (legacy) языковые данные Tesseract rus + eng для OCR.
  После загрузки интернет для работы JARVIS не нужен.
.PARAMETER Root
  Папка данных JARVIS (по умолчанию %LOCALAPPDATA%\JARVIS).
.PARAMETER KeepLanguageModel
  Не удалять большой статистический файл ru.lm (JARVIS использует только грамматику JSGF).
#>
param(
    [string]$Root = (Join-Path $env:LOCALAPPDATA "JARVIS"),
    [switch]$KeepLanguageModel,
    [switch]$WithPocketSphinx,
    [switch]$SkipOcr
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$models = Join-Path $Root "models"
$tess   = Join-Path $Root "tessdata"
New-Item -ItemType Directory -Force -Path $models, $tess | Out-Null

$gigaDir = Join-Path $models "gigaam-v3-ctc"
$gigaBase = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16/resolve/main"
New-Item -ItemType Directory -Force -Path $gigaDir | Out-Null
foreach ($f in @("tokens.txt", "LICENSE", "model.int8.onnx")) {
    $target = Join-Path $gigaDir $f
    if ((Test-Path $target) -and ((Get-Item $target).Length -gt 0)) { continue }
    if ($f -eq "model.int8.onnx") { Write-Host "Скачивание модели GigaAM v3 (~225 МБ, несколько минут)..." }
    $tmp = "$target.part"
    Invoke-WebRequest -Uri "$gigaBase/$f" -OutFile $tmp -MaximumRedirection 10
    Move-Item -Force $tmp $target
}
if ((Get-Item (Join-Path $gigaDir "model.int8.onnx")).Length -lt 100MB) { throw "Файл модели GigaAM скачан не полностью — запустите скрипт ещё раз" }
Write-Host "Модель GigaAM v3 установлена: $gigaDir"

$voskName = "vosk-model-small-ru-0.22"
$voskDir = Join-Path $models $voskName
if (Test-Path (Join-Path $voskDir "am\final.mdl")) {
    Write-Host "Модель Vosk уже установлена: $voskDir"
} else {
    $zip = Join-Path $env:TEMP "$voskName.zip"
    Write-Host "Скачивание модели распознавания речи Vosk (~45 МБ)..."
    Invoke-WebRequest -Uri "https://alphacephei.com/vosk/models/$voskName.zip" -OutFile $zip -MaximumRedirection 10
    Write-Host "Распаковка..."
    Expand-Archive -Path $zip -DestinationPath $models -Force
    Remove-Item $zip -Force
    if (-not (Test-Path (Join-Path $voskDir "am\final.mdl"))) { throw "Архив Vosk распакован, но модель не найдена в $voskDir" }
    Write-Host "Модель установлена: $voskDir"
}

$modelDir = Join-Path $models "cmusphinx-ru-5.2"
if (-not $WithPocketSphinx) {
    Write-Host "PocketSphinx пропущен (нужен только при выборе этого движка; добавьте -WithPocketSphinx)."
} elseif (Test-Path (Join-Path $modelDir "mdef")) {
    Write-Host "Акустическая модель уже установлена: $modelDir"
} else {
    $tgz = Join-Path $env:TEMP "cmusphinx-ru-5.2.tar.gz"
    $url = "https://downloads.sourceforge.net/project/cmusphinx/Acoustic%20and%20Language%20Models/Russian/cmusphinx-ru-5.2.tar.gz"
    Write-Host "Скачивание акустической модели (архив несколько сотен МБ, распакованная модель без ru.lm ≈ 85 МБ)..."
    Invoke-WebRequest -Uri $url -OutFile $tgz -UserAgent "Wget" -MaximumRedirection 10
    Write-Host "Распаковка..."
    tar -xzf $tgz -C $models
    if ($LASTEXITCODE -ne 0) { throw "Не удалось распаковать $tgz (нужен tar.exe, есть в Windows 10 1803+)" }
    Remove-Item $tgz -Force
    if (-not $KeepLanguageModel) {
        $lm = Join-Path $modelDir "ru.lm"
        if (Test-Path $lm) { Remove-Item $lm -Force; Write-Host "Удалён ru.lm (не нужен для командной грамматики)" }
    }
    Write-Host "Модель установлена: $modelDir"
}

if (-not $SkipOcr) {
    foreach ($lang in @("rus", "eng")) {
        $file = Join-Path $tess "$lang.traineddata"
        if (Test-Path $file) { Write-Host "OCR $lang уже есть"; continue }
        Write-Host "Скачивание Tesseract $lang (legacy-совместимая версия)..."
        Invoke-WebRequest -Uri "https://github.com/tesseract-ocr/tessdata/raw/main/$lang.traineddata" -OutFile $file
    }
}
Write-Host ""
Write-Host "Готово. Запустите JARVIS.exe — раздел «Главная» покажет состояние компонентов."
