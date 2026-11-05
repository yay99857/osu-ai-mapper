using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
#if WINDOWS
using NAudio.Wave;
#endif

namespace OsuAi.Desktop.Services;

/// <summary>
/// Basic playback of the project audio: play, pause, stop. Waveform, click track and object sounds
/// belong to the Rhythm Workbench (roadmap M3). The Windows build plays through NAudio (WinMM); the
/// portable build through an mpv process controlled over its IPC socket.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
#if WINDOWS
    private readonly IBackend backend = new WaveOutBackend();
#else
    private readonly IBackend backend = new MpvBackend();
#endif

    public AudioPlayer() => backend.Stopped += () => Stopped?.Invoke();

    public bool IsPlaying => backend.IsPlaying;

    public event Action? Stopped;

    public void Play(string path) => backend.Play(path);

    public void Pause() => backend.Pause();

    public void Stop() => backend.Stop();

    public void Dispose() => backend.Dispose();

    private interface IBackend : IDisposable
    {
        bool IsPlaying { get; }

        event Action? Stopped;

        void Play(string path);

        void Pause();

        void Stop();
    }

#if WINDOWS
    private sealed class WaveOutBackend : IBackend
    {
        private WaveOut? output;
        private AudioFileReader? reader;
        private string? loaded;

        public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;

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
#endif

    /// <summary>
    /// mpv without video or terminal output. Pausing and resuming go through its JSON IPC socket, so
    /// the position is kept; Stop ends the process and the next Play starts from the beginning.
    /// </summary>
    private sealed class MpvBackend : IBackend
    {
        private Process? process;
        private string? loaded;
        private string? socket;
        private bool paused;

        public bool IsPlaying => process is { HasExited: false } && !paused;

        public event Action? Stopped;

        public void Play(string path)
        {
            if (process is { HasExited: false } && loaded == path)
            {
                if (paused && Send("""{ "command": ["set_property", "pause", false] }"""))
                    paused = false;
                return;
            }

            Stop();
            socket = Path.Combine(Path.GetTempPath(), $"osu-ai-mapper-mpv-{Environment.ProcessId}-{Guid.NewGuid():N}.sock");
            var start = new ProcessStartInfo("mpv") { UseShellExecute = false, RedirectStandardInput = true };
            foreach (string a in new[] { "--no-video", "--no-terminal", "--really-quiet", "--idle=no", $"--input-ipc-server={socket}", "--", path })
                start.ArgumentList.Add(a);

            Process started;
            try
            {
                started = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o mpv.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new InvalidOperationException("A reprodução de áudio no Linux usa o mpv. Instale com: sudo pacman -S mpv", e);
            }

            started.EnableRaisingEvents = true;
            started.Exited += (_, _) =>
            {
                if (ReferenceEquals(process, started))
                    Stopped?.Invoke();
            };
            process = started;
            loaded = path;
            paused = false;
        }

        public void Pause()
        {
            if (process is { HasExited: false } && !paused && Send("""{ "command": ["set_property", "pause", true] }"""))
                paused = true;
        }

        public void Stop()
        {
            var running = process;
            process = null;
            loaded = null;
            paused = false;

            if (running is not null)
            {
                try
                {
                    if (!running.HasExited)
                        running.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                running.Dispose();
                Stopped?.Invoke();
            }

            if (socket is not null)
            {
                try
                {
                    File.Delete(socket);
                }
                catch (IOException)
                {
                }
                socket = null;
            }
        }

        public void Dispose() => Stop();

        /// <summary>One command to mpv; the socket appears a moment after the process starts.</summary>
        private bool Send(string json)
        {
            if (socket is null)
                return false;

            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    client.Connect(new UnixDomainSocketEndPoint(socket));
                    client.Send(Encoding.UTF8.GetBytes(json + "\n"));
                    return true;
                }
                catch (SocketException)
                {
                    Thread.Sleep(50);
                }
            }

            return false;
        }
    }
}
