using OsuAi.BeatmapCore;

namespace OsuAi.BeatmapCore.Tests;

public class BeatmapValidatorTests
{
    private static IEnumerable<string> Codes(BeatmapIr map) => BeatmapValidator.Validate(map).Select(d => d.Code);

    [Test]
    public void MinimalMapIsValid() => Assert.That(BeatmapValidator.Validate(SampleMaps.Minimal()), Is.Empty);

    [Test]
    public void UnsortedObjectsAreAnError()
    {
        var map = SampleMaps.Minimal();
        map = map with { Objects = map.Objects.Reverse().ToArray() };

        Assert.That(Codes(map), Does.Contain("object.order"));
        Assert.That(BeatmapValidator.Validate(map).HasErrors());
    }

    [Test]
    public void SameTimestampIsNotAnError()
    {
        var map = SampleMaps.Minimal();
        var second = (CircleIr)map.Objects[0] with { Id = Guid.NewGuid(), Position = new Vector2D(300, 300) };
        map = map with { Objects = [map.Objects[0], second, .. map.Objects.Skip(1)] };

        Assert.That(BeatmapValidator.Validate(map).HasErrors(), Is.False);
    }

    [Test]
    public void DuplicateIdsAreAnError()
    {
        var map = SampleMaps.Minimal();
        map = map with { Objects = [map.Objects[0], map.Objects[1] with { Id = SampleMaps.CircleId }, map.Objects[2]] };

        Assert.That(Codes(map), Does.Contain("object.duplicate-id"));
    }

    [Test]
    public void NodeSampleCardinalityFollowsSpanCount()
    {
        var map = SampleMaps.Minimal();
        var slider = (SliderIr)map.Objects[1] with { SpanCount = 3 };
        map = map with { Objects = [map.Objects[0], slider, map.Objects[2]] };

        Assert.That(Codes(map), Does.Contain("slider.node-samples"));
    }

    [Test]
    public void SliderPathMustStartAtHead()
    {
        var map = SampleMaps.Minimal();
        var slider = (SliderIr)map.Objects[1];
        slider = slider with
        {
            Path = new SliderPathIr
            {
                ControlPoints = [new PathControlPointIr { X = 5, Y = 0, SegmentType = SegmentType.Linear }, new PathControlPointIr { X = 140, Y = 0 }],
            },
        };
        map = map with { Objects = [map.Objects[0], slider, map.Objects[2]] };

        Assert.That(Codes(map), Does.Contain("slider.path.origin"));
    }

    [Test]
    public void DegreeOnlyOnBSpline()
    {
        var map = SampleMaps.Minimal();
        var slider = (SliderIr)map.Objects[1];
        slider = slider with
        {
            Path = new SliderPathIr
            {
                ControlPoints = [new PathControlPointIr { X = 0, Y = 0, SegmentType = SegmentType.Bezier, Degree = 3 }, new PathControlPointIr { X = 140, Y = 0 }],
            },
        };
        map = map with { Objects = [map.Objects[0], slider, map.Objects[2]] };

        Assert.That(Codes(map), Does.Contain("slider.path.degree"));
    }

    [Test]
    public void SpinnerMustHavePositiveDuration()
    {
        var map = SampleMaps.Minimal();
        var spinner = (SpinnerIr)map.Objects[2] with { EndTimeUs = 4_000_000 };
        map = map with { Objects = [map.Objects[0], map.Objects[1], spinner] };

        Assert.That(Codes(map), Does.Contain("spinner.duration"));
    }

    [Test]
    public void NonFiniteValuesAreAnError()
    {
        var map = SampleMaps.Minimal();
        map = map with
        {
            Settings = map.Settings with { Ar = double.NaN },
            Objects = [map.Objects[0] with { Position = new Vector2D(double.PositiveInfinity, 0) }, map.Objects[1], map.Objects[2]],
        };

        Assert.That(Codes(map), Does.Contain("settings.ar").And.Contain("object.position"));
    }

    [Test]
    public void TimingSegmentsMustAdvanceInTimeAndBeat()
    {
        var map = SampleMaps.Minimal();
        var first = map.Timing.Segments[0];
        map = map with
        {
            Timing = new TimingMap { Segments = [first, first with { Id = "t1", StartTimeUs = 2_000_000 }] },
        };

        Assert.That(Codes(map), Does.Contain("timing.order"));
    }

    [Test]
    public void DanglingLockIsAnError()
    {
        var map = SampleMaps.Minimal() with { Locks = [new ObjectLock(Guid.NewGuid(), ["position"])] };

        Assert.That(Codes(map), Does.Contain("lock.dangling"));
    }

    [Test]
    public void OutsidePlayfieldIsOnlyAWarning()
    {
        var map = SampleMaps.Minimal();
        map = map with { Objects = [map.Objects[0] with { Position = new Vector2D(-20, 100) }, map.Objects[1], map.Objects[2]] };

        var diagnostics = BeatmapValidator.Validate(map);
        Assert.That(diagnostics.Select(d => d.Code), Does.Contain("object.outside-playfield"));
        Assert.That(diagnostics.HasErrors(), Is.False);
    }

    [TestCase("audio.mp3", true)]
    [TestCase("assets/audio.mp3", true)]
    [TestCase("../audio.mp3", false)]
    [TestCase("assets/../../audio.mp3", false)]
    [TestCase("/audio.mp3", false)]
    [TestCase("C:/audio.mp3", false)]
    [TestCase(@"assets\audio.mp3", false)]
    [TestCase("assets//audio.mp3", false)]
    public void AssetPathsStayInsideTheProject(string path, bool valid) =>
        Assert.That(BeatmapValidator.IsContainedRelativePath(path), Is.EqualTo(valid));

    [Test]
    public void DeterministicGuidsAreStableVersion8()
    {
        var a = DeterministicGuid.Create("abc");

        Assert.That(DeterministicGuid.Create("abc"), Is.EqualTo(a));
        Assert.That(DeterministicGuid.Create("abd"), Is.Not.EqualTo(a));
        Assert.That(a.Version, Is.EqualTo(8));
    }
}
