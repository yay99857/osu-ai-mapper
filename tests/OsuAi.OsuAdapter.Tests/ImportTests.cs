using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter.Tests;

public class ImportTests
{
    private static BeatmapIr Import(string name) => OsuImporter.Import(Fixtures.Read(name)).Beatmap;

    [Test]
    public void ImportIsDeterministic() =>
        Assert.That(IrJson.Serialize(Import("slider-heavy.osu")), Is.EqualTo(IrJson.Serialize(Import("slider-heavy.osu"))));

    [Test]
    public void EarlyFormatVersionsGetTheOfficialOffset()
    {
        // The official decoder shifts v4 and older files by 24 ms; the IR records the shifted time,
        // which re-encodes as v128 without the shift.
        var map = Import("legacy-old-version.osu");

        Assert.That(map.Objects[0].TimeUs, Is.EqualTo(1_024_000));
        Assert.That(map.Timing.Segments[0].StartTimeUs, Is.EqualTo(1_024_000));
        Assert.That(map.Provenance.SourceFormatVersion, Is.EqualTo(4));
    }

    [Test]
    public void NegativeOffsetIsKept()
    {
        var map = Import("offset-negative.osu");

        Assert.That(map.Timing.Segments[0].StartTimeUs, Is.EqualTo(-150_000));
        Assert.That(map.Timing.TimeUsAt(Rational.FromInteger(2)), Is.EqualTo(map.Objects[0].TimeUs));
    }

    [Test]
    public void StackedObjectsKeepAuthoredPositions()
    {
        var map = Import("stacking.osu");

        Assert.That(map.Objects.Take(5).Select(o => o.Position), Is.All.EqualTo(new Vector2D(200, 200)));
    }

    [Test]
    public void VariableTimingContinuesBeatNumbering()
    {
        var segments = Import("variable-bpm.osu").Timing.Segments;

        Assert.That(segments.Select(s => s.StartBeat), Is.EqualTo(new[] { Rational.Zero, Rational.FromInteger(14), Rational.FromInteger(23) }));
        Assert.That(segments.Select(s => s.Meter!.Numerator), Is.EqualTo(new[] { 4, 3, 4 }));
        Assert.That(segments[1].BeatLengthUs, Is.EqualTo(333_333.333333333).Within(1e-3));
    }

    [Test]
    public void SliderVelocityLivesOnTheSlider()
    {
        var sliders = Import("variable-bpm.osu").Objects.OfType<SliderIr>().ToArray();

        Assert.That(sliders.Select(s => s.SvMultiplier), Is.EqualTo(new[] { 2.0, 0.75 }).Within(1e-9));
    }

    [Test]
    public void LazerPathTypesAreKept()
    {
        var sliders = Import("lazer-path.osu").Objects.OfType<SliderIr>().ToArray();
        var types = sliders[0].Path.ControlPoints.Where(p => p.SegmentType is not null).Select(p => p.SegmentType);

        Assert.That(types, Is.EqualTo(new SegmentType?[] { SegmentType.Bezier, SegmentType.Linear, SegmentType.PerfectCurve }));
        Assert.That(sliders[1].Path.ControlPoints[0].SegmentType, Is.EqualTo(SegmentType.BSpline));
        Assert.That(sliders[1].Path.ControlPoints[0].Degree, Is.EqualTo(4));
        Assert.That(sliders[2].SpanCount, Is.EqualTo(2));
    }

    [Test]
    public void RepeatNodeSamplesMatchSpanCount()
    {
        foreach (var slider in Import("slider-repeat.osu").Objects.OfType<SliderIr>())
            Assert.That(slider.NodeSamples, Has.Count.EqualTo(slider.SpanCount + 1));
    }

    [Test]
    public void SliderComputedValuesComeFromTheEngine()
    {
        var slider = Import("simple-120bpm.osu").Objects.OfType<SliderIr>().First();

        // 128 px at SliderMultiplier 1.4 (140 px per beat) and 500 ms beats.
        Assert.That(slider.Computed!.DurationUs, Is.EqualTo(457_143).Within(1));
        Assert.That(slider.Computed.EndPosition, Is.EqualTo(new Vector2D(384, 288)));
        Assert.That(slider.Computed.Engine, Is.EqualTo(OfficialEngine.Version));
    }

    [Test]
    public void UnicodeMetadataIsKept()
    {
        var metadata = Import("unicode.osu").Metadata;

        Assert.That(metadata.TitleUnicode, Is.EqualTo("風の歌"));
        Assert.That(metadata.ArtistUnicode, Is.EqualTo("新星フィクスチャ"));
        Assert.That(metadata.Tags, Does.Contain("テスト"));
    }

    [Test]
    public void ZeroOnlineIdsAreNotInvented() => Assert.That(Import("simple-120bpm.osu").Metadata.OnlineIds, Is.Null);

    [TestCase("Mode: 3")]
    [TestCase("Mode:1")]
    [TestCase("Mode : 2")]
    public void OtherRulesetsAreRefusedEvenWhenNotInstalled(string modeLine)
    {
        string text = System.Text.Encoding.UTF8.GetString(Fixtures.Read("simple-120bpm.osu")).Replace("Mode: 0", modeLine);

        Assert.Throws<NotSupportedException>(() => OsuImporter.Import(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Test]
    public void MissingModeMeansStandard() =>
        Assert.That(OsuImporter.ReadDeclaredMode("osu file format v4\n[General]\nAudioFilename: a.mp3\n"), Is.Zero);
}
