<#
.SYNOPSIS
  Собирает нативную библиотеку PocketSphinx 5.0.4 (BSD-2) для Windows x64
  и кладёт pocketsphinx.dll в native\win-x64\ (её подхватывает JARVIS.App.csproj).
  Требуется: CMake и Visual Studio Build Tools (C/C++). На GitHub windows-runner всё есть.
#>
param(
    [string]$Version = "5.0.4",
    [string]$WorkDir = "$PSScriptRoot\..\artifacts\pocketsphinx-src",
    [string]$OutDir  = "$PSScriptRoot\..\native\win-x64"
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

New-Item -ItemType Directory -Force -Path $WorkDir, $OutDir | Out-Null
$zip = Join-Path $WorkDir "pocketsphinx-$Version.zip"
$src = Join-Path $WorkDir "pocketsphinx-$Version"

if (-not (Test-Path $src)) {
    Write-Host "Скачивание PocketSphinx $Version..."
    Invoke-WebRequest -Uri "https://github.com/cmusphinx/pocketsphinx/archive/refs/tags/v$Version.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $WorkDir -Force
}

$build = Join-Path $src "build-win-x64"
Write-Host "CMake configure..."
cmake -S $src -B $build -A x64 -DBUILD_SHARED_LIBS=ON -DCMAKE_BUILD_TYPE=Release
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)" }

Write-Host "CMake build..."
cmake --build $build --config Release --target pocketsphinx --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($LASTEXITCODE)" }

$dll = Get-ChildItem -Path $build -Recurse -Filter "pocketsphinx.dll" | Select-Object -First 1
if (-not $dll) { throw "pocketsphinx.dll не найдена после сборки" }
Copy-Item $dll.FullName -Destination (Join-Path $OutDir "pocketsphinx.dll") -Force
Write-Host "Готово: $(Join-Path $OutDir 'pocketsphinx.dll') ($([math]::Round($dll.Length/1KB)) КБ)"
