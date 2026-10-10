using System.Runtime.InteropServices;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Прямые привязки к libvosk с явной передачей строк в UTF-8.
/// Обёртка из NuGet-пакета Vosk передаёт строки в ANSI-кодировке Windows (cp1251):
/// русский словарь команд превращался в «слова вне словаря», и распознаватель всегда возвращал пустоту.
/// Пакет используется только как поставщик нативной библиотеки libvosk.dll.
/// </summary>
internal static partial class VoskNative
{
    private const string Lib = "libvosk";

    [LibraryImport(Lib, EntryPoint = "vosk_set_log_level")]
    internal static partial void SetLogLevel(int level);

    [LibraryImport(Lib, EntryPoint = "vosk_model_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr ModelNew(string path);

    [LibraryImport(Lib, EntryPoint = "vosk_model_free")]
    internal static partial void ModelFree(IntPtr model);

    [LibraryImport(Lib, EntryPoint = "vosk_recognizer_new")]
    internal static partial IntPtr RecognizerNew(IntPtr model, float sampleRate);

    [LibraryImport(Lib, EntryPoint = "vosk_recognizer_new_grm", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr RecognizerNewGrm(IntPtr model, float sampleRate, string grammar);

    [LibraryImport(Lib, EntryPoint = "vosk_recognizer_set_words")]
    internal static partial void RecognizerSetWords(IntPtr rec, int words);

    [LibraryImport(Lib, EntryPoint = "vosk_recognizer_accept_waveform_s")]
    internal static unsafe partial int RecognizerAcceptWaveformS(IntPtr rec, short* data, int length);

    [LibraryImport(Lib, EntryPoint = "vosk_recognizer_final_result")]
    internal static partial IntPtr RecognizerFinalResult(IntPtr rec);

    [LibraryImport(Lib, EntryPoint = "vosk_recognizer_reset")]
    internal static partial void RecognizerReset(IntPtr rec);

    [LibraryImport(Lib, EntryPoint = "vosk_recognizer_free")]
    internal static partial void RecognizerFree(IntPtr rec);

    /// <summary>Строка результата принадлежит распознавателю и действительна до следующего вызова.</summary>
    internal static string PtrToUtf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";
}
