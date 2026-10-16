using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter.Tests;

/// <summary>Gate G0, headless half: every supported fixture survives parse → IR → encode → parse.</summary>
public class RoundTripTests
{
    private static IEnumerable<string> fixtureNames() => Fixtures.OsuNames();

    [TestCaseSource(nameof(fixtureNames))]
    public void FixtureRoundTripsWithoutUnexpectedLoss(string name)
    {
        var report = RoundTrip.Run(Fixtures.Read(name));

        Assert.Multiple(() =>
        {
            Assert.That(report.ImportDiagnostics.Where(d => d.Severity == Severity.Error), Is.Empty);
            Assert.That(report.Differences, Is.Empty);
            Assert.That(report.EncoderIsStable, "encoding the re-imported map changed the text");
            Assert.That(report.RoundTripDifficulty.StarRating, Is.EqualTo(report.OriginalDifficulty.StarRating).Within(RoundTrip.StarTolerance));
            Assert.That(report.EncodedText, Does.StartWith("osu file format v128"));
        });
    }

    [Test]
    public void OnlyDeclaredLossIsTheCustomSampleFile()
    {
        var declared = Fixtures.OsuNames()
                               .SelectMany(n => RoundTrip.Run(Fixtures.Read(n)).DeclaredLosses.Select(d => $"{n}:{d.Code}"))
                               .ToArray();

        Assert.That(declared, Is.EqualTo(new[] { "samples-kiai.osu:profile.loss.sample-file" }));
    }

    [Test]
    public void ComparerNoticesAChangedObject()
    {
        var ir = OsuImporter.Import(Fixtures.Read("simple-120bpm.osu")).Beatmap;
        var moved = ir with { Objects = [ir.Objects[0] with { TimeUs = ir.Objects[0].TimeUs + 2 }, .. ir.Objects.Skip(1)] };

        Assert.That(SemanticComparer.Compare(ir, moved).Select(d => d.Path), Is.EqualTo(new[] { "objects[0].timeUs" }));
    }
}
