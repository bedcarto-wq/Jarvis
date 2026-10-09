using Jarvis.Core.Abstractions;
using NAudio.CoreAudioApi;

namespace Jarvis.Platform;

/// <summary>Громкость устройства вывода по умолчанию (Core Audio).</summary>
public sealed class VolumeService : IVolumeService
{
    private static MMDevice Device()
    {
        using var en = new MMDeviceEnumerator();
        return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public int GetVolume()
    {
        using var d = Device();
        return (int)Math.Round(d.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
    }

    public void SetVolume(int percent)
    {
        using var d = Device();
        d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        if (percent > 0 && d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
    }

    public void ChangeVolume(int deltaPercent) => SetVolume(GetVolume() + deltaPercent);

    public void SetMute(bool mute)
    {
        using var d = Device();
        d.AudioEndpointVolume.Mute = mute;
    }

    public bool IsMuted()
    {
        using var d = Device();
        return d.AudioEndpointVolume.Mute;
    }
}
