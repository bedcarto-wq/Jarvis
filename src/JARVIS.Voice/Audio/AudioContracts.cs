namespace Jarvis.Voice.Audio;

public sealed record AudioDevice(int Number, string Name);

/// <summary>Источник звука: 16 кГц, моно, 16 бит. Звук нигде не сохраняется.</summary>
public interface IAudioSource : IDisposable
{
    const int SampleRate = 16000;
    event Action<short[], int>? SamplesAvailable;
    event Action<string>? Error;
    bool IsRunning { get; }
    IReadOnlyList<AudioDevice> GetDevices();
    void Start(int deviceNumber);
    void Stop();
}

public interface IEarcons
{
    void ListenStart();
    void CommandDone();
    void Error();
}
