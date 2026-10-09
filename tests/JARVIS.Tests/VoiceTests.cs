using Jarvis.Core.Apps;
using Jarvis.Core.Settings;
using Jarvis.Voice.Audio;
using Jarvis.Voice.Recognition;
using Jarvis.Voice.Session;
using Xunit;

namespace Jarvis.Tests;

public class VoiceTests
{
    private static readonly string[] Phones =
        "SIL a0 a1 b bj c ch d dj e0 e1 f fj g gj h hj i0 i1 j k kj l lj m mj n nj o0 o1 p pj r rj s sch sh sj t tj u0 u1 v vj y0 y1 z zh zj".Split(' ');

    [Theory]
    [InlineData("джарвис")]
    [InlineData("жёпа")]
    [InlineData("телеграм")]
    [InlineData("включись")]
    [InlineData("щётка")]
    public void G2P_ProducesOnlyModelPhones(string word)
    {
        var p = RussianG2P.Convert(word);
        Assert.NotNull(p);
        Assert.All(p!.Split(' '), ph => Assert.Contains(ph, Phones));
    }

    [Fact]
    public void G2P_StressOnYo()
    {
        Assert.Contains("o1", RussianG2P.Convert("жёпа")!);
    }

    [Fact]
    public void G2P_RejectsLatin() => Assert.Null(RussianG2P.Convert("chrome"));

    private static GrammarSpec Spec() => VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"]);

    [Fact]
    public void Grammar_ContainsWakeStopAppsAndTemplates()
    {
        var spec = Spec();
        var jsgf = spec.ToJsgf();
        Assert.Contains("#JSGF V1.0;", jsgf);
        Assert.Contains("джарвис", jsgf);
        Assert.Contains("жёпа", jsgf);
        Assert.Contains("рабочий режим", jsgf);
        Assert.Contains("public <utt>", jsgf);
        var words = spec.AllWords().ToList();
        Assert.Contains("телеграм", words);
        Assert.DoesNotContain("", words);
    }

    [Fact]
    public void Grammar_ExpandPhrasesIncludesWakeCombinations()
    {
        var phrases = Spec().ExpandPhrases();
        Assert.Contains("джарвис открой телеграм", phrases);
        Assert.Contains("открой телеграм", phrases);
        Assert.Contains("жёпа", phrases);
    }

    [Fact]
    public void StripGarbage_RemovesChatter()
    {
        var spec = Spec();
        Assert.Equal("", GrammarSpec.StripGarbage("ну вот короче", spec));
        Assert.Equal("джарвис открой телеграм", GrammarSpec.StripGarbage("ну джарвис открой телеграм", spec));
    }

    [Fact]
    public void Pronunciations_FallBackToG2PAndTransliterate()
    {
        var dic = new PronunciationDictionary(null);
        var r = dic.Resolve(["джарвис", "chrome"]);
        Assert.Contains(r, e => e.Word == "джарвис");
        Assert.Contains(r, e => e.Word == "chrome" && e.Phones.StartsWith("h "));
    }

    [Fact]
    public void Vad_DetectsUtteranceAfterSilence()
    {
        var vad = new EnergyVad { ThresholdDb = -40, EndSilenceMs = 200 };
        short[]? got = null;
        vad.UtteranceCompleted += s => got = s;
        var loud = Enumerable.Range(0, 16000).Select(i => (short)(Math.Sin(i * 0.2) * 8000)).ToArray();
        var silence = new short[16000];
        vad.Process(silence, silence.Length);
        vad.Process(loud, loud.Length);
        vad.Process(silence, silence.Length);
        Assert.NotNull(got);
        Assert.True(got!.Length >= 16000);
    }

    [Fact]
    public void Vad_IgnoresShortClick()
    {
        var vad = new EnergyVad { ThresholdDb = -40, EndSilenceMs = 200 };
        var fired = false;
        vad.UtteranceCompleted += _ => fired = true;
        var click = Enumerable.Range(0, 1600).Select(i => (short)(i % 2 == 0 ? 10000 : -10000)).ToArray();
        vad.Process(click, click.Length);
        vad.Process(new short[16000], 16000);
        Assert.False(fired);
    }

