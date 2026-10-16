using NAudio.Wave;

namespace OsuAi.Desktop.Services;

/// <summary>
/// Basic playback of the project audio: play, pause, stop. Waveform, click track and object sounds
/// belong to the Rhythm Workbench (roadmap M3).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOut? output;
    private AudioFileReader? reader;
    private string? loaded;

    public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan Position => reader?.CurrentTime ?? TimeSpan.Zero;

    public TimeSpan Duration => reader?.TotalTime ?? TimeSpan.Zero;

    public event Action? Stopped;

    public void Play(string path)
    {
        if (loaded != path)
            Load(path);

        output!.Play();
    }

    public void Pause() => output?.Pause();

    public void Stop()
    {
        output?.Stop();

        if (reader is not null)
            reader.Position = 0;
    }

    public void Dispose()
    {
        output?.Dispose();
        reader?.Dispose();
        output = null;
        reader = null;
        loaded = null;
    }

    private void Load(string path)
    {
        Dispose();
        reader = new AudioFileReader(path);
        output = new WaveOut();
        output.Init(reader);
        output.PlaybackStopped += (_, _) => Stopped?.Invoke();
        loaded = path;
    }
}
