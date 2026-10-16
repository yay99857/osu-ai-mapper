using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

/// <summary>
/// Pulso v2 predicts how far each note is from the previous one. Placement uses that distance and the
/// spacing factor only scales it, so a high star search can no longer throw every 1/2 jump across the
/// screen (the v1 search reached ~10 circle radii where human maps of the same stars have 3-4.5).
/// </summary>
public class LearnedSpacingTests
{
    private static MappingPlan Plan(Func<int, double?> distance)
    {
        var events = Enumerable.Range(0, 24).Select(k => new PlannedEvent
        {
            GridIndex = 2 * k,
            TimeUs = 2 * k * 100_000L,
            Kind = EventKind.Attack,
            Importance = 0.5,
            Intensity = 2,
            TemplateId = 0,
            DistanceRadii = distance(k),
        }).ToList();
        return new MappingPlan(events, new Dictionary<int, int>());
    }

    private static double[] Distances(MappingPlan plan, double factor)
    {
        var placed = ProceduralPlacement.Place(plan, new PlacementSettings { CircleSize = 4, SliderMultiplier = 1.8, SpacingFactor = factor, Seed = 1 }, 400_000);
        return placed.Zip(placed.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.Position.X - a.EndPosition.X, 2) + Math.Pow(b.Position.Y - a.EndPosition.Y, 2))).ToArray();
    }

    [Test]
    public void LearnedDistanceIsUsedAndScaledByTheFactor()
    {
        double radius = 54.4 - 4.48 * 4;
        var near = Distances(Plan(_ => 3.0), 1.0);
        var far = Distances(Plan(_ => 3.0), 1.3);

        // Clearance rules may move a note a little; the median keeps the learned distance.
        Assert.That(Median(near) / radius, Is.EqualTo(3.0).Within(0.4));
        Assert.That(Median(far) / Median(near), Is.EqualTo(1.3).Within(0.15));
    }

    [Test]
    public void LearnedDistancesVaryWithThePrediction()
    {
        double radius = 54.4 - 4.48 * 4;
        var d = Distances(Plan(k => k < 12 ? 2.5 : 5.0), 1.0);
        Assert.That(Median(d[12..]) / Median(d[..11]), Is.GreaterThan(1.6));
        Assert.That(Median(d[..11]) / radius, Is.LessThan(3.2));
    }

    [Test]
    public void TheSearchWidensLoudPassagesMoreThanCalmOnes()
    {
        Assert.That(ProceduralPlacement.LoudnessScaled(1.0, 0), Is.EqualTo(1.0));
        Assert.That(ProceduralPlacement.LoudnessScaled(1.0, 3), Is.EqualTo(1.0));
        Assert.That(ProceduralPlacement.LoudnessScaled(1.5, 0), Is.LessThan(1.15));
        Assert.That(ProceduralPlacement.LoudnessScaled(1.5, 3), Is.GreaterThan(1.6));
        Assert.That(ProceduralPlacement.LoudnessScaled(0.85, 3), Is.LessThan(ProceduralPlacement.LoudnessScaled(0.85, 0)));
    }

    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
