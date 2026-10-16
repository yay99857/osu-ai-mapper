using OsuAi.Application;

namespace OsuAi.Application.Tests;

public class MapperatorinatorEngineTests
{
    [Test]
    public void ReferenceObjectsGetWholeNumbersAndEverythingElseIsKept()
    {
        string osu = "[TimingPoints]\n17.5,375,4,2,0,60,1,0\n\n[HitObjects]\n256.4,192,1000.6,1,0,0:0:0:0:\n256,192,172360,12,0,174981.253,0:0:0:0:\n100,100,2000,2,0,B|200:100,1,90\n";
        string fixedText = MapperatorinatorEngine.WholeNumberHitObjects(osu);
        Assert.That(fixedText, Does.Contain("17.5,375,4,2,0,60,1,0"), "timing points untouched");
        Assert.That(fixedText, Does.Contain("256,192,1001,1,0,0:0:0:0:"));
        Assert.That(fixedText, Does.Contain("256,192,172360,12,0,174981,0:0:0:0:"), "spinner end rounded");
        Assert.That(fixedText, Does.Contain("100,100,2000,2,0,B|200:100,1,90"));
    }

    [Test]
    public void PathsAreQuotedForHydra()
    {
        Assert.That(MapperatorinatorEngine.HydraValue(@"C:\Desktop\Song (youtube) (3)\work\ref.osu"), Is.EqualTo("'C:/Desktop/Song (youtube) (3)/work/ref.osu'"));
        Assert.That(MapperatorinatorEngine.HydraValue(@"C:\Rock 'n' Roll\a.wav"), Is.EqualTo(@"'C:/Rock \'n\' Roll/a.wav'"));
    }

    [Test]
    public void StyleGoesInAsHydraOverrides()
    {
        var request = new MapperatorinatorRequest("v32", @"C:\p\ref.osu", @"C:\p\a.wav", @"C:\p\out", 5.25, 7)
        {
            Descriptors = ["skillset/jumps", "style/clean"], MapperId = 4452992, StrongStyle = true, CircleSize = 4.2, Span = (1000, 31000),
        };
        var args = MapperatorinatorEngine.Arguments(request);
        Assert.That(args, Does.Contain("descriptors=['skillset/jumps','style/clean']"));
        Assert.That(args, Does.Contain("mapper_id=4452992").And.Contain("cfg_scale=2").And.Contain("circle_size=4.2").And.Contain("year=2023"));
        Assert.That(args, Does.Contain("difficulty=5.25").And.Contain("start_time=1000").And.Contain("end_time=31000"));

        var plain = MapperatorinatorEngine.Arguments(request with { Descriptors = [], MapperId = null });
        Assert.That(plain.Any(a => a.StartsWith("cfg_scale") || a.StartsWith("descriptors") || a.StartsWith("mapper_id")), Is.False,
            "guidance doubles the time and has nothing to follow without a style");
        Assert.Throws<ArgumentException>(() => MapperatorinatorEngine.Arguments(request with { Descriptors = ["style/LN density"] }));
    }

    [Test]
    public void TheRemainingTimeIsRead()
    {
        Assert.That(MapperatorinatorEngine.ParseRemaining(" 34%|███▍ | 11/32 [03:54<08:09, 23.30s/it]"), Is.EqualTo(TimeSpan.FromSeconds(489)));
        Assert.That(MapperatorinatorEngine.ParseRemaining("5/90 [01:02:03<1:10:00, 3s/it]"), Is.EqualTo(new TimeSpan(1, 10, 0)));
        Assert.That(MapperatorinatorEngine.ParseRemaining("0/32 [00:00<?, ?it/s]"), Is.Null);
    }

    [Test]
    public void StarRequestsAreCorrectedByHowFarTheLastOnesLanded()
    {
        Assert.That(StarCalibration.Request(5, []), Is.EqualTo(5));
        var observations = new[] { new OsuAi.ProjectStore.StarObservation(5, 3.98, true, DateTimeOffset.UtcNow) };
        Assert.That(StarCalibration.Request(5, observations), Is.EqualTo(6.02).Within(0.001));
        var wild = new[] { new OsuAi.ProjectStore.StarObservation(5, 0.5, false, DateTimeOffset.UtcNow) };
        Assert.That(StarCalibration.Request(5, wild), Is.EqualTo(7), "never more than two stars away from the target");
    }

    [Test]
    public void ProgressBarsAreRead()
    {
        Assert.That(MapperatorinatorEngine.ParseProgress(" 34%|███▍      | 11/32 [03:54<08:09, 23.30s/it, 5.0 tok/s]"), Is.EqualTo((11, 32)));
        Assert.That(MapperatorinatorEngine.ParseProgress("Model loaded on device cpu"), Is.Null);
        Assert.That(MapperatorinatorEngine.IsEngine("mapperatorinator:v32") && !MapperatorinatorEngine.IsEngine("C:/models/pulso.joblib"));
    }

    [Test]
    public void ObjectsThatGoBackInTimeAreDropped()
    {
        string osu = """
            [General]
            AudioFilename: audio.mp3

            [HitObjects]
            100,100,7229,1,0,0:0:0:0:
            120,100,7510,1,0,0:0:0:0:
            130,100,136766,1,0,0:0:0:0:
            232,88,0,1,0,1:1:-1:0:
            336,266,0,5,0,1:1:-1:0:
            140,100,137000,1,0,0:0:0:0:
            """;
        string clean = MapperatorinatorEngine.DropTimeReversedObjects(osu, out int dropped);
        Assert.That(dropped, Is.EqualTo(2));
        Assert.That(clean, Does.Not.Contain(",0,1,0,1:1:-1:0:"));
        Assert.That(clean, Does.Contain("140,100,137000"));
        Assert.That(clean, Does.Contain("AudioFilename: audio.mp3"));
    }
}

