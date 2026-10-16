using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

/// <summary>The Python analyzer and the C# reader agree on packages/contracts/examples/song-analysis-minimal.json.</summary>
public class ContractTests
{
    private static string Example => Path.Combine(TestContext.CurrentContext.TestDirectory, "contracts", "song-analysis-minimal.json");

    [Test]
    public void ReadsTheAnalyzerExample()
    {
        var analysis = SongAnalysis.Load(Example);

        Assert.That(analysis.Timing.Bpm, Is.EqualTo(150).Within(0.05));
        Assert.That(analysis.Timing.Source, Is.EqualTo("detected"));
        Assert.That(analysis.Grid.First(p => p.Index == 0).TimeUs, Is.EqualTo(analysis.Timing.OffsetUs));
        Assert.That(analysis.Sections, Is.Not.Empty);
    }

    [Test]
    public void RefusesAnotherSchemaVersion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"analysis-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, File.ReadAllText(Example).Replace("\"poc-0.3\"", "\"poc-0.1\""));

        try
        {
            Assert.Throws<InvalidDataException>(() => SongAnalysis.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
