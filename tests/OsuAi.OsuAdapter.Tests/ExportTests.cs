using System.IO.Compression;
using System.Text;
using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter.Tests;

public class ExportTests
{
    private string workDir = null!;

    [SetUp]
    public void CreateWorkDir()
    {
        workDir = Path.Combine(Path.GetTempPath(), "osu-ai-mapper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
    }

    [TearDown]
    public void DeleteWorkDir() => Directory.Delete(workDir, recursive: true);

    private static BeatmapIr Authored()
    {
        var hitnormal = new HitSample { Name = HitSample.Normal, Bank = "drum", Volume = 50 };

        return new BeatmapIr
        {
            Id = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),
            Metadata = new BeatmapMetadata { Title = "Authored", Artist = "Tests", Creator = "Tests", DifficultyName = "Normal" },
            Assets = new BeatmapAssets { Audio = new AssetRef { Id = "audio", RelativePath = "audio.wav" } },
            Settings = new DifficultySettings { Ar = 8.5, Od = 7, Cs = 3.8, Hp = 5, SliderMultiplier = 1.6, SliderTickRate = 2 },
            Timing = new TimingMap
            {
                Segments =
                [
                    new TimingSegment
                    {
                        Id = "t0",
                        StartTimeUs = 638_000,
                        StartBeat = Rational.Zero,
                        BeatLengthUs = 60_000_000.0 / 182,
                        Meter = new Meter(4, 4),
                        Source = TimingSource.Manual,
                    },
                ],
            },
            Objects =
            [
                new CircleIr { Id = Guid.NewGuid(), TimeUs = 638_000, Position = new Vector2D(100, 100), NewCombo = true, Samples = [hitnormal] },
                new SliderIr
                {
                    Id = Guid.NewGuid(),
                    TimeUs = 967_670,
                    Position = new Vector2D(200, 120),
                    Samples = [hitnormal],
                    Path = new SliderPathIr
                    {
                        ControlPoints =
                        [
                            new PathControlPointIr { X = 0, Y = 0, SegmentType = SegmentType.Bezier },
                            new PathControlPointIr { X = 60, Y = -50 },
                            new PathControlPointIr { X = 120, Y = 0 },
                        ],
                    },
                    ExpectedDistancePx = 120,
                    SpanCount = 2,
                    SvMultiplier = 1.25,
                    NodeSamples = [[hitnormal], [hitnormal], [hitnormal, new HitSample { Name = HitSample.Clap, Bank = "drum", Volume = 50, AutoBank = true }]],
                },
                new SpinnerIr { Id = Guid.NewGuid(), TimeUs = 3_000_000, Position = new Vector2D(256, 192), NewCombo = true, Samples = [hitnormal], EndTimeUs = 5_000_000 },
            ],
            Provenance = new Provenance { CreatedBy = "tests" },
        };
    }

    [Test]
    public void AuthoredIrSurvivesTheOfficialEncoder()
    {
        var ir = Authored();
        string text = OsuExporter.ToOsuText(ir);
        var back = OsuImporter.Import(Encoding.UTF8.GetBytes(text));

        Assert.That(text, Does.StartWith("osu file format v128"));
        Assert.That(back.Diagnostics, Is.Empty);
        Assert.That(SemanticComparer.Compare(ir, back.Beatmap), Is.Empty);
    }

    [Test]
    public void InvalidIrIsRefused()
    {
        var ir = Authored();
        ir = ir with { Objects = ir.Objects.Reverse().ToArray() };

        var e = Assert.Throws<InvalidBeatmapException>(() => OsuExporter.ToOsuText(ir));
        Assert.That(e!.Errors.Select(d => d.Code), Does.Contain("object.order"));
    }

