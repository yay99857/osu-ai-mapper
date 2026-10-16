using System.Text.Json;
using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter.Tests;

/// <summary>
/// Golden difficulty values for the pinned engine. A failure after an osu! upgrade is expected:
/// review the change, regenerate with `osu-ai-harness snapshot`, and treat old labels as a different
/// version. Never update this file to make a test pass without that review.
/// </summary>
public class DifficultyTests
{
    private static IEnumerable<string> fixtureNames() => Fixtures.OsuNames();

    private static Dictionary<string, JsonElement> golden = null!;

    [OneTimeSetUp]
    public void LoadGolden()
    {
        string file = Path.Combine(Fixtures.Root, "golden", "difficulty-osu-2026.916.0.json");
        golden = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file))!;
    }

    [Test]
    public void EngineIsThePinnedRelease() => Assert.That(OfficialEngine.Version, Is.EqualTo("ppy.osu.Game 2026.916.0"));

    [TestCaseSource(nameof(fixtureNames))]
    public void MatchesGoldenSnapshot(string name)
    {
        var expected = golden[name];
        var actual = DifficultyService.CalculateFromOsuText(File.ReadAllText(Path.Combine(Fixtures.OsuDirectory, name)));

        Assert.Multiple(() =>
        {
            Assert.That(actual.StarRating, Is.EqualTo(expected.GetProperty("starRating").GetDouble()).Within(1e-12));
            Assert.That(actual.Aim, Is.EqualTo(expected.GetProperty("aim").GetDouble()).Within(1e-12));
            Assert.That(actual.Speed, Is.EqualTo(expected.GetProperty("speed").GetDouble()).Within(1e-12));
            Assert.That(actual.MaxCombo, Is.EqualTo(expected.GetProperty("maxCombo").GetInt32()));
            Assert.That(actual.Engine, Is.EqualTo(expected.GetProperty("engine").GetString()));
        });
    }

    [Test]
    public void IrAndFileRateTheSame()
    {
        var bytes = Fixtures.Read("stream-heavy.osu");
        var fromIr = DifficultyService.Calculate(OsuImporter.Import(bytes).Beatmap);
        var fromFile = DifficultyService.CalculateFromOsuText(System.Text.Encoding.UTF8.GetString(bytes));

        Assert.That(fromIr.StarRating, Is.EqualTo(fromFile.StarRating).Within(1e-12));
    }

    [Test]
    public void ProfileNamesTheLabelSpace()
    {
        var report = DifficultyService.CalculateFromOsuText(File.ReadAllText(Path.Combine(Fixtures.OsuDirectory, "spinner.osu")));
        Assert.That(report.Profile, Is.EqualTo("osu-2026.916.0-nomod"));
    }
}
