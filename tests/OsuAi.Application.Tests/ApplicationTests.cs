using System.IO.Compression;
using System.Text;
using OsuAi.Application;
using OsuAi.BeatmapCore;
using OsuAi.OsuAdapter;
using OsuAi.ProjectStore;

namespace OsuAi.Application.Tests;

public class ApplicationTests
{
    private string work = null!;
    private string audio = null!;

    private static readonly ProjectMetadata metadata = new()
    {
        Title = "Song",
        Artist = "Singer",
        Creator = "Mapper",
        DifficultyName = "Insane",
    };

    [SetUp]
    public void SetUp()
    {
        work = Path.Combine(Path.GetTempPath(), "osu-ai-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        audio = Path.Combine(work, "song.mp3");
        File.WriteAllBytes(audio, Encoding.ASCII.GetBytes("not really audio"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(work, recursive: true);

    /// <summary>The service with a uv that does not exist: any attempt to run a tool fails loudly.</summary>
    private MappingService Service() => new(new ToolEnvironment(RepositoryRoot(), uv: "uv-that-does-not-exist"));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OsuAiMapper.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Run from the repository.");
    }

    // ---- metadata (META-01, UI-02) ----

    [Test]
    public void ArtistAndMapperAreSeparateRequiredFields()
    {
        var problems = MetadataRules.Problems(metadata with { Artist = " ", Creator = "" });
        Assert.That(problems, Has.Some.Contains("Artist").And.Some.Contains("Mapper"));
    }

    [TestCase("Title\nVersion:Hacked")]
    [TestCase("Title\r")]
    [TestCase("Ti\ttle")]
    public void LineBreaksCannotInjectFields(string title) =>
        Assert.That(MetadataRules.Problems(metadata with { Title = title }), Has.Some.Contains("control character"));

    [Test]
    public void UnicodeIsKept()
    {
        var m = MetadataRules.Normalise(metadata with { TitleUnicode = "  風の歌 ", Tags = ["tag", "", "tag"] });
        Assert.That(MetadataRules.Problems(m), Is.Empty);
        Assert.That(m.TitleUnicode, Is.EqualTo("風の歌"));
        Assert.That(m.Tags, Is.EqualTo(new[] { "tag" }));
    }

    [Test]
    public void TagsWithSpacesAreRefused() =>
        Assert.That(MetadataRules.Problems(metadata with { Tags = ["two words"] }), Has.Some.Contains("space"));

    [Test]
    public void FormatRefusesControlCharactersEvenIfTheAppMissedThem()
    {
        var ir = OsuImporter.Import(File.ReadAllBytes(Fixture("simple-120bpm.osu"))).Beatmap;
        ir = ir with { Metadata = ir.Metadata with { Creator = "a\nVersion: x" } };

        var e = Assert.Throws<InvalidBeatmapException>(() => OsuExporter.ToOsuText(ir));
        Assert.That(e!.Errors.Select(d => d.Code), Does.Contain("profile.osu-interchange-v1.metadata-control-character"));
    }

    // ---- analysis cache key (PRJ-02) ----

    [Test]
    public void CacheKeyFollowsAudioAnalyzerAndTimingOnly()
    {
        string key = MappingService.KeyOf("audio-a", "analyzer-1", null);

        Assert.That(MappingService.KeyOf("audio-a", "analyzer-1", null), Is.EqualTo(key));
        Assert.That(MappingService.KeyOf("audio-b", "analyzer-1", null), Is.Not.EqualTo(key));
        Assert.That(MappingService.KeyOf("audio-a", "analyzer-2", null), Is.Not.EqualTo(key));
        Assert.That(MappingService.KeyOf("audio-a", "analyzer-1", new TimingCorrection(180, null)), Is.Not.EqualTo(key));
        Assert.That(MappingService.KeyOf("audio-a", "analyzer-1", new TimingCorrection(180, 12.5)),
                    Is.Not.EqualTo(MappingService.KeyOf("audio-a", "analyzer-1", new TimingCorrection(180, null))));
    }

    [Test]
    public void ChangingMetadataKeepsTheCacheKey()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        string before = service.AnalysisKey(project);

        service.SetMetadata(project, metadata with { Title = "Other" });

        Assert.That(service.AnalysisKey(project), Is.EqualTo(before));
    }

    [Test]
    public async Task ValidCachedAnalysisIsReusedWithoutRunningTools()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        PlantAnalysis(project, service.AnalysisKey(project), matchingAudio: true);

        var outcome = await service.AnalyseAsync(project, null, CancellationToken.None);

        Assert.That(outcome.Reused, Is.True);
        Assert.That(project.Manifest.Audio!.RelativePath, Does.StartWith($"analysis/{service.AnalysisKey(project)}/"));
    }

    [Test]
    public void AnalysisOfOtherAudioIsNotReused()
    {
        // The analysis.json does not describe the audio.mp3 next to it (as after swapping files):
        // the service must analyse again, which here fails because uv does not exist.
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        PlantAnalysis(project, service.AnalysisKey(project), matchingAudio: false);

        Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => service.AnalyseAsync(project, null, CancellationToken.None));
        Assert.That(project.Manifest.Analysis, Is.Null);
    }

