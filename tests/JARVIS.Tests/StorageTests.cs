using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Storage;
using Jarvis.Core.Vision;
using Jarvis.Templates;

namespace Jarvis.Tests;

public class StorageTests
{
    [Fact]
    public void Settings_SaveAndLoad_RoundTrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var s = new SettingsService(path);
        s.Load();
        s.Update(x => { x.StopWord = "хватит"; x.WakePhrases = ["компьютер"]; x.StopReaction = StopReaction.Pause; x.Autostart = false; });

        var s2 = new SettingsService(path);
        var loaded = s2.Load();
        Assert.Equal(LoadStatus.Ok, s2.LastLoadStatus);
        Assert.Equal("хватит", loaded.StopWord);
        Assert.Equal(["компьютер"], loaded.WakePhrases);
        Assert.Equal(StopReaction.Pause, loaded.StopReaction);
        Assert.False(loaded.Autostart);
        Assert.Contains("хватит", File.ReadAllText(path)); // кириллица читаема, не экранирована
    }

    [Fact]
    public void Settings_DefaultStopWordAndWakePhrases()
    {
        var s = new JarvisSettings();
        Assert.Equal("жёпа", s.StopWord);
        Assert.Contains("окей джарвис", s.WakePhrases);
        Assert.All(s.DangerousOperations, d => Assert.True(d.RequireConfirmation));
        Assert.Equal(Enum.GetValues<DangerCategory>().Length - 1, s.DangerousOperations.Count);
    }

    [Fact]
    public void Settings_CorruptFile_IsBackedUpAndDefaultsLoaded()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{ это не json ");
        var s = new SettingsService(path, Path.Combine(dir.Path, "corrupt"));
        var loaded = s.Load();
        Assert.Equal(LoadStatus.Corrupt, s.LastLoadStatus);
        Assert.Equal("жёпа", loaded.StopWord);
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "corrupt")));
        Assert.Contains("повреждён", s.LastLoadMessage);
        // Новый корректный файл записан.
        Assert.Equal(LoadStatus.Ok, new SettingsService(path).Let(x => { x.Load(); return x.LastLoadStatus; }));
    }

    [Fact]
    public void Settings_Normalize_ClampsUnsafeValues()
    {
        var s = new JarvisSettings { MaxRetriesCap = 1000, DefaultRetries = 500, MaxRepeatCount = 0, StopWord = " ", WakePhrases = ["", "Джарвис", "джарвис"] }.Normalize();
        Assert.Equal(10, s.MaxRetriesCap);
        Assert.Equal(10, s.DefaultRetries);
        Assert.Equal(1, s.MaxRepeatCount);
        Assert.Equal("жёпа", s.StopWord);
        Assert.Single(s.WakePhrases);
    }

    [Fact]
    public void Templates_SaveLoad_Export_Import()
    {
        using var dir = new TempDir();
        var store = new TemplateStore(Path.Combine(dir.Path, "templates"));
        var t = new Template
        {
            Name = "Работа",
            VoicePhrases = ["Джарвис, работа"],
            Steps =
            [
                Steps.Of(StepKind.LaunchApp, (P.App, "Google Chrome")),
                new TemplateStep
                {
                    Kind = StepKind.If,
                    Condition = new StepCondition { Kind = ConditionKind.WindowExists, Parameters = { [P.App] = "Telegram" } },
                    Then = [Steps.Of(StepKind.SwitchToWindow, (P.App, "Telegram"))],
                    Else = [Steps.Of(StepKind.LaunchApp, (P.App, "Telegram"))],
                },
            ],
        };
        store.Save(t);

        var store2 = new TemplateStore(Path.Combine(dir.Path, "templates"));
        Assert.Empty(store2.Load());
        var loaded = store2.FindByName("работа")!;
        Assert.Equal(t.Id, loaded.Id);
        Assert.Equal(2, loaded.Steps.Count);
        Assert.Equal(ConditionKind.WindowExists, loaded.Steps[1].Condition!.Kind);
        Assert.Single(loaded.Steps[1].Else);

        var export = Path.Combine(dir.Path, "export.json");
        store2.Export([loaded], export);
        var imported = store2.Import(export);
        Assert.Single(imported);
        Assert.NotEqual(t.Id, imported[0].Id);
        Assert.Equal("Работа (импорт)", imported[0].Name);
        Assert.Equal(2, store2.All.Count);
    }

    [Fact]
    public void Templates_CorruptFile_IsSkippedAndReported()
    {
        using var dir = new TempDir();
        var tdir = Path.Combine(dir.Path, "templates");
        var store = new TemplateStore(tdir, Path.Combine(dir.Path, "corrupt"));
        store.Save(new Template { Name = "Хороший", Steps = [Steps.Say("привет")] });
        File.WriteAllText(Path.Combine(tdir, "broken.json"), "{\"schemaVersion\":1,\"data\":{\"steps\":[{\"kind\":\"НетТакого\"}]}}");
        File.WriteAllText(Path.Combine(tdir, "garbage.json"), "\u0000\u0001 мусор");

        var s2 = new TemplateStore(tdir, Path.Combine(dir.Path, "corrupt"));
        var messages = s2.Load();
        Assert.Equal(2, messages.Count);
        Assert.Single(s2.All);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dir.Path, "corrupt")).Length);
    }

    [Fact]
    public void Templates_ImportRejectsInvalidFile()
    {
        using var dir = new TempDir();
        var store = new TemplateStore(Path.Combine(dir.Path, "t"));
        var f = Path.Combine(dir.Path, "bad.json");
        File.WriteAllText(f, "[1,2,3]");
        Assert.Throws<InvalidDataException>(() => store.Import(f));
    }

    [Fact]
    public void Templates_Duplicate_CreatesIndependentCopy()
    {
        using var dir = new TempDir();
        var store = new TemplateStore(Path.Combine(dir.Path, "t"));
        var t = new Template { Name = "Утро", VoicePhrases = ["утро"], Steps = [Steps.Say("a")] };
        store.Save(t);
        var copy = store.Duplicate(t.Id);
        Assert.Equal("Утро (копия)", copy.Name);
        Assert.Empty(copy.VoicePhrases);
        Assert.NotEqual(t.Steps[0].Id, copy.Steps[0].Id);
    }

    [Fact]
    public void Anchors_SaveLoadRemove()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "anchors.json");
        var store = new AnchorStore(path);
        var a = new ElementAnchor { Label = "Кнопка отправить", ElementName = "Send", RelativeX = 0.9, RelativeY = 0.95 };
        store.Upsert(a);
        var s2 = new AnchorStore(path);
        s2.Load();
        Assert.Equal("Send", s2.FindByLabel("кнопка отправить")!.ElementName);
        Assert.True(s2.Remove(a.Id));
        Assert.Empty(s2.All);
    }
}

internal static class FuncExt
{
    public static TR Let<T, TR>(this T value, Func<T, TR> f) => f(value);
}
