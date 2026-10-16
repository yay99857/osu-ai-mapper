using OsuAi.BeatmapCore;
using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

public class SpinnerTests
{
    private const double beat_us = 60_000_000 / 180.0;

    /// <summary>Twelve quieter bars, then a drop at bar 12 three times as loud, drums throughout.</summary>
    private static SongAnalysis SongWithDrop()
    {
        double[] pattern = [1.0, 0, 0.3, 0, 0.8, 0, 0.25, 0, 1.0, 0, 0.3, 0, 0.8, 0, 0.6, 0];
        var grid = Enumerable.Range(0, 24 * 16).Select(i => new GridPoint
        {
            Index = i,
            TimeUs = (long)Math.Round(500_000 + i * beat_us / 4),
            Onset = pattern[i % 16],
            Low = i % 8 == 0 ? pattern[i % 16] : 0,
            High = i % 8 == 4 ? pattern[i % 16] : 0,
            Energy = i / 16 < 12 ? 0.3 : 0.9,
            Sustain = 0.05,
        }).ToList();

        return new SongAnalysis
        {
            SchemaVersion = SongAnalysis.SupportedSchema,
            Audio = new AnalysedAudio(new string('b', 64), (long)(500_000 + 24 * 4 * beat_us), 44_100),
            Timing = new AnalysedTiming { Bpm = 180, BeatLengthUs = beat_us, OffsetUs = 500_000, Confidence = 0.9, Source = "detected" },
            GridDivisor = 4,
            Grid = grid,
            Sections = [new AnalysedSection(0, 0, 0, 12, 1, 0.3), new AnalysedSection(0, 0, 12, 12, 3, 0.9)],
        };
    }

    private static BeatmapIr Build()
    {
        var song = SongWithDrop();
        var options = new GenerationOptions
        {
            AudioFileName = "audio.mp3",
            Metadata = new BeatmapMetadata { Title = "Drop", Artist = "Tests", Creator = "Tests", DifficultyName = "Insane" },
        };
        return B1Generator.Build(song, RhythmSelection.Plan(song), options, 1.8);
    }

    [Test]
    public void TheBuildUpBeforeTheDropBecomesASpinner()
    {
        var map = Build();
        var spinner = map.Objects.OfType<SpinnerIr>().Single();
        long dropUs = map.Timing.Segments[0].StartTimeUs + (long)Math.Round(12 * 4 * beat_us);

        Assert.That(spinner.TimeUs, Is.EqualTo(dropUs - (long)Math.Round(8 * beat_us)).Within(2), "two calm bars before the drop");
        Assert.That(spinner.EndTimeUs, Is.EqualTo(dropUs - (long)Math.Round(beat_us)).Within(2), "a beat before the drop");
        Assert.That(map.Objects.Where(o => o is not SpinnerIr).Any(o => o.TimeUs > spinner.TimeUs - beat_us / 2 && o.TimeUs < spinner.EndTimeUs + beat_us / 2), Is.False);
        Assert.That(map.Objects.Any(o => o.TimeUs >= dropUs && o.TimeUs < dropUs + beat_us), "the drop itself is mapped");
        var after = map.Objects.SkipWhile(o => o is not SpinnerIr).Skip(1).First();
        Assert.That(after.NewCombo, "osu! starts a new combo after a spinner");
    }

    [Test]
    public void AMapWithASpinnerStaysValid()
    {
        var diagnostics = BeatmapValidator.Validate(Build());
        Assert.That(diagnostics, Is.Empty, string.Join(Environment.NewLine, diagnostics));
    }
}
