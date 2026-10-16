using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

public class BurstLayoutTests
{
    private static double StackedShare(double quarterMs) =>
        Enumerable.Range(0, 2000).Count(seed => ProceduralPlacement.BurstLayout(quarterMs, seed).Stacked) / 2000.0;

    [Test]
    public void FastBurstsMostlyStackAndSlowOnesSpreadLikeJumps()
    {
        Assert.That(StackedShare(80), Is.InRange(0.6, 0.8));
        Assert.That(StackedShare(110), Is.InRange(0.25, 0.45));
        Assert.That(StackedShare(140), Is.LessThan(0.15));
        Assert.That(ProceduralPlacement.BurstLayout(140, 1).StepRadii, Is.GreaterThan(ProceduralPlacement.BurstLayout(80, 1).StepRadii * 2));
    }

    [Test]
    public void StackedTripletsShareOneSpotAfterTheirFirstNote()
    {
        // A jump, then X Z X at 1/4 (80 ms): when the layout says stacked, the three notes coincide.
        int seedForStack = Enumerable.Range(0, 200).First(s => ProceduralPlacement.BurstLayout(80, 12 * 19 + s).Stacked);
        var events = new[] { 0, 4, 8, 12, 13, 14, 20, 24 }.Select(g => new PlannedEvent
        {
            GridIndex = g, TimeUs = g * 80_000L, Kind = EventKind.Attack, Importance = 0.5, Intensity = 2, TemplateId = 0, DistanceRadii = 4,
        }).ToList();
        var placed = ProceduralPlacement.Place(new MappingPlan(events, new Dictionary<int, int>()),
            new PlacementSettings { CircleSize = 4, SliderMultiplier = 1.8, SpacingFactor = 1, Seed = seedForStack, Tier = DifficultyTier.Expert }, 320_000);
        Assert.That(placed[4].Position, Is.EqualTo(placed[3].Position));
        Assert.That(placed[5].Position, Is.EqualTo(placed[3].Position));
        Assert.That(placed[3].Position.X - placed[2].Position.X, Is.Not.EqualTo(0).Or.Not.EqualTo(placed[3].Position.Y - placed[2].Position.Y));
    }

    [Test]
    public void EachBurstDecidesForItself()
    {
        // Twenty triplets at 1/4 = 80 ms: about 70% stacked, so both kinds must appear.
        var grid = Enumerable.Range(0, 20).SelectMany(k => new[] { k * 16, k * 16 + 1, k * 16 + 2, k * 16 + 8 }).ToArray();
        var events = grid.Select(g => new PlannedEvent { GridIndex = g, TimeUs = g * 80_000L, Kind = EventKind.Attack, Importance = 0.5, Intensity = 2, TemplateId = 0, DistanceRadii = 4 }).ToList();
        var placed = ProceduralPlacement.Place(new MappingPlan(events, new Dictionary<int, int>()),
            new PlacementSettings { CircleSize = 4, SliderMultiplier = 1.8, SpacingFactor = 1, Seed = 3, Tier = DifficultyTier.Expert }, 320_000);
        int stacked = Enumerable.Range(0, 20).Count(k => placed[k * 4 + 2].Position == placed[k * 4].Position);
        Assert.That(stacked, Is.InRange(8, 19));
    }

    [Test]
    public void StreamsWidenWithTheMusicAndAreNeverStacked()
    {
        Assert.That(ProceduralPlacement.StreamStep(DifficultyTier.Insane, 3), Is.GreaterThan(ProceduralPlacement.StreamStep(DifficultyTier.Insane, 0)));
        Assert.That(ProceduralPlacement.StreamStep(DifficultyTier.Expert, 3), Is.GreaterThan(ProceduralPlacement.StreamStep(DifficultyTier.Insane, 3)));
        // Seven 1/4 notes at 80 ms in Insane: a stream, not a stack.
        var events = new[] { 0, 4, 8, 9, 10, 11, 12, 13, 14, 20 }.Select(g => new PlannedEvent
        {
            GridIndex = g, TimeUs = g * 80_000L, Kind = EventKind.Attack, Importance = 0.5, Intensity = 3, TemplateId = 0, DistanceRadii = 4,
        }).ToList();
        var placed = ProceduralPlacement.Place(new MappingPlan(events, new Dictionary<int, int>()),
            new PlacementSettings { CircleSize = 4, SliderMultiplier = 1.8, SpacingFactor = 1, Seed = 1, Tier = DifficultyTier.Insane }, 320_000);
        double radius = 54.4 - 4.48 * 4;
        var steps = Enumerable.Range(3, 6).Select(i => Math.Sqrt(Math.Pow(placed[i].Position.X - placed[i - 1].Position.X, 2) + Math.Pow(placed[i].Position.Y - placed[i - 1].Position.Y, 2)) / radius).ToArray();
        Assert.That(steps.Average(), Is.InRange(0.8, 1.2));
    }
}
