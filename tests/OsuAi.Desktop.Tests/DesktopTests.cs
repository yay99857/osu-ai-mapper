using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using OsuAi.Application;
using OsuAi.BeatmapCore;
using OsuAi.Desktop.Services;
using OsuAi.Desktop.ViewModels;
using OsuAi.Desktop.Views;
using OsuAi.OsuAdapter;
using OsuAi.ProjectStore;

[assembly: AvaloniaTestApplication(typeof(OsuAi.Desktop.Tests.TestApp))]

namespace OsuAi.Desktop.Tests;

public static class TestApp
{
    // Real Skia rendering, so frames can be captured as images.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class DesktopTests
{
    private string work = null!;

    [SetUp]
    public void SetUp()
    {
        work = Path.Combine(Path.GetTempPath(), "osu-ai-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OsuAiMapper.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Run from the repository.");
    }

    private static MainViewModel NewViewModel()
    {
        var tools = new ToolEnvironment(RepositoryRoot(), uv: "uv-that-does-not-exist");
        return new MainViewModel(new MappingService(tools), new Preferences(), new AudioPlayer(), tools);
    }

    /// <summary>A project with one generated revision built from a fixture, as the generator would leave it.</summary>
    private string ProjectWithRevision()
    {
        string audio = Path.Combine(work, "Singer - Song.mp3");
        File.WriteAllBytes(audio, "not really audio"u8.ToArray());
        var tools = new ToolEnvironment(RepositoryRoot(), uv: "uv-that-does-not-exist");
        var service = new MappingService(tools);
        var project = service.CreateProject(Path.Combine(work, "project"), audio,
            new ProjectMetadata { Title = "Song", Artist = "Singer", Creator = "Mapper", DifficultyName = "Insane" }, new ProjectSettings());

        // Delivered audio and analysis, as after a real analysis.
        string key = service.AnalysisKey(project);
        string dir = project.PathOf($"analysis/{key}");
        Directory.CreateDirectory(dir);
        byte[] mp3 = "delivered"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir, "audio.mp3"), mp3);
        var example = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "packages", "contracts", "examples", "song-analysis-minimal.json")))!;
        example["audio"]!["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(mp3));
        File.WriteAllText(Path.Combine(dir, "analysis.json"), example.ToJsonString());
        project.Update(m => m with
        {
            Analysis = new AnalysisRef(key, $"analysis/{key}", tools.AnalyzerId, null),
            Audio = new ManagedFile($"analysis/{key}/audio.mp3", "x", mp3.Length),
        });

        var ir = OsuImporter.Import(File.ReadAllBytes(Path.Combine(RepositoryRoot(), "tests", "fixtures", "osu", "simple-120bpm.osu"))).Beatmap;
        ir = ir with
        {
            Metadata = ir.Metadata with { Title = "Song", Artist = "Singer", Creator = "Mapper", DifficultyName = "Insane", Tags = [MappingService.GeneratedTag, "b1"] },
            Assets = ir.Assets with { Audio = ir.Assets.Audio with { RelativePath = "audio.mp3" } },
        };
        project.AddRevision((staging, relative) =>
        {
            File.WriteAllText(Path.Combine(staging, "beatmap-ir.json"), IrJson.Serialize(ir));
            return new RevisionRecord
            {
                Id = project.NextRevisionId,
                Kind = RevisionKind.Generated,
                CreatedAt = DateTimeOffset.UtcNow,
                BeatmapIr = $"{relative}/beatmap-ir.json",
                AnalysisKey = key,
                Metadata = project.Manifest.Metadata,
                Generation = new GenerationRecord { Generator = "b1", Seed = 1, TargetStars = 5, ActualStars = 4.97, TargetReached = true, SpacingFactor = 2, DifficultyProfile = "test" },
            };
        });

        return project.Root;
    }

    [AvaloniaTest]
    public void ANewProjectStartsWithTheDefaultModel()
    {
        var vm = NewViewModel();
        string audio = Path.Combine(work, "Singer - Song.mp3");
        File.WriteAllBytes(audio, "not really audio"u8.ToArray());
        int expected = vm.ModelIndex;

        vm.CreateProject(audio, work);

        Assert.That(vm.ErrorText, Is.Empty);
        Assert.That(vm.ModelIndex, Is.EqualTo(expected));
        if (vm.Models.Count > 1)
            Assert.That(vm.ModelIndex, Is.Not.EqualTo(0), "not the rules");
    }

    [AvaloniaTest]
    public void OpeningAProjectFillsTheForm()
    {
        var vm = NewViewModel();
        vm.OpenProject(ProjectWithRevision());

        Assert.That(vm.ErrorText, Is.Empty);
        Assert.That(vm.HasProject);
        Assert.That(vm.Title, Is.EqualTo("Song"));
        Assert.That(vm.Artist, Is.EqualTo("Singer"));
        Assert.That(vm.Creator, Is.EqualTo("Mapper"));
        Assert.That(vm.Revisions, Has.Count.EqualTo(1));
        Assert.That(vm.AnalysisText, Does.Contain("BPM"));
        Assert.That(vm.ExportCommand.CanExecute(null));
    }

    [AvaloniaTest]
    public void InvalidMetadataIsExplainedAndNotSaved()
    {
        var vm = NewViewModel();
        vm.OpenProject(ProjectWithRevision());

        vm.Creator = "";
        vm.Title = "Song\nVersion: hack";

        Assert.That(vm.MetadataProblems, Does.Contain("mapper").And.Contain("quebras de linha"));
        vm.SaveMetadataCommand.Execute(null);
        Assert.That(vm.ErrorText, Is.Not.Empty);
        Assert.That(Project.Open(vm.ProjectPath!).Manifest.Revisions, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public void RenamingAddsARevisionAndExportCarriesIt()
    {
        var vm = NewViewModel();
        string root = ProjectWithRevision();
        vm.OpenProject(root);

        vm.Title = "Renamed";
        vm.Creator = "Someone";
        vm.SaveMetadataCommand.Execute(null);
        vm.ExportCommand.Execute(null);

        var project = Project.Open(root);
        Assert.That(vm.ErrorText, Is.Empty);
        Assert.That(project.Manifest.Revisions, Has.Count.EqualTo(2));
        Assert.That(project.ActiveRevision!.Kind, Is.EqualTo(RevisionKind.Metadata));
        Assert.That(vm.InfoText, Does.Contain("Renamed").And.Contain(".osz"));
        Assert.That(Directory.GetFiles(project.PathOf("exports"), "*.osz").Single(), Does.Contain("Someone"));
    }

    [AvaloniaTest]
    public void AnalysisFailureIsShownNotThrown()
    {
        // Changing the timing correction invalidates the cached analysis; the tools are absent here,
        // so analysing again fails, and the window must say so instead of crashing.
        var vm = NewViewModel();
        vm.OpenProject(ProjectWithRevision());
        vm.TimingBpm = 175;

        vm.AnalyseCommand.Execute(null);

        Assert.That(vm.ErrorText, Does.Contain("uv"));
        Assert.That(vm.IsBusy, Is.False);
    }

    // Screenshots for review only; the newer encoder-options overload adds nothing here.
#pragma warning disable CS0618
    [AvaloniaTest]
    public void RendersStartAndProjectScreens()
    {
        string output = Path.Combine(RepositoryRoot(), "artifacts", "ui");
        Directory.CreateDirectory(output);

        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1240, Height = 860 };
        window.Show();
        window.CaptureRenderedFrame()?.Save(Path.Combine(output, "start.png"));

        vm.OpenProject(ProjectWithRevision());
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(output, "project.png"));

        Assert.That(vm.Timeline, Is.Not.Null, "the timeline of the active revision");
        Assert.That(vm.Timeline!.ObjectTimesMs, Is.Not.Empty);
        Assert.That(vm.ReportSummary, Is.Not.Empty);
        Assert.That(vm.Revisions.Single().InPackage, "the active revision is packaged");

        // A drawn stand-in for a background picture, 16:10 so it gets cropped: a gradient with circles.
        string image = Path.Combine(work, "cover.png");
        var art = new Avalonia.Controls.Canvas
        {
            Width = 1600, Height = 1000,
            Background = new Avalonia.Media.LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new(Avalonia.Media.Color.Parse("#FF7AB6"), 0), new(Avalonia.Media.Color.Parse("#7B5CFF"), 0.55), new(Avalonia.Media.Color.Parse("#12082A"), 1) },
            },
        };
        foreach (var (x, y, r) in new[] { (1100.0, 180.0, 220.0), (300.0, 520.0, 140.0), (760.0, 640.0, 90.0) })
        {
            var circle = new Avalonia.Controls.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#40FFFFFF")) };
            Avalonia.Controls.Canvas.SetLeft(circle, x - r);
            Avalonia.Controls.Canvas.SetTop(circle, y - r);
            art.Children.Add(circle);
        }
        art.Measure(new Size(1600, 1000));
        art.Arrange(new Rect(0, 0, 1600, 1000));
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1600, 1000)))
        {
            bitmap.Render(art);
            bitmap.Save(image);
        }

        vm.SetBackground(image);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.That(vm.ErrorText, Is.Empty);
        Assert.That(vm.HasBackground && vm.BackgroundInfo.Contains("1600×900"), vm.BackgroundInfo + " / " + vm.InfoText);
        Assert.That(vm.InfoText, Does.Contain("16:9"));
        vm.StyleTags[0].IsSelected = vm.StyleTags[10].IsSelected = true;
        vm.MapperIndex = 1;
        int engine = vm.Models.ToList().FindIndex(m => OsuAi.Application.MapperatorinatorEngine.IsEngine(m.Path));
        if (engine >= 0)
            vm.ModelIndex = engine;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(output, "project-background.png"));
        window.Height = 1900;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(output, "project-full.png"));

        Assert.That(File.Exists(Path.Combine(output, "project.png")));
    }
#pragma warning restore CS0618
}
