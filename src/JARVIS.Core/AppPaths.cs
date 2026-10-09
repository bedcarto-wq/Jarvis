namespace Jarvis.Core;

/// <summary>
/// Расположение локальных данных JARVIS. По умолчанию %LOCALAPPDATA%\JARVIS\.
/// Корень можно переопределить (тесты, портативный режим).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JARVIS");
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string AppsFile => Path.Combine(Root, "apps.json");
    public string AnchorsFile => Path.Combine(Root, "anchors.json");
    public string TemplatesDir => Path.Combine(Root, "templates");
    public string CorruptDir => Path.Combine(Root, "corrupt");
    public string LogsDir => Path.Combine(Root, "logs");
    public string ModelsDir => Path.Combine(Root, "models");
    public string TessdataDir => Path.Combine(Root, "tessdata");
    public string ProfilesDir => Path.Combine(Root, "profiles");
    public string ReportsDir => Path.Combine(Root, "reports");
    public string ExportsDir => Path.Combine(Root, "exports");
    public string BenchmarkAudioDir => Path.Combine(Root, "benchmark-audio");
    public string ScreenshotsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "JARVIS");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(TemplatesDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(TessdataDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(ReportsDir);
        Directory.CreateDirectory(ExportsDir);
    }
}
