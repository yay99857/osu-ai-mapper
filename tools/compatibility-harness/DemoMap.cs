using OsuAi.BeatmapCore;

namespace OsuAi.CompatibilityHarness;

/// <summary>
/// A B0-style map built directly in the IR: a circle on every beat walking a ring, with a linear
/// and a Bézier slider each bar of the second half. It exists to exercise export and import into
/// the real client, not to be a good map.
/// </summary>
public static class DemoMap
{
    public const long DurationUs = 45_000_000;

    private const double bpm = 150;
    private const long offset_us = 1_000_000;
    private const int bars = 24;

    public static BeatmapIr Create()
    {
        var timing = new TimingMap
        {
            Segments =
            [
                new TimingSegment
                {
                    Id = "t0",
                    StartTimeUs = offset_us,
                    StartBeat = Rational.Zero,
                    BeatLengthUs = 60_000_000 / bpm,
                    Meter = new Meter(4, 4),
                    Source = TimingSource.Manual,
                    Confidence = 1,
                },
            ],
        };

        var objects = new List<HitObjectIr>();
        var hitnormal = new HitSample { Name = HitSample.Normal, Bank = "soft", Volume = 60 };
        var finish = new HitSample { Name = HitSample.Finish, Bank = "soft", Volume = 60, AutoBank = true };

        for (int bar = 0; bar < bars; bar++)
        {
            bool sliders = bar >= bars / 2;

            for (int beatInBar = 0; beatInBar < 4; beatInBar++)
            {
                var beat = Rational.FromInteger(bar * 4 + beatInBar);
                long timeUs = (long)Math.Round(timing.TimeUsAt(beat));
                var position = Ring(bar * 4 + beatInBar);
                var id = DeterministicGuid.Create($"demo:{bar}:{beatInBar}");
                bool newCombo = beatInBar == 0;
                HitSample[] samples = newCombo && bar % 4 == 0 ? [hitnormal, finish] : [hitnormal];

                if (sliders && beatInBar is 0 or 2)
                {
                    // One beat long at SV 1: SliderMultiplier * 100 px.
                    var direction = Ring(bar * 4 + beatInBar + 1);
                    double dx = direction.X - position.X;
                    double dy = direction.Y - position.Y;
                    double scale = 140 / Math.Sqrt(dx * dx + dy * dy);

                    objects.Add(new SliderIr
                    {
                        Id = id,
                        TimeUs = timeUs,
                        Position = position,
                        NewCombo = newCombo,
                        Samples = samples,
                        Path = beatInBar == 0
                            ? Linear(dx * scale, dy * scale)
                            : Bezier(dx * scale, dy * scale),
                        ExpectedDistancePx = 140,
                        SpanCount = 1,
                        NodeSamples = [samples, [hitnormal]],
                    });

                    beatInBar++;
                    continue;
                }

                objects.Add(new CircleIr { Id = id, TimeUs = timeUs, Position = position, NewCombo = newCombo, Samples = samples });
            }
        }

        return new BeatmapIr
        {
            Id = DeterministicGuid.Create("demo:beatmap"),
            RevisionId = DeterministicGuid.Create("demo:revision:1"),
            Metadata = new BeatmapMetadata
            {
                Title = "Click Track 150",
                Artist = "osu-ai-mapper",
                Creator = "osu-ai-mapper",
                DifficultyName = "B0 Demo",
                Tags = ["osu-ai-mapper", "synthetic", "b0"],
            },
            Assets = new BeatmapAssets { Audio = new AssetRef { Id = "audio", RelativePath = "click-150.wav", MimeType = "audio/wav" } },
            Settings = new DifficultySettings { Ar = 8, Od = 7, Cs = 4, Hp = 5, SliderMultiplier = 1.4, SliderTickRate = 1, PreviewTimeUs = offset_us },
            Timing = timing,
            Controls = new BeatmapControls
            {
                Effects =
                [
                    new EffectControl((long)Math.Round(timing.TimeUsAt(Rational.FromInteger(bars / 2 * 4))), true),
                    new EffectControl((long)Math.Round(timing.TimeUsAt(Rational.FromInteger((bars - 2) * 4))), false),
                ],
            },
            Objects = objects,
            Provenance = new Provenance { CreatedBy = "compatibility-harness.demo" },
        };
    }

    private static Vector2D Ring(int step)
    {
        double angle = step * Math.PI / 4;
        return new Vector2D(Math.Round(256 + 120 * Math.Cos(angle)), Math.Round(192 + 100 * Math.Sin(angle)));
    }

    private static SliderPathIr Linear(double x, double y) => new()
    {
        ControlPoints =
        [
            new PathControlPointIr { X = 0, Y = 0, SegmentType = SegmentType.Linear },
            new PathControlPointIr { X = Math.Round(x), Y = Math.Round(y) },
        ],
    };

    private static SliderPathIr Bezier(double x, double y) => new()
    {
        ControlPoints =
        [
            new PathControlPointIr { X = 0, Y = 0, SegmentType = SegmentType.Bezier },
            new PathControlPointIr { X = Math.Round(x / 2 - y / 3), Y = Math.Round(y / 2 + x / 3) },
            new PathControlPointIr { X = Math.Round(x), Y = Math.Round(y) },
        ],
    };
}
