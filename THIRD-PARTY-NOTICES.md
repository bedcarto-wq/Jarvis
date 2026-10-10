# Сторонние компоненты

| Компонент | Назначение | Лицензия | Как попадает в сборку |
|---|---|---|---|
| [Vosk API 0.3.38](https://github.com/alphacep/vosk-api) (NuGet `Vosk`) | Распознавание речи (Kaldi), движок по умолчанию | Apache-2.0 | NuGet-пакет, `libvosk.dll` рядом с программой |
| [vosk-model-small-ru-0.22](https://alphacephei.com/vosk/models) | Лёгкая модель распознавания русской речи (~45 МБ) | Apache-2.0 | Скачивается пользователем (`scripts/download-models.ps1`), в репозитории не хранится |
| [PocketSphinx 5.0.4](https://github.com/cmusphinx/pocketsphinx) (CMU Sphinx) | Распознавание речи (GMM-HMM, не нейросеть) | BSD-2-Clause | Собирается из исходников в CI (`scripts/build-pocketsphinx.ps1`), `pocketsphinx.dll` |
| [cmusphinx-ru-5.2](https://sourceforge.net/projects/cmusphinx/files/Acoustic%20and%20Language%20Models/Russian/) | Акустическая модель и словарь русского языка | BSD-подобная лицензия CMU Sphinx (см. README в архиве модели) | Скачивается пользователем (`scripts/download-models.ps1`), в репозитории не хранится |
| [NAudio 2.2.1](https://github.com/naudio/NAudio) | Захват микрофона, управление громкостью | MIT | NuGet |
| [Tesseract (.NET wrapper) 5.2.0](https://github.com/charlesw/tesseract) + Tesseract OCR / Leptonica | OCR (классический движок) | Apache-2.0 (Tesseract, обёртка), BSD-2 (Leptonica) | NuGet; нативные DLL в папке `x64` |
| [tessdata](https://github.com/tesseract-ocr/tessdata) rus/eng | Языковые данные OCR | Apache-2.0 | Скачиваются пользователем |
| System.Speech 10.0.0 | Доступ к Windows SAPI | MIT (.NET) | NuGet |
| .NET Runtime / WPF / Windows Forms | Платформа | MIT | Self-contained публикация |

Нейросетевые модели и облачные сервисы не используются.
