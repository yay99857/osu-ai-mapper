using System.Text;
using OsuAi.BeatmapCore;

namespace OsuAi.CompatibilityHarness;

/// <summary>
/// A synthetic metronome rendered from a <see cref="TimingMap"/>: audio we own outright, so test
/// packages can be imported and played without any licensed music.
/// </summary>
public static class ClickTrack
{
    private const int sample_rate = 44_100;

    public static void WriteWav(string path, TimingMap timing, long durationUs)
    {
        int frameCount = checked((int)(durationUs * sample_rate / 1_000_000));
        var pcm = new float[frameCount];

        for (var beat = Rational.Zero; ; beat += Rational.FromInteger(1))
        {
            double timeUs = timing.TimeUsAt(beat);

            if (timeUs >= durationUs)
                break;

            if (timeUs < 0)
                continue;

            var segment = timing.SegmentAtBeat(beat);
            int beatsPerBar = segment.Meter?.Numerator ?? 4;
            bool downbeat = (beat - segment.StartBeat).Numerator % beatsPerBar == 0;
            AddClick(pcm, (int)Math.Round(timeUs * sample_rate / 1_000_000), downbeat ? 1600 : 1000, downbeat ? 0.8f : 0.5f);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        int dataBytes = frameCount * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sample_rate);
        writer.Write(sample_rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (float sample in pcm)
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
    }

    private static void AddClick(float[] pcm, int start, double frequency, float gain)
    {
        int length = sample_rate * 30 / 1000;

        for (int i = 0; i < length && start + i < pcm.Length; i++)
        {
            double t = (double)i / sample_rate;
            pcm[start + i] += gain * (float)(Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t * 150));
        }
    }
}
