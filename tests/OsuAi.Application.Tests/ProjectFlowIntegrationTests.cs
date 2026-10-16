using System.IO.Compression;
using System.Text;
using OsuAi.Application;
using OsuAi.BeatmapCore;
using OsuAi.OsuAdapter;
using OsuAi.ProjectStore;

namespace OsuAi.Application.Tests;

/// <summary>
/// QA-01: the whole project flow with the real tools. Import, generate, rename, save, reopen,
/// export, on a synthetic drum track we own. Needs uv and the analyzer environment; skipped when
/// uv is not on the PATH (the .NET CI job), run locally and in a job that installs uv.
/// </summary>
[Category("Integration")]
public class ProjectFlowIntegrationTests
{
    private string work = null!;

    [SetUp]
    public void SetUp()
    {
        if (!UvAvailable())
            Assert.Ignore("uv is not on the PATH; the integration flow needs the Python tools.");

        work = Path.Combine(Path.GetTempPath(), "osu-ai-flow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
    }

    [TearDown]
    public void TearDown()
    {
        if (work is not null && Directory.Exists(work))
            Directory.Delete(work, recursive: true);
    }

    [Test]
    [CancelAfter(600_000)]
    public async Task ImportGenerateRenameReopenExport()
    {
        string audio = Path.Combine(work, "drums.wav");
        WriteDrumTrack(audio, bpm: 170, seconds: 40);
        var service = new MappingService(ToolEnvironment.Discover());

        var project = service.CreateProject(Path.Combine(work, "project"), audio,
            new ProjectMetadata { Title = "Drums", Artist = "Tests", Creator = "Mapper", DifficultyName = "Insane" },
            new ProjectSettings { TargetStars = 4 });

        var generated = await service.GenerateAsync(project, null, CancellationToken.None);
        Assert.That(generated.Beatmap.Objects, Is.Not.Empty);
        Assert.That(project.Manifest.Analysis, Is.Not.Null);

        // Rename: a metadata revision, nothing analysed or generated again.
        string analysisKey = project.Manifest.Analysis!.Key;
        var renamed = service.UpdateMetadata(project, project.Manifest.Metadata with { Title = "Drums (renamed)", Creator = "Another Mapper" })!;

        // Reopen from disk and export.
        var reopened = Project.Open(project.Root);
        Assert.That(reopened.ActiveRevision!.Id, Is.EqualTo(renamed.Id));
        Assert.That(reopened.Manifest.Analysis!.Key, Is.EqualTo(analysisKey));
        string package = service.Export(reopened);

        // Analysing again reuses the cache.
        var again = await service.AnalyseAsync(reopened, null, CancellationToken.None);
        Assert.That(again.Reused);

        using var zip = ZipFile.OpenRead(package);
        using var reader = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".osu")).Open());
        var exported = OsuImporter.Import(Encoding.UTF8.GetBytes(reader.ReadToEnd())).Beatmap;
        Assert.That(exported.Metadata.Title, Is.EqualTo("Drums (renamed)"));
        Assert.That(exported.Metadata.Creator, Is.EqualTo("Another Mapper"));
        Assert.That(exported.Objects.Select(o => o.TimeUs), Is.EqualTo(generated.Beatmap.Objects.Select(o => o.TimeUs)));
        Assert.That(zip.Entries.Select(e => e.FullName), Does.Contain("audio.mp3"));
        Assert.That(Math.Abs(exported.Timing.Segments[0].BeatLengthUs - 60_000_000 / 170.0), Is.LessThan(100), "170 BPM detected");
    }

    [Test]
    [CancelAfter(120_000)]
    public void CancellingAnalysisLeavesTheProjectWithoutAnalysis()
    {
        string audio = Path.Combine(work, "drums.wav");
        WriteDrumTrack(audio, bpm: 150, seconds: 30);
        var service = new MappingService(ToolEnvironment.Discover());
        var project = service.CreateProject(Path.Combine(work, "project"), audio,
            new ProjectMetadata { Title = "Drums", Artist = "Tests", Creator = "Mapper", DifficultyName = "Insane" }, new ProjectSettings());

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.CatchAsync<OperationCanceledException>(() => service.AnalyseAsync(project, null, cancel.Token));

        var reopened = Project.Open(project.Root);
        Assert.That(reopened.Manifest.Analysis, Is.Null);
        Assert.That(Directory.GetDirectories(reopened.PathOf("analysis")), Is.Empty, "staging removed");
    }

    private static bool UvAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("uv", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Kick on 1 and the "and" of 3, snare on 2 and 4, hats on eighths: audio we own.</summary>
    private static void WriteDrumTrack(string path, double bpm, double seconds)
    {
        const int rate = 44_100;
        var samples = new float[(int)(seconds * rate)];
        double beat = 60 / bpm;
        var random = new Random(1);

        void Hit(double at, Func<double, double> shape, double length)
        {
            int start = (int)(at * rate);
            for (int i = 0; i < length * rate && start + i < samples.Length; i++)
                samples[start + i] += (float)shape(i / (double)rate);
        }

        for (double bar = 0.3; bar < seconds; bar += 4 * beat)
        {
            foreach (double k in new[] { 0, 2.5 })
                Hit(bar + k * beat, t => 0.8 * Math.Sin(2 * Math.PI * (55 + 90 * Math.Exp(-t * 40)) * t) * Math.Exp(-t * 25), 0.12);
            foreach (double s in new[] { 1.0, 3.0 })
                Hit(bar + s * beat, t => 0.4 * (random.NextDouble() * 2 - 1) * Math.Exp(-t * 35), 0.12);
            for (int h = 0; h < 8; h++)
                Hit(bar + h * beat / 2, t => 0.12 * (random.NextDouble() * 2 - 1) * Math.Exp(-t * 90), 0.05);
        }

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (float s in samples)
            writer.Write((short)(Math.Clamp(s, -1f, 1f) * short.MaxValue));
    }
}