    // ---- project store (PRJ-01) ----

    [Test]
    public void ProjectReopensWithItsFieldsAndRevision()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings { TargetStars = 4.5 });
        AddRevision(project, "Song");

        var reopened = Project.Open(project.Root);

        Assert.That(reopened.Manifest.Metadata, Is.EqualTo(project.Manifest.Metadata).Using<ProjectMetadata>(SameMetadata));
        Assert.That(reopened.Manifest.Settings.TargetStars, Is.EqualTo(4.5));
        Assert.That(reopened.ActiveRevision!.Id, Is.EqualTo("rev-0001"));
        Assert.That(File.ReadAllBytes(reopened.PathOf(reopened.Manifest.Original.RelativePath)), Is.EqualTo(File.ReadAllBytes(audio)));
    }

    [Test]
    public void FailedRevisionLeavesThePreviousStateIntact()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        AddRevision(project, "Song");
        string manifestBefore = File.ReadAllText(Path.Combine(project.Root, Project.ManifestName));

        Assert.Throws<IOException>(() => project.AddRevision((staging, _) =>
        {
            File.WriteAllText(Path.Combine(staging, "half-written.json"), "{");
            throw new IOException("disk full");
        }));

        Assert.That(File.ReadAllText(Path.Combine(project.Root, Project.ManifestName)), Is.EqualTo(manifestBefore));
        Assert.That(Directory.GetDirectories(project.PathOf("revisions")).Select(Path.GetFileName), Is.EqualTo(new[] { "rev-0001" }));
        Assert.That(Project.Open(project.Root).ActiveRevision!.Id, Is.EqualTo("rev-0001"));
    }

    [Test]
    public void ProjectFolderMustBeNewOrEmpty()
    {
        string dir = Path.Combine(work, "busy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "something.txt"), "x");

        Assert.Throws<InvalidOperationException>(() => Service().CreateProject(dir, audio, metadata, new ProjectSettings()));
    }

    // ---- metadata revision and export (META-01, QA-01) ----

    [Test]
    public void RenamingKeepsObjectsAndReachesTheExportedFile()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        PlantAnalysis(project, service.AnalysisKey(project), matchingAudio: true);
        project.Update(m => m with { Analysis = new AnalysisRef(service.AnalysisKey(project), $"analysis/{service.AnalysisKey(project)}", "test", null) });
        var first = AddRevision(project, "Song");

        var renamed = service.UpdateMetadata(project, metadata with { Title = "Renamed", Creator = "Someone Else" })!;
        var reopened = Project.Open(project.Root);
        string package = service.Export(reopened);

        var a = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(project.PathOf(first.BeatmapIr)));
        var b = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(project.PathOf(renamed.BeatmapIr)));
        Assert.That(renamed.Kind, Is.EqualTo(RevisionKind.Metadata));
        Assert.That(renamed.Parent, Is.EqualTo(first.Id));
        Assert.That(IrJson.Serialize(b.Objects), Is.EqualTo(IrJson.Serialize(a.Objects)));
        Assert.That(IrJson.Serialize(b.Timing), Is.EqualTo(IrJson.Serialize(a.Timing)));

        using var zip = ZipFile.OpenRead(package);
        var osuEntry = zip.Entries.Single(e => e.FullName.EndsWith(".osu"));
        using var reader = new StreamReader(osuEntry.Open());
        var exported = OsuImporter.Import(Encoding.UTF8.GetBytes(reader.ReadToEnd())).Beatmap;
        Assert.That(exported.Metadata.Title, Is.EqualTo("Renamed"));
        Assert.That(exported.Metadata.Creator, Is.EqualTo("Someone Else"));
        Assert.That(exported.Metadata.Artist, Is.EqualTo("Singer"));
        Assert.That(exported.Metadata.Tags, Does.Contain(MappingService.GeneratedTag));
        Assert.That(exported.Objects, Has.Count.EqualTo(a.Objects.Count));
        Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("audio.mp3"));
    }

    [Test]
    public void BackgroundIsAppliedAtExportWithoutANewRevision()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        PlantAnalysis(project, service.AnalysisKey(project), matchingAudio: true);
        project.Update(m => m with { Analysis = new AnalysisRef(service.AnalysisKey(project), $"analysis/{service.AnalysisKey(project)}", "test", null) });
        AddRevision(project, "Song");
        string image = Path.Combine(work, "cover.jpg");
        File.WriteAllBytes(image, [0xFF, 0xD8, 0xFF, 1, 2]);

        Assert.Throws<ArgumentException>(() => service.SetBackground(project, audio), "only JPG or PNG");
        service.SetBackground(project, image);
        var reopened = Project.Open(project.Root);
        Assert.That(reopened.Manifest.Background!.RelativePath, Is.EqualTo("assets/background.jpg"));
        Assert.That(reopened.Manifest.Revisions, Has.Count.EqualTo(1));

        string package = service.Export(reopened);
        using (var zip = ZipFile.OpenRead(package))
        {
            Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("background.jpg"));
            using var reader = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".osu")).Open());
            Assert.That(reader.ReadToEnd(), Does.Contain("\"background.jpg\""));
        }

        service.ClearBackground(reopened);
        Assert.That(File.Exists(reopened.PathOf("assets/background.jpg")), Is.False);
        using var plain = ZipFile.OpenRead(service.Export(Project.Open(project.Root)));
        Assert.That(plain.Entries.Select(e => e.FullName), Does.Not.Contain("background.jpg"));
    }

    [Test]
    public void AMapsetPackagesOneMapPerDifficultyUnderTheSongsName()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        PlantAnalysis(project, service.AnalysisKey(project), matchingAudio: true);
        project.Update(m => m with { Analysis = new AnalysisRef(service.AnalysisKey(project), $"analysis/{service.AnalysisKey(project)}", "test", null) });
        var insane = AddRevision(project, "Song", "Insane");
        AddRevision(project, "Song", "Hard");
        service.SetInPackage(project, insane.Id, include: true);
        var preview = AddRevision(project, "Song", "Hard (prévia)", preview: true);

        Assert.Throws<InvalidOperationException>(() => service.SetInPackage(project, preview.Id, include: true), "previews stay out of mapsets");
        Assert.That(MappingService.PackageOf(project).Select(r => r.Metadata.DifficultyName), Is.EquivalentTo(new[] { "Hard (prévia)", "Hard", "Insane" }),
            "an active preview is exported with the mapset, without taking the place of the full Hard");

        var hard = AddRevision(project, "Song", "Hard");
        Assert.That(MappingService.PackageOf(project).Select(r => r.Id), Is.EquivalentTo(new[] { hard.Id, insane.Id }));
        Assert.Throws<InvalidOperationException>(() => service.SetInPackage(project, hard.Id, include: false), "the active revision always goes");

        string package = service.Export(Project.Open(project.Root));
        Assert.That(Path.GetFileName(package), Is.EqualTo("Singer - Song (Mapper).osz"));
        using var zip = ZipFile.OpenRead(package);
        Assert.That(zip.Entries.Select(e => e.FullName).Where(n => n.EndsWith(".osu")),
            Is.EquivalentTo(new[] { "Singer - Song (Mapper) [Hard].osu", "Singer - Song (Mapper) [Insane].osu" }));

        service.SetInPackage(project, insane.Id, include: false);
        Assert.That(Path.GetFileName(service.Export(Project.Open(project.Root))), Is.EqualTo("Singer - Song (Mapper) [Hard].osz"));
    }

    [Test]
    public void RenamingADifficultyInAMapsetReplacesIt()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        var insane = AddRevision(project, "Song", "Insane");
        var hard = AddRevision(project, "Song", "Hard");
        service.SetInPackage(project, insane.Id, include: true);

        var renamed = service.UpdateMetadata(project, metadata with { DifficultyName = "Muzu's Hard" })!;
        Assert.That(MappingService.PackageOf(project).Select(r => r.Id), Is.EquivalentTo(new[] { renamed.Id, insane.Id }),
            "the renamed Hard takes its parent's place; the Insane stays");
        Assert.That(project.Manifest.Package, Does.Not.Contain(hard.Id));
    }

    [Test]
    public void TheInstalledConfigNamesTheRepositoryAndUv()
    {
        string config = Path.Combine(work, ToolEnvironment.ConfigFileName);
        File.WriteAllText(config, """{ "repositoryRoot": "C:\\repo", "uv": "C:\\bin\\uv.exe" }""");
        var read = ToolEnvironment.ReadConfig(config);
        Assert.That(read.RepositoryRoot, Is.EqualTo(@"C:\repo"));
        Assert.That(read.Uv, Is.EqualTo(@"C:\bin\uv.exe"));
    }

    [Test]
    public void APreviewNeedsMapperatorinator()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(project, null, CancellationToken.None, preview: true));
    }

    [Test]
    public void ExportNeedsARevision()
    {
        var service = Service();
        var project = service.CreateProject(Path.Combine(work, "p"), audio, metadata, new ProjectSettings());
        Assert.Throws<InvalidOperationException>(() => service.Export(project));
    }

    // ---- cancellation (APP-02) ----

    [Test]
    public void CancellingStopsTheChildProcessQuickly()
    {
        var (file, args) = OperatingSystem.IsWindows() ? ("ping", new[] { "-n", "30", "127.0.0.1" }) : ("sleep", new[] { "30" });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.CatchAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(file, args, cancel.Token));
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    // ---- helpers ----

    private static bool SameMetadata(ProjectMetadata a, ProjectMetadata b) =>
        a with { Tags = [] } == b with { Tags = [] } && a.Tags.SequenceEqual(b.Tags);

    private static string Fixture(string name)
    {
        string root = RepositoryRoot();
        return Path.Combine(root, "tests", "fixtures", "osu", name);
    }

    /// <summary>A cached analysis folder as the analyzer would leave it, with the minimal contract example.</summary>
    private void PlantAnalysis(Project project, string key, bool matchingAudio)
    {
        string dir = project.PathOf($"analysis/{key}");
        Directory.CreateDirectory(dir);
        byte[] mp3 = Encoding.ASCII.GetBytes("delivered audio bytes");
        File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), mp3);

        string sha = matchingAudio ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(mp3)) : new string('0', 64);
        string example = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "contracts", "song-analysis-minimal.json"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(example)!;
        json["audio"]!["sha256"] = sha;
        File.WriteAllText(Path.Combine(dir, "analysis.json"), json.ToJsonString());
    }

    /// <summary>A generated revision made from a fixture, as the generator would have written it.</summary>
    private static RevisionRecord AddRevision(Project project, string title, string difficulty = "Insane", bool preview = false)
    {
        var ir = OsuImporter.Import(File.ReadAllBytes(Fixture("simple-120bpm.osu"))).Beatmap;
        ir = ir with
        {
            Metadata = ir.Metadata with { Title = title, Artist = "Singer", Creator = "Mapper", DifficultyName = difficulty, Tags = [MappingService.GeneratedTag, "b1"] },
            Assets = ir.Assets with { Audio = ir.Assets.Audio with { RelativePath = "audio.mp3" } },
        };

        return project.AddRevision((staging, relative) =>
        {
            File.WriteAllText(Path.Combine(staging, "beatmap-ir.json"), IrJson.Serialize(ir));

            return new RevisionRecord
            {
                Id = project.NextRevisionId,
                Kind = RevisionKind.Generated,
                CreatedAt = DateTimeOffset.UtcNow,
                BeatmapIr = $"{relative}/beatmap-ir.json",
                AnalysisKey = "test",
                Metadata = project.Manifest.Metadata with { DifficultyName = difficulty },
                Generation = new GenerationRecord
                {
                    Generator = "b1", Seed = 1, TargetStars = 1, ActualStars = 1.2, TargetReached = true, SpacingFactor = 1, DifficultyProfile = "test",
                    PreviewStartMs = preview ? 0 : null, PreviewEndMs = preview ? 30_000 : null,
                },
            };
        });
    }
}
