using OsuAi.BeatmapCore;
using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

public class B1GeneratorTests
{
    private const double bpm = 180;
    private const double beat_us = 60_000_000 / bpm;
    private const int bars = 32;

    /// <summary>
    /// A synthetic song: the same one-bar drum pattern throughout (kick on 1 and 3, snare on 2 and 4,
    /// a weak offbeat), held sound in every eighth bar, intensity rising in four-bar phrases.
    /// </summary>
    private static SongAnalysis Song()
    {
        var grid = new List<GridPoint>();
        double[] pattern = [1.0, 0, 0.3, 0, 0.8, 0, 0.25, 0, 1.0, 0, 0.3, 0, 0.8, 0, 0.6, 0];

        for (int i = 0; i < bars * 16; i++)
        {
            int bar = i / 16;
            double onset = pattern[i % 16];
            bool held = bar % 8 == 7;

            grid.Add(new GridPoint
            {
                Index = i,
                TimeUs = (long)Math.Round(500_000 + i * beat_us / 4),
                Onset = held ? (i % 16 == 0 ? 1 : 0) : onset,
                Low = i % 8 == 0 ? onset : 0,
                High = i % 8 == 4 ? onset : 0,
                Energy = 0.7,
                Sustain = held ? 0.8 : 0.05,
            });
        }

        var sections = Enumerable.Range(0, bars / 4)
                                 .Select(p => new AnalysedSection(0, 0, p * 4, 4, p % 4, 0.7))
                                 .ToArray();

        return new SongAnalysis
        {
            SchemaVersion = SongAnalysis.SupportedSchema,
            Audio = new AnalysedAudio(new string('a', 64), (long)(500_000 + bars * 4 * beat_us + 2_000_000), 44_100),
            Timing = new AnalysedTiming { Bpm = bpm, BeatLengthUs = beat_us, OffsetUs = 500_000, Confidence = 0.9, Source = "detected" },
            GridDivisor = 4,
            Grid = grid,
            Sections = sections,
        };
    }

    private static GenerationOptions Options() => new()
    {
        AudioFileName = "audio.mp3",
        Metadata = new BeatmapMetadata { Title = "Synthetic", Artist = "Tests", Creator = "Tests", DifficultyName = "B1" },
    };

    private static BeatmapIr Build(double spacing = 1.8) =>
        B1Generator.Build(Song(), RhythmSelection.Plan(Song()), Options(), spacing);

    [Test]
    public void RedLineFollowsTheClientConvention()
    {
        var map = Build();

        Assert.That(map.Timing.Segments[0].StartTimeUs, Is.EqualTo(500_000 - 15_000));
        Assert.That(map.Objects[0].TimeUs % 1, Is.Zero);
        Assert.That((map.Objects[0].TimeUs - 485_000) % (long)Math.Round(beat_us / 2), Is.LessThanOrEqualTo(1), "objects sit on the shifted grid");
    }

    [Test]
    public void IsDeterministic() => Assert.That(IrJson.Serialize(Build()), Is.EqualTo(IrJson.Serialize(Build())));

    [Test]
    public void IsStructurallyValid()
    {
        var diagnostics = BeatmapValidator.Validate(Build());
        Assert.That(diagnostics, Is.Empty, string.Join(Environment.NewLine, diagnostics));
    }

    [TestCase(1.0)]
    [TestCase(1.8)]
    [TestCase(3.0)]
    public void NothingOverlapsWithinOneBeat(double spacing)
    {
        var map = Build(spacing);
        double radius = 54.4 - 4.48 * map.Settings.Cs;

        for (int i = 0; i < map.Objects.Count; i++)
        {
            for (int j = i - 1; j >= 0 && map.Objects[i].TimeUs - map.Objects[j].TimeUs <= beat_us; j--)
            {
                double d = Distance(map.Objects[i].Position, map.Objects[j].Position);
                Assert.That(d, Is.GreaterThanOrEqualTo(radius), $"objects {j} and {i} overlap");
            }
        }
    }

    [Test]
    public void SlidersStayInsideThePlayfieldAndLastAtMostOneBeat()
    {
        var sliders = Build().Objects.OfType<SliderIr>().ToList();

        // Like the human maps it was calibrated on, some sliders reverse, but never more than once.
        Assert.That(sliders.Select(s => s.SpanCount).Distinct().Order(), Is.SubsetOf(new[] { 1, 2 }));
        Assert.That(sliders.Count(s => s.SpanCount == 2), Is.LessThan(sliders.Count / 3));

        foreach (var slider in sliders)
        {
            Assert.That(slider.SpanCount * slider.ExpectedDistancePx / (180 * slider.SvMultiplier), Is.LessThanOrEqualTo(1 + 1e-6), "sliders are at most one beat long");

            foreach (var p in slider.Path.ControlPoints)
            {
                Assert.That(slider.Position.X + p.X, Is.InRange(0, 512));
                Assert.That(slider.Position.Y + p.Y, Is.InRange(0, 384));
            }
        }
    }

    [Test]
    public void RepeatedBarsGetTheSameRhythm()
    {
        var plan = RhythmSelection.Plan(Song());

        // Phrases 0 (bars 0-3) and 4 (bars 16-19) carry the same music at the same intensity.
        string Rhythm(int bar) => string.Join(",", plan.Events
                                                       .Where(e => RhythmSelection.Bar(e.GridIndex) == bar)
                                                       .Select(e => $"{e.GridIndex % 16}{e.Kind}{e.DurationGrid}"));

        Assert.That(Rhythm(17), Is.EqualTo(Rhythm(1)));
        Assert.That(Rhythm(18), Is.EqualTo(Rhythm(2)));
    }

    [Test]
    public void DensityFollowsIntensity()
    {
        var plan = RhythmSelection.Plan(Song());
        int Count(int phrase) => plan.Events.Count(e => RhythmSelection.Bar(e.GridIndex) / 4 == phrase);

        Assert.That(Count(3), Is.GreaterThan(Count(1)));
        Assert.That(Count(1), Is.GreaterThan(Count(0)));
    }

    [Test]
    public void HalfBeatRunsAreLimitedOutsidePeaks()
    {
        var plan = RhythmSelection.Plan(Song());
        int run = 0;

        for (int i = 1; i < plan.Events.Count; i++)
        {
            var e = plan.Events[i];
            run = e.GridIndex - plan.Events[i - 1].GridIndex == 2 ? run + 1 : 0;

            if (e.Intensity < 3)
                Assert.That(run, Is.LessThanOrEqualTo(3), $"run at grid {e.GridIndex}");
        }
    }

    [Test]
    public void StarSearchRespectsItsBudget()
    {
        int calls = 0;
        var result = B1Generator.Generate(Song(), Options() with { MaxCandidates = 3, TargetStars = 99 }, _ => ++calls);

        Assert.That(calls, Is.EqualTo(3));
        Assert.That(result.Status, Is.EqualTo(GenerationStatus.Infeasible));
    }

    private static double Distance(Vector2D a, Vector2D b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
