using OsuAi.BeatmapCore;

namespace OsuAi.BeatmapCore.Tests;

public class RationalTests
{
    [Test]
    public void IsNormalised()
    {
        var r = new Rational(6, -8);

        Assert.That(r.Numerator, Is.EqualTo(-3));
        Assert.That(r.Denominator, Is.EqualTo(4));
        Assert.That(r, Is.EqualTo(new Rational(-3, 4)));
    }

    [Test]
    public void ZeroDenominatorIsRejected() =>
        Assert.Throws<ArgumentException>(() => _ = new Rational(1, 0));

    [Test]
    public void ArithmeticIsExact()
    {
        var sum = Rational.Zero;

        // A tenth of a beat added 10 000 times is exactly 1 000 beats, which a double sum is not.
        for (int i = 0; i < 10_000; i++)
            sum += new Rational(1, 10);

        Assert.That(sum, Is.EqualTo(Rational.FromInteger(1000)));
        Assert.That(new Rational(1, 3) * new Rational(3, 4), Is.EqualTo(new Rational(1, 4)));
        Assert.That(new Rational(1, 2) - new Rational(3, 4), Is.EqualTo(new Rational(-1, 4)));
    }

    [Test]
    public void Ordering()
    {
        Assert.That(new Rational(1, 3) < new Rational(1, 2));
        Assert.That(new Rational(-1, 2) < Rational.Zero);
        Assert.That(new Rational(2, 4).CompareTo(new Rational(1, 2)), Is.Zero);
    }
}