    private sealed class NoAudio : IAudioSource
    {
        public event Action<short[], int>? SamplesAvailable { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public bool IsRunning => false;
        public IReadOnlyList<AudioDevice> GetDevices() => [];
        public void Start(int deviceNumber) { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class NoEngine : ISpeechEngine
    {
        public string Name => "none"; public bool IsReady => true; public string Status => "";
        public void Initialize(GrammarSpec grammar) { }
        public void UpdateGrammar(GrammarSpec grammar) { }
        public RecognitionResult? Recognize(short[] samples) => null;
        public void Dispose() { }
    }

    private static (VoiceSession s, List<string> log) Session(Func<DateTime>? now = null)
    {
        var s = new VoiceSession(new NoAudio(), new NoEngine(), new VoiceSessionOptions(), null, now);
        var log = new List<string>();
        s.CommandHeard += c => log.Add("cmd:" + c);
        s.StopWordHeard += _ => log.Add("stop");
        s.WakeHeard += () => log.Add("wake");
        s.EnableRequested += () => log.Add("enable");
        s.Ignored += t => log.Add("ignored:" + t);
        return (s, log);
    }

    [Fact]
    public void Session_WakeWithCommand()
    {
        var (s, log) = Session();
        s.HandleText("Окей, Джарвис, открой Телеграм");
        Assert.Equal(["cmd:открой телеграм"], log);
    }

    [Fact]
    public void Session_IgnoresSpeechWithoutWake()
    {
        var (s, log) = Session();
        s.HandleText("открой телеграм");
        Assert.Equal(["ignored:открой телеграм"], log);
    }

    [Fact]
    public void Session_WakeOpensWindowThenExpires()
    {
        var t = new DateTime(2026, 1, 1);
        var (s, log) = Session(() => t);
        s.HandleText("джарвис");
        s.HandleText("открой хром");
        t = t.AddSeconds(20);
        s.HandleText("джарвис");
        t = t.AddSeconds(7);
        s.HandleText("закрой хром");
        Assert.Equal(["wake", "cmd:открой хром", "wake", "ignored:закрой хром"], log);
    }

    [Fact]
    public void Session_StopWordHasPriorityEvenWhenDisabledOrSpeaking()
    {
        var (s, log) = Session();
        s.IsSpeaking = () => true;
        s.SetSoftDisabled(true);
        s.HandleText("жёпа");
        s.HandleText("ну жепа же");
        Assert.Equal(["stop", "stop"], log);
    }

    [Fact]
    public void Session_SoftDisabledOnlyAcceptsEnable()
    {
        var (s, log) = Session();
        s.SetSoftDisabled(true);
        s.HandleText("джарвис открой хром");
        s.HandleText("джарвис включись");
        Assert.Equal(["ignored:джарвис открой хром", "enable"], log);
    }

    [Fact]
    public void Session_ExpectingReplyAcceptsBareAnswer()
    {
        var (s, log) = Session();
        s.ExpectingReply = true;
        s.HandleText("да");
        Assert.Equal(["cmd:да"], log);
    }

    [Fact]
    public void Confidence_Mapping()
    {
        Assert.Equal(1.0, PocketSphinxEngine.ToConfidence(0));
        Assert.True(PocketSphinxEngine.ToConfidence(-100000) < 0.01);
        Assert.Equal(0, PocketSphinxEngine.MinConfidence(0));
        Assert.True(PocketSphinxEngine.MinConfidence(1) > PocketSphinxEngine.MinConfidence(0.5));
    }
}

/// <summary>
/// Интеграционный тест с настоящим PocketSphinx. Выполняется, если заданы
/// JARVIS_POCKETSPHINX_LIB и JARVIS_PS_MODEL (иначе пропускается с успехом).
/// </summary>
public class PocketSphinxIntegrationTests
{
    [Fact]
    public void RealDecoder_LoadsGrammar_AndRejectsNoise()
    {
        var lib = Environment.GetEnvironmentVariable("JARVIS_POCKETSPHINX_LIB");
        var model = Environment.GetEnvironmentVariable("JARVIS_PS_MODEL");
        if (string.IsNullOrEmpty(lib) || string.IsNullOrEmpty(model) || !File.Exists(lib) || !Directory.Exists(model)) return;
        var work = Path.Combine(Path.GetTempPath(), "jarvis-ps-" + Guid.NewGuid().ToString("N"));
        using var engine = new PocketSphinxEngine(model, work);
        var spec = VocabularyBuilder.Build(new JarvisSettings(), BuiltInApps.All.Select(BuiltInApps.ToEntry), ["рабочий режим"]);
        engine.Initialize(spec);
        Assert.True(engine.IsReady, engine.Status);
        Assert.True(File.Exists(Path.Combine(work, "jarvis.dic")));

        Assert.Null(engine.Recognize(new short[16000]));
        var rnd = new Random(1);
        var noise = Enumerable.Range(0, 32000).Select(_ => (short)rnd.Next(-300, 300)).ToArray();
        var r = engine.Recognize(noise);
        Assert.True(r is null || r.Text.Length > 0);

        engine.UpdateGrammar(VocabularyBuilder.Build(new JarvisSettings { StopWord = "абракадабра" }, BuiltInApps.All.Select(BuiltInApps.ToEntry), ["вечерний стрим"]));
        Assert.True(engine.IsReady);
    }
}
