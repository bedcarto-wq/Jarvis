using System.Reflection;
using System.Runtime.InteropServices;

namespace Jarvis.Voice.Recognition;

/// <summary>
/// Привязки к CMU PocketSphinx 5 (BSD-лицензия, классическая HMM/GMM-модель, не нейросеть).
/// Библиотека pocketsphinx.dll собирается в CI из исходников и кладётся рядом с JARVIS.exe.
/// </summary>
internal static unsafe partial class PocketSphinxNative
{
    public const string Lib = "pocketsphinx";
    private static int _resolverSet;

    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(PocketSphinxNative).Assembly, Resolve);
        }
        catch (InvalidOperationException) { /* уже установлен */ }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("JARVIS_POCKETSPHINX_LIB");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "pocketsphinx.dll"));
        candidates.Add(Path.Combine(baseDir, "native", "pocketsphinx.dll"));
        candidates.Add(Path.Combine(baseDir, "libpocketsphinx.so"));
        foreach (var c in candidates)
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
        return NativeLibrary.TryLoad(name, assembly, path, out var handle) ? handle : IntPtr.Zero;
    }

    [LibraryImport(Lib)] public static partial IntPtr ps_config_init(IntPtr defn);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr ps_config_set_str(IntPtr config, string name, string? val);
    [LibraryImport(Lib)] public static partial int ps_config_free(IntPtr config);
    [LibraryImport(Lib)] public static partial IntPtr ps_init(IntPtr config);
    [LibraryImport(Lib)] public static partial int ps_free(IntPtr ps);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int ps_add_word(IntPtr ps, string word, string phones, int update);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr ps_lookup_word(IntPtr ps, string word);
    [LibraryImport(Lib)] public static partial void ckd_free(IntPtr ptr);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int ps_add_jsgf_string(IntPtr ps, string name, string jsgf);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int ps_activate_search(IntPtr ps, string? name);
    [LibraryImport(Lib)] public static partial int ps_start_utt(IntPtr ps);
    [LibraryImport(Lib)] public static partial int ps_process_raw(IntPtr ps, short* data, nuint nSamples, int noSearch, int fullUtt);
    [LibraryImport(Lib)] public static partial int ps_end_utt(IntPtr ps);
    [LibraryImport(Lib)] public static partial IntPtr ps_get_hyp(IntPtr ps, out int score);
    [LibraryImport(Lib)] public static partial int ps_get_prob(IntPtr ps);
    [LibraryImport(Lib)] public static partial int ps_get_n_frames(IntPtr ps);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr err_set_loglevel_str(string level);
}
