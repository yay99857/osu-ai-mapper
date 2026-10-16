using OsuAi.BeatmapCore;

namespace OsuAi.BeatmapCore.Tests;

internal static class SampleMaps
{
    public static readonly Guid CircleId = DeterministicGuid.Create("sample:circle");
    public static readonly Guid SliderId = DeterministicGuid.Create("sample:slider");
    public static readonly Guid SpinnerId = DeterministicGuid.Create("sample:spinner");

    public static BeatmapIr Minimal() => new()
    {
        Id = DeterministicGuid.Create("sample:beatmap"),
        RevisionId = DeterministicGuid.Create("sample:revision"),
        Metadata = new BeatmapMetadata
        {
            Title = "Sample",
            Artist = "osu-ai-mapper",
            Creator = "osu-ai-mapper",
            DifficultyName = "Test",
        },
        Assets = new BeatmapAssets { Audio = new AssetRef { Id = "audio", RelativePath = "audio.wav" } },
        Settings = new DifficultySettings { Ar = 9, Od = 8, Cs = 4, Hp = 5, SliderMultiplier = 1.4, SliderTickRate = 1 },
        Timing = new TimingMap
        {
            Segments =
            [
                new TimingSegment
                {
                    Id = "t0",
                    StartTimeUs = 1_000_000,
                    StartBeat = Rational.Zero,
                    BeatLengthUs = 500_000,
                    Meter = new Meter(4, 4),
                    Source = TimingSource.Manual,
                },
            ],
        },
        Objects =
        [
            new CircleIr
            {
                Id = CircleId,
                TimeUs = 1_000_000,
                Position = new Vector2D(100, 100),
                NewCombo = true,
                Samples = [new HitSample { Name = HitSample.Normal, Bank = "normal", Volume = 70 }],
            },
            new SliderIr
            {
                Id = SliderId,
                TimeUs = 1_500_000,
                Position = new Vector2D(200, 100),
                Path = new SliderPathIr
                {
                    ControlPoints =
                    [
                        new PathControlPointIr { X = 0, Y = 0, SegmentType = SegmentType.Linear },
                        new PathControlPointIr { X = 140, Y = 0 },
                    ],
                },
                ExpectedDistancePx = 140,
                SpanCount = 2,
                NodeSamples = [[], [], []],
            },
            new SpinnerIr
            {
                Id = SpinnerId,
                TimeUs = 4_000_000,
                Position = new Vector2D(256, 192),
                NewCombo = true,
                EndTimeUs = 6_000_000,
            },
        ],
        Provenance = new Provenance { CreatedBy = "tests" },
    };
}
