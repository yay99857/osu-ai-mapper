using OsuAi.Application;

namespace OsuAi.Application.Tests;

public class EnvironmentDoctorTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "osu-ai-doctor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private ToolEnvironment Tools(string uv = "uv-not-needed") => new(root, uv);

    [Test]
    public async Task AMissingCheckoutIsAnErrorWithTheSetupAsFix()
    {
        var checks = await EnvironmentDoctor.MapperatorinatorAsync(Tools(), CancellationToken.None);

        Assert.That(checks, Has.Count.EqualTo(1));
        Assert.That(checks[0].Status, Is.EqualTo(CheckStatus.Error));
        Assert.That(checks[0].Detail, Does.Contain("checkout não encontrado"));
        Assert.That(checks[0].Fix, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ACheckoutWithoutItsEnvironmentIsAnError()
    {
        var tools = Tools();
        Directory.CreateDirectory(tools.MapperatorinatorRoot);
        File.WriteAllText(Path.Combine(tools.MapperatorinatorRoot, "inference.py"), "");

        var checks = await EnvironmentDoctor.MapperatorinatorAsync(tools, CancellationToken.None);

        Assert.That(checks.Single().Status, Is.EqualTo(CheckStatus.Error));
        Assert.That(checks.Single().Detail, Does.Contain("sem ambiente Python"));
    }

    [Test]
    public async Task AMissingUvStopsAGeneration()
    {
        var checks = await EnvironmentDoctor.RunAsync(Tools("uv-that-does-not-exist"), CancellationToken.None);

        var uv = checks.Single(c => c.Name == "uv");
        Assert.That(uv.Status, Is.EqualTo(CheckStatus.Error));
        Assert.That(uv.Fix, Does.Contain("uv"));
    }

    [Test]
    public void TheAnalysisEnvironmentIsOnlyAWarningWhenMissing()
    {
        // uv run creates it on the first analysis, so its absence makes that analysis slower only.
        Assert.That(EnvironmentDoctor.AnalysisEnvironment(Tools()).Status, Is.EqualTo(CheckStatus.Warning));
    }

    [TestCase("5884d1bcb57494f09fe3d74f3a6b58ca0d2960aa", "5884d1bcb57494f09fe3d74f3a6b58ca0d2960aa\n", CheckStatus.Ok)]
    [TestCase("5884D1BCB57494F09FE3D74F3A6B58CA0D2960AA", "5884d1bcb57494f09fe3d74f3a6b58ca0d2960aa", CheckStatus.Ok)]
    [TestCase("0123456789abcdef0123456789abcdef01234567", "5884d1bcb57494f09fe3d74f3a6b58ca0d2960aa", CheckStatus.Warning)]
    public void TheCheckoutIsComparedWithTheVerifiedRevision(string current, string verified, CheckStatus expected)
    {
        var check = EnvironmentDoctor.Revision(current, verified);

        Assert.That(check.Status, Is.EqualTo(expected));
        if (expected == CheckStatus.Warning)
            Assert.That(check.Detail, Does.Contain("0123456").And.Contain("5884d1b"));
    }

    [Test]
    public void LowDiskAndMemoryAreWarnings()
    {
        Assert.That(EnvironmentDoctor.Space(EnvironmentDoctor.MinFreeDiskBytes).Status, Is.EqualTo(CheckStatus.Ok));
        Assert.That(EnvironmentDoctor.Space(EnvironmentDoctor.MinFreeDiskBytes - 1).Status, Is.EqualTo(CheckStatus.Warning));
        Assert.That(EnvironmentDoctor.Memory(EnvironmentDoctor.MinAvailableMemoryBytes)!.Status, Is.EqualTo(CheckStatus.Ok));
        Assert.That(EnvironmentDoctor.Memory(1L << 30)!.Status, Is.EqualTo(CheckStatus.Warning));
        Assert.That(EnvironmentDoctor.Memory(null), Is.Null);
    }

    [Test]
    public void AvailableMemoryIsReadFromMeminfo()
    {
        const string meminfo = "MemTotal:       16303960 kB\nMemFree:          658432 kB\nMemAvailable:    4718592 kB\nBuffers:            1234 kB\n";

        Assert.That(EnvironmentDoctor.ParseMemAvailable(meminfo), Is.EqualTo(4718592L * 1024));
        Assert.That(EnvironmentDoctor.ParseMemAvailable("MemTotal: 1 kB\n"), Is.Null);
        Assert.That(EnvironmentDoctor.ReadAvailableMemory(Path.Combine(root, "no-meminfo")), Is.Null);
    }
}
