using OsuAi.BeatmapCore;

namespace OsuAi.BeatmapCore.Tests;

public class TimingMapTests
{
    private static TimingMap Constant(double bpm, long offsetUs) => new()
    {
        Segments =
        [
            new TimingSegment
            {
                Id = "t0",
                StartTimeUs = offsetUs,
                StartBeat = Rational.Zero,
                BeatLengthUs = 60_000_000 / bpm,
                Source = TimingSource.Manual,
            },
        ],
    };

    [Test]
    public void BlueprintExampleAt182Bpm()
    {
        // 06-intermediate-representations: 182 BPM, offset 638 ms.
        var map = Constant(182, 638_000);

        Assert.That(map.TimeUsAt(Rational.FromInteger(1)), Is.EqualTo(967_670.33).Within(0.01));
        Assert.That(map.TimeUsAt(Rational.FromInteger(2)), Is.EqualTo(1_297_340.66).Within(0.01));
        Assert.That(map.TimeUsAt(Rational.FromInteger(3)), Is.EqualTo(1_627_010.99).Within(0.01));
    }

    [Test]
    public void FarBeatsDoNotAccumulateError()
    {
        var map = Constant(182, 638_000);
        double beatLength = 60_000_000.0 / 182;

        var beat = Rational.Zero;
        for (int i = 0; i < 4 * 2000; i++)
            beat += new Rational(1, 4);

        Assert.That(map.TimeUsAt(beat), Is.EqualTo(638_000 + 2000 * beatLength).Within(1e-6));
    }

    [Test]
    public void NegativeOffsetAndInverse()
    {
        var map = Constant(150, -150_000);

        Assert.That(map.TimeUsAt(Rational.Zero), Is.EqualTo(-150_000));
        Assert.That(map.BeatAt(map.TimeUsAt(new Rational(7, 4))), Is.EqualTo(1.75).Within(1e-12));
    }

    [Test]
    public void LooksUpTheOwningSegment()
    {
        var map = new TimingMap
        {
            Segments =
            [
                new TimingSegment { Id = "a", StartTimeUs = 0, StartBeat = Rational.Zero, BeatLengthUs = 500_000, Source = TimingSource.Manual },
                new TimingSegment { Id = "b", StartTimeUs = 2_000_000, StartBeat = Rational.FromInteger(4), BeatLengthUs = 250_000, Source = TimingSource.Manual },
            ],
        };

        Assert.That(map.TimeUsAt(new Rational(7, 2)), Is.EqualTo(1_750_000));
        Assert.That(map.TimeUsAt(Rational.FromInteger(6)), Is.EqualTo(2_500_000));
        Assert.That(map.SegmentAtTime(1_999_999).Id, Is.EqualTo("a"));
        Assert.That(map.SegmentAtTime(2_000_000).Id, Is.EqualTo("b"));
        Assert.That(map.SegmentAtTime(-5).Id, Is.EqualTo("a"));
    }
}
