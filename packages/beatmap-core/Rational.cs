using System.Numerics;
using System.Text.Json.Serialization;

namespace OsuAi.BeatmapCore;

/// <summary>
/// An exact fraction of beats. Musical positions are stored this way so that walking a grid
/// never accumulates floating point error: beat 3/4 stays 3/4 no matter how it was reached.
/// Always normalised: denominator positive, gcd(numerator, denominator) == 1.
/// </summary>
public readonly record struct Rational : IComparable<Rational>
{
    public long Numerator { get; }
    public long Denominator { get; }

    [JsonConstructor]
    public Rational(long numerator, long denominator)
    {
        if (denominator == 0)
            throw new ArgumentException("Denominator cannot be zero.", nameof(denominator));

        if (denominator < 0)
        {
            numerator = checked(-numerator);
            denominator = checked(-denominator);
        }

        long gcd = (long)BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / gcd;
        Denominator = denominator / gcd;
    }

    public static Rational Zero => new(0, 1);

    public static Rational FromInteger(long value) => new(value, 1);

    public double ToDouble() => (double)Numerator / Denominator;

    public static Rational operator +(Rational a, Rational b) =>
        Reduce((BigInteger)a.Numerator * b.Denominator + (BigInteger)b.Numerator * a.Denominator, (BigInteger)a.Denominator * b.Denominator);

    public static Rational operator -(Rational a, Rational b) =>
        Reduce((BigInteger)a.Numerator * b.Denominator - (BigInteger)b.Numerator * a.Denominator, (BigInteger)a.Denominator * b.Denominator);

    public static Rational operator *(Rational a, Rational b) =>
        Reduce((BigInteger)a.Numerator * b.Numerator, (BigInteger)a.Denominator * b.Denominator);

    public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;
    public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;
    public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;

    public int CompareTo(Rational other) =>
        ((BigInteger)Numerator * other.Denominator).CompareTo((BigInteger)other.Numerator * Denominator);

    public override string ToString() => Denominator == 1 ? $"{Numerator}" : $"{Numerator}/{Denominator}";

    private static Rational Reduce(BigInteger numerator, BigInteger denominator)
    {
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return new Rational((long)(numerator / gcd), (long)(denominator / gcd));
    }
}
