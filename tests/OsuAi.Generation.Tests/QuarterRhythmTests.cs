using OsuAi.BeatmapCore;
using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

/// <summary>Bursts and streams: only where the audio plays 1/4, sized and placed by difficulty tier.</summary>
public class QuarterRhythmTests
{
    [Test]
    public void PulsoUsesLearnedTypesAndClipsSliderBeforeNextHead()
    {
        var prediction = new RhythmPrediction
        {
            ModelSha256 = new string('a', 64), Threshold = 0, Stars = 5,
            Probability = new Dictionary<int, double> { [0] = 0.8, [4] = 0.7, [16] = 0.9 },
            Notes = new Dictionary<int, PulsoNote>
            {
                [0] = new(0.9, 8, 0.2), [4] = new(0.1, 4, 0.8), [16] = new(0.9, 2, 0.5),
            },
        };
        var plan = RhythmSelection.PlanFromModel(Song(), DifficultyTier.Insane, prediction);
        Assert.That(plan.Events.Select(e => e.GridIndex), Is.EqualTo(new[] { 0, 4, 16 }));
        Assert.That(plan.Events[0].DurationGrid, Is.EqualTo(3));
        Assert.That(plan.Events[1].Kind, Is.EqualTo(EventKind.Attack));
        Assert.That(plan.Events[1].Importance, Is.EqualTo(0.8));
        Assert.That(plan.Events[2].DurationGrid, Is.EqualTo(2));
    }

    private const double bpm = 190;
    private const double beat_us = 60_000_000 / bpm;
    private const int bars = 16;

    /// <summary>Beats on every half beat everywhere; a full bar of 1/4 drum roll in bars 4-5 and 12-13.</summary>
    private static SongAnalysis Song()
    {
        var grid = new List<GridPoint>();

        for (int i = 0; i < bars * 16; i++)
        {
            int bar = i / 16;
            bool roll = bar is 4 or 5 or 12 or 13;
            // The roll is as loud as the beat: an obvious fill that even Hard difficulties map.
            double onset = i % 2 == 0 ? (i % 4 == 0 ? 1.0 : 0.7) : roll ? 1.0 : 0;

            grid.Add(new GridPoint
            {
                Index = i,
                TimeUs = (long)Math.Round(400_000 + i * beat_us / 4),
                Onset = onset,
                Low = i % 8 == 0 ? onset : 0,
                High = onset * 0.5,
                Energy = 0.8,
                Sustain = 0.05,
            });
        }

        // Peak intensity throughout, so the tiers' run limits are what is being tested.
        var sections = new[] { new AnalysedSection(0, 0, 0, bars, 3, 0.8) };

        return new SongAnalysis
        {
            SchemaVersion = SongAnalysis.SupportedSchema,
            Audio = new AnalysedAudio(new string('b', 64), (long)(400_000 + bars * 4 * beat_us + 1_000_000), 44_100),
            Timing = new AnalysedTiming { Bpm = bpm, BeatLengthUs = beat_us, OffsetUs = 400_000, Confidence = 0.9, Source = "detected" },
            GridDivisor = 4,
            Grid = grid,
            Sections = sections,
        };
    }

    private static GenerationOptions Options(double stars) => new()
    {
        TargetStars = stars,
        AudioFileName = "audio.mp3",
        Metadata = new BeatmapMetadata { Title = "Rolls", Artist = "Tests", Creator = "Tests", DifficultyName = "B1" },
    };

    private static BeatmapIr Build(double stars) =>
        B1Generator.Build(Song(), RhythmSelection.Plan(Song(), RhythmSelection.TierFor(stars)), Options(stars), 1.8);

    private static List<List<HitObjectIr>> QuarterRuns(BeatmapIr map)
    {
        var runs = new List<List<HitObjectIr>>();
        var current = new List<HitObjectIr> { map.Objects[0] };

        for (int i = 1; i < map.Objects.Count; i++)
        {
            bool quarter = Math.Abs(map.Objects[i].TimeUs - map.Objects[i - 1].TimeUs - beat_us / 4) < 2000;

            if (quarter)
            {
                current.Add(map.Objects[i]);
            }
            else
            {
                if (current.Count >= 2)
                    runs.Add(current);
                current = [map.Objects[i]];
            }
        }

        if (current.Count >= 2)
            runs.Add(current);

        return runs;
    }

    private static int Bar(HitObjectIr o) => (int)Math.Floor((o.TimeUs - (400_000 - 15_000)) / (beat_us * 4));

    [TestCase(3.5, 3)]
    [TestCase(5.0, 5)]
    public void RunLengthFollowsTheTier(double stars, int longest)
    {
        var runs = QuarterRuns(Build(stars));

        Assert.That(runs, Is.Not.Empty);
        Assert.That(runs.Max(r => r.Count), Is.LessThanOrEqualTo(longest));
    }

    [Test]
    public void ExpertStreamsRunLongButCapped()
    {
        var runs = QuarterRuns(Build(6.0));

        Assert.That(runs.Max(r => r.Count), Is.GreaterThan(8));
        // Regression: a slider turned into a stream start used to reset the count, giving 57-note runs.
        Assert.That(runs.Max(r => r.Count), Is.LessThanOrEqualTo(24));
    }