public class MapperatorinatorRepairTests
{
    private static OsuAi.Generation.SongAnalysis Song(double seconds, Func<double, double> energy) => new()
    {
        SchemaVersion = OsuAi.Generation.SongAnalysis.SupportedSchema,
        Audio = new OsuAi.Generation.AnalysedAudio(new string('c', 64), (long)(seconds * 1e6), 44_100),
        Timing = new OsuAi.Generation.AnalysedTiming { Bpm = 120, BeatLengthUs = 500_000, OffsetUs = 0, Confidence = 0.9, Source = "detected" },
        GridDivisor = 4,
        Grid = Enumerable.Range(0, (int)(seconds * 8)).Select(i => new OsuAi.Generation.GridPoint
        {
            Index = i, TimeUs = i * 125_000L, Onset = 0.5, Low = 0.5, High = 0.5, Energy = energy(i * 0.125), Sustain = 0.1,
        }).ToList(),
        Sections = [],
    };

    private static string Map(params int[] times) =>
        "[General]\nAudioFilename: audio.mp3\n\n[TimingPoints]\n0,500,4,2,0,60,1,0\n10000,-100,4,2,0,60,0,0\n\n[HitObjects]\n"
        + string.Concat(times.Select(t => $"256,192,{t},1,0,0:0:0:0:\n"));

    private static OsuAi.BeatmapCore.BeatmapIr Circles(params (double Ms, double X)[] objects)
    {
        string fixture = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "osu", "simple-120bpm.osu");
        var ir = OsuAi.OsuAdapter.OsuImporter.Import(File.ReadAllBytes(File.Exists(fixture) ? fixture : FindFixture())).Beatmap;
        return ir with
        {
            Objects = objects.Select(o => (OsuAi.BeatmapCore.HitObjectIr)new OsuAi.BeatmapCore.CircleIr
            {
                Id = Guid.NewGuid(), TimeUs = (long)(o.Ms * 1000), Position = new OsuAi.BeatmapCore.Vector2D(o.X, 192),
            }).ToList(),
        };
    }

    private static string FindFixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "osu", "simple-120bpm.osu");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("simple-120bpm.osu");
    }

    [Test]
    public void TheReportFindsWhatBrokeMapsBefore()
    {
        var song = Song(60, _ => 0.6);
        // Steady notes to 39 s, then nothing: the "stopped halfway" map. Three more objects piled on the note at 1 s.
        var notes = Enumerable.Range(2, 77).Select(i => (i * 500.0, 100.0 + i % 2 * 60)).Append((1000.0, 200.0)).Append((1000.0, 220.0)).Append((1001.0, 240.0));
        var issues = MapReport.Check(Circles(notes.OrderBy(n => n.Item1).ToArray()), song, 5, 6.2);

        Assert.That(issues[0].Message, Does.Contain("6.20★").Or.Contain("6,20★"), "stars come first");
        Assert.That(issues.Any(i => i.Severity == IssueSeverity.Problem && i.TimeMs == 1000 && i.Message.StartsWith("4 objetos")));
        Assert.That(issues.Any(i => i.Message.Contains("termina em 0:39")), string.Join("; ", issues.Select(i => i.Message)));
    }

    [Test]
    public void APreviewIsMadeWhereTheSongIsLoudest()
    {
        var song = Song(120, t => t is > 70 and < 95 ? 1.0 : 0.2);
        var (start, end) = PreviewSpan.Loudest(song);
        Assert.That(end - start, Is.EqualTo(30_000));
        Assert.That(start, Is.InRange(64_000, 71_000));
        Assert.That(PreviewSpan.Loudest(Song(20, _ => 0.5)), Is.EqualTo((0L, 20_000L)), "a short song is previewed whole");
    }

    [Test]
    public void EmptyStretchesAreFoundOnlyWhereTheMusicPlays()
    {
        var song = Song(60, t => t is > 30 and < 45 ? 0.01 : 0.6);
        var stretches = MapperatorinatorEngine.EmptyStretches(Map(1000, 2000, 10000, 20000, 30000, 46000, 47000), song);
        Assert.That(stretches, Is.EqualTo(new[] { (2000L, 10000L), (10000L, 20000L), (20000L, 30000L) }));
    }

    [Test]
    public void APatchFillsOnlyTheStretchAndBringsItsSliderVelocity()
    {
        string map = Map(1000, 2000, 30000);
        string patch = "[TimingPoints]\n0,500,4,2,0,60,1,0\n15000,-50,4,2,0,60,0,0\n\n[HitObjects]\n100,100,1500,1,0,0:0:0:0:\n100,100,12000,1,0,0:0:0:0:\n100,100,25000,1,0,0:0:0:0:\n";
        string spliced = MapperatorinatorEngine.Splice(map, patch, 2150, 29850);
        Assert.That(spliced, Does.Contain("100,100,12000").And.Contain("100,100,25000"));
        Assert.That(spliced, Does.Not.Contain("100,100,1500"), "outside the stretch the map keeps its own objects");
        Assert.That(spliced, Does.Contain("15000,-50,4,2,0,60,0,0").And.Contain("0,500,4,2,0,60,1,0"));
        Assert.That(spliced, Does.Not.Contain("10000,-100"), "inside the stretch the patch's slider velocity replaces the map's");
        Assert.That(spliced.IndexOf("100,100,12000"), Is.LessThan(spliced.IndexOf("256,192,30000")), "objects stay in time order");
    }
}
