<#
.SYNOPSIS
  Скачивает локальные (не нейросетевые) модели для JARVIS:
   - акустическую модель CMUSphinx для русского языка (cmusphinx-ru-5.2, GMM-HMM);
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
    [switch]$SkipOcr
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$models = Join-Path $Root "models"
$tess   = Join-Path $Root "tessdata"
New-Item -ItemType Directory -Force -Path $models, $tess | Out-Null

$modelDir = Join-Path $models "cmusphinx-ru-5.2"
if (Test-Path (Join-Path $modelDir "mdef")) {
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