    [Test]
    public void ModelClicksAndFillsTogetherRespectTheCap()
    {
        // Regression: in the B2 path the model places runs of consecutive 1/4 clicks itself. The fill did
        // not count those, so it re-joined model runs across single gaps into chains of up to 238 notes.
        // Here the model streams through the rolls with a hole every 10 positions, which the audio-driven
        // fill closes; the joined stream must still be cut at the tier's cap.
        var song = Song();
        bool InRoll(int i) => (i / 16) is 4 or 5 or 12 or 13;
        var holes = new RhythmPrediction
        {
            ModelSha256 = new string('0', 64),
            Threshold = 0.5,
            Stars = 6,
            Probability = song.Grid.Where(p => p.Index >= 0)
                              .ToDictionary(p => p.Index, p => InRoll(p.Index) ? (p.Index % 10 == 5 ? 0.0 : 0.9) : p.Index % 2 == 0 ? 0.9 : 0.0),
        };

        var plan = RhythmSelection.PlanFromModel(song, DifficultyTier.Expert, holes);
        var map = B1Generator.Build(song, plan, Options(6.0), 1.8);

        Assert.That(QuarterRuns(map).Max(r => r.Count), Is.LessThanOrEqualTo(24));
    }

    [Test]
    public void TwoBarRollIsSplitAtTheCap()
    {
        // Bars 4-5 roll for 32 quarters; with the 24-note cap the stream must break at least once.
        var runs = QuarterRuns(Build(6.0)).Where(r => Bar(r[0]) is 3 or 4 or 5).ToList();
        Assert.That(runs.Count, Is.GreaterThanOrEqualTo(2));
    }

    [TestCase(3.5)]
    [TestCase(5.0)]
    [TestCase(6.0)]
    public void QuarterNotesOnlyWhereTheAudioPlaysThem(double stars)
    {
        foreach (var run in QuarterRuns(Build(stars)))
        {
            // The notes inserted between clicks lie inside a roll; a run may end on the next click after it.
            Assert.That(run.Skip(1).SkipLast(1).Select(Bar), Is.All.AnyOf(4, 5, 12, 13), $"run at {run[0].TimeUs}");
        }
    }

    [Test]
    public void InsaneBurstsAreMostlyStackedAtThisTempo()
    {
        // 1/4 at 190 BPM is 79 ms: human maps stack about two thirds of such bursts (burst_study.py)
        // and never stack longer streams.
        var runs = QuarterRuns(Build(5.0));
        var bursts = runs.Where(r => r.Count <= 5).ToList();
        Assert.That(bursts, Is.Not.Empty);
        Assert.That(bursts.Count(r => r.Select(o => o.Position).Distinct().Count() == 1), Is.GreaterThanOrEqualTo(bursts.Count / 2));
        Assert.That(runs.Where(r => r.Count >= 6).All(r => r.Select(o => o.Position).Distinct().Count() > 1));
    }

    [Test]
    public void ExpertStreamsOverlapAndKeepMoving()
    {
        var map = Build(6.0);
        double radius = 54.4 - 4.48 * map.Settings.Cs;

        foreach (var run in QuarterRuns(map).Where(r => r.Count >= 4))
        {
            for (int i = 1; i < run.Count; i++)
            {
                double step = Math.Sqrt(Math.Pow(run[i].Position.X - run[i - 1].Position.X, 2) + Math.Pow(run[i].Position.Y - run[i - 1].Position.Y, 2));
                Assert.That(step, Is.InRange(radius * 0.5, radius * 2), "stream circles overlap but move");
            }
        }
    }

    [Test]
    public void LongStreamsDoNotLoopOverThemselves()
    {
        // Regression: constant 30° per note closed a stream into a loop every 12 notes. Notes of one
        // stream that are three or more apart must not sit on each other.
        var map = Build(6.0);
        double radius = 54.4 - 4.48 * map.Settings.Cs;

        foreach (var run in QuarterRuns(map).Where(r => r.Count >= 8))
        {
            for (int i = 0; i < run.Count; i++)
            {
                for (int j = i + 3; j < run.Count; j++)
                {
                    double d = Math.Sqrt(Math.Pow(run[i].Position.X - run[j].Position.X, 2) + Math.Pow(run[i].Position.Y - run[j].Position.Y, 2));
                    Assert.That(d, Is.GreaterThan(radius), $"notes {i} and {j} of a {run.Count}-note stream overlap");
                }
            }
        }
    }

    [Test]
    public void StreamsStayInsideThePlayfield()
    {
        foreach (var o in QuarterRuns(Build(6.0)).SelectMany(r => r))
        {
            Assert.That(o.Position.X, Is.InRange(0, 512));
            Assert.That(o.Position.Y, Is.InRange(0, 384));
        }
    }

    [Test]
    public void NoNewComboInsideARun()
    {
        foreach (var run in QuarterRuns(Build(6.0)))
            Assert.That(run.Skip(1).Any(o => o.NewCombo), Is.False);
    }

    [Test]
    public void StreamsAreStillValidAndExportable()
    {
        var map = Build(6.0);
        Assert.That(BeatmapValidator.Validate(map).HasErrors(), Is.False);
    }
}