    [Test]
    public void BackgroundGoesIntoThePackageNextToTheMap()
    {
        string audio = Path.Combine(workDir, "audio.wav");
        File.WriteAllBytes(audio, [1, 2, 3]);
        string image = Path.Combine(workDir, "my picture.png");
        File.WriteAllBytes(image, [9, 8, 7]);
        var ir = Authored();
        ir = ir with { Assets = ir.Assets with { Background = new AssetRef { Id = "background", RelativePath = "background.png" } } };
        string text = OsuExporter.ToOsuText(ir);
        string osz = Path.Combine(workDir, "bg.osz");

        OszPackager.Pack(Encoding.UTF8.GetBytes(text), audio, osz, image);

        Assert.That(text, Does.Contain("0,0,\"background.png\",0,0"));
        using var zip = ZipFile.OpenRead(osz);
        Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("background.png").And.Contain("audio.wav"));
        Assert.Throws<FileNotFoundException>(() => OszPackager.Pack(Encoding.UTF8.GetBytes(text), audio, Path.Combine(workDir, "none.osz")));
    }

    [Test]
    public void PackageContainsOsuAndAudioAtRoot()
    {
        string audio = Path.Combine(workDir, "audio.wav");
        File.WriteAllBytes(audio, [1, 2, 3, 4, 5]);
        byte[] osu = Encoding.UTF8.GetBytes(OsuExporter.ToOsuText(Authored()));
        string osz = Path.Combine(workDir, "out.osz");

        var result = OszPackager.Pack(osu, audio, osz);

        using var zip = ZipFile.OpenRead(osz);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EquivalentTo(new[] { "Tests - Authored (Tests) [Normal].osu", "audio.wav" }));
        Assert.That(result.Entries, Has.Count.EqualTo(2));

        using var audioEntry = zip.GetEntry("audio.wav")!.Open();
        using var copy = new MemoryStream();
        audioEntry.CopyTo(copy);
        Assert.That(copy.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.That(Directory.GetFiles(workDir, "*.tmp"), Is.Empty);
    }

    [Test]
    public void ExporterRefusesEscapingAudioPath()
    {
        var ir = Authored();
        ir = ir with { Assets = new BeatmapAssets { Audio = new AssetRef { Id = "audio", RelativePath = "../audio.wav" } } };

        var e = Assert.Throws<InvalidBeatmapException>(() => OsuExporter.ToOsuText(ir));
        Assert.That(e!.Errors.Select(d => d.Code), Does.Contain("asset.path"));
    }

    [Test]
    public void ExporterRefusesAudioInSubdirectory()
    {
        // Contained, but the official encoder would silently drop the directory.
        var ir = Authored();
        ir = ir with { Assets = new BeatmapAssets { Audio = new AssetRef { Id = "audio", RelativePath = "assets/audio.wav" } } };

        var e = Assert.Throws<InvalidBeatmapException>(() => OsuExporter.ToOsuText(ir));
        Assert.That(e!.Errors.Select(d => d.Code), Is.EqualTo(new[] { "profile.osu-interchange-v1.audio-path" }));
    }

    [Test]
    public void PackagerRefusesEscapingAudioPathInRawFile()
    {
        string audio = Path.Combine(workDir, "audio.wav");
        File.WriteAllBytes(audio, [0]);
        string text = OsuExporter.ToOsuText(Authored()).Replace("AudioFilename: audio.wav", "AudioFilename: ../audio.wav");
        string osz = Path.Combine(workDir, "out.osz");

        Assert.Throws<InvalidBeatmapException>(() => OszPackager.Pack(Encoding.UTF8.GetBytes(text), audio, osz));
        Assert.That(File.Exists(osz), Is.False);
    }

    [Test]
    public void ObjectsWithoutHitNormalAreRefused()
    {
        var ir = Authored();
        ir = ir with { Objects = [ir.Objects[0] with { Samples = [] }, .. ir.Objects.Skip(1)] };

        var e = Assert.Throws<InvalidBeatmapException>(() => OsuExporter.ToOsuText(ir));
        Assert.That(e!.Errors.Select(d => d.Code), Is.EqualTo(new[] { "profile.osu-interchange-v1.hitnormal" }));
    }

    [TestCase("audio.mp3", true)]
    [TestCase("Ünïcødé 歌.ogg", true)]
    [TestCase("../audio.mp3", false)]
    [TestCase("sub/audio.mp3", false)]
    [TestCase("sub\\audio.mp3", false)]
    [TestCase("C:audio.mp3", false)]
    [TestCase("CON", false)]
    [TestCase("nul.wav", false)]
    [TestCase("audio.mp3.", false)]
    [TestCase(" audio.mp3", false)]
    [TestCase("", false)]
    public void EntryNameSafety(string name, bool safe) => Assert.That(PackageNames.IsSafeEntryName(name), Is.EqualTo(safe));

    [Test]
    public void CaseInsensitiveCollisionsAreRefused() =>
        Assert.Throws<UnsafePackageException>(() => PackageNames.RequireDistinct(["Audio.mp3", "audio.MP3"]));
}
