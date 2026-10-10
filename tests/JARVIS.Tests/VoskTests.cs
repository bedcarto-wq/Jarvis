using Jarvis.Core.Apps;
using Jarvis.Core.Settings;
using Jarvis.Voice.Recognition;
using Xunit;

namespace Jarvis.Tests;

public class VoskTests
{
    [Fact]
    public void Parse_DropsUnk_AndAveragesConfidence()
    {
        var json = """{"result":[{"conf":1.0,"word":"джарвис"},{"conf":0.5,"word":"[unk]"},{"conf":0.8,"word":"открой"}],"text":"джарвис [unk] открой"}""";
        var (text, conf) = VoskEngine.Parse(json);
        Assert.Equal("джарвис открой", text);
        Assert.Equal(0.9, conf, 3);
    }

    [Fact]
    public void Parse_EmptyOrOnlyUnk_GivesEmptyText()
    {
        Assert.Equal("", VoskEngine.Parse("""{"text":""}""").Text);
        Assert.Equal("", VoskEngine.Parse("""{"result":[{"conf":1,"word":"[unk]"}],"text":"[unk]"}""").Text);
    }

    [Fact]
    public void BuildPhrases_HasWakeStopCommandsAndUnk_AndYoAliases()
    {
        var spec = VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"]);
        var (phrases, aliases) = VoskEngine.BuildPhrases(spec);
        Assert.Contains("джарвис", phrases);
        Assert.Contains("[unk]", phrases);
        Assert.Contains("рабочий режим", phrases);
        Assert.Contains(phrases, p => p.StartsWith("открой "));
        // «жёпа» нет в словаре модели — добавляются варианты с «е» и «о», которые потом переводятся обратно.
        Assert.Contains("жопа", phrases);
        Assert.Equal("жёпа", aliases["жопа"]);
        Assert.Equal("жёпа", VoskEngine.Parse("""{"result":[{"conf":1,"word":"жопа"}]}""", aliases).Text);
        Assert.DoesNotContain(phrases, p => p.Contains('ё'));
        Assert.Equal(phrases.Count, phrases.Distinct().Count());
    }

    [Fact]
    public void Strictness_Monotonic()
    {
        Assert.Equal(0, VoskEngine.MinConfidence(0));
        Assert.True(VoskEngine.MinConfidence(1) > VoskEngine.MinConfidence(0.5));
    }

    [Fact]
    public void ValidateModel_ReportsMissingFolder()
    {
        Assert.NotNull(VoskEngine.ValidateModel(Path.Combine(Path.GetTempPath(), "нет-такой-модели-" + Guid.NewGuid())));
    }

    [Fact]
    public void Settings_OldPocketSphinxDefault_MigratesToVosk_Once()
    {
        var old = new JarvisSettings { SpeechEngine = SpeechEngineKind.PocketSphinx, SpeechEngineRevision = null }.Normalize();
        Assert.Equal(SpeechEngineKind.Vosk, old.SpeechEngine);
        var chosen = new JarvisSettings { SpeechEngine = SpeechEngineKind.PocketSphinx, SpeechEngineRevision = 2 }.Normalize();
        Assert.Equal(SpeechEngineKind.PocketSphinx, chosen.SpeechEngine);
    }
}

/// <summary>
/// Интеграционный тест с настоящей моделью Vosk. Выполняется, если задана JARVIS_VOSK_MODEL.
/// Если задана JARVIS_VOSK_SAMPLES — проверяет распознавание пар *.raw (16 кГц, 16 бит, моно) + *.txt.
/// </summary>
public class VoskIntegrationTests
{
    [Fact]
    public void RealModel_LoadsGrammar_RejectsSilence_AndRecognizesSamples()
    {
        var model = Environment.GetEnvironmentVariable("JARVIS_VOSK_MODEL");
        if (string.IsNullOrEmpty(model) || !Directory.Exists(model)) return;
        using var engine = new VoskEngine(model);
        var spec = VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"]);
        engine.Initialize(spec);
        Assert.True(engine.IsReady, engine.Status);
        Assert.Null(engine.Recognize(new short[16000]));

        var samples = Environment.GetEnvironmentVariable("JARVIS_VOSK_SAMPLES");
        if (string.IsNullOrEmpty(samples) || !Directory.Exists(samples)) return;
        foreach (var raw in Directory.GetFiles(samples, "*.raw"))
        {
            var txt = Path.ChangeExtension(raw, ".txt");
            if (!File.Exists(txt)) continue;
            var bytes = File.ReadAllBytes(raw);
            var pcm = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
            var expected = File.ReadAllText(txt).Trim();
            var r = engine.Recognize(pcm);
            // Пустой *.txt — посторонний разговор: в нём не должно появиться фразы активации.
            if (expected.Length == 0) Assert.True(r is null || !r.Text.Contains("джарвис"), r?.Text);
            else Assert.Equal(expected, r?.Text);
        }
    }
}
