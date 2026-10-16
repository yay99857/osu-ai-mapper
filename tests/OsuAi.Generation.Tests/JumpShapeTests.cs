using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

public class JumpShapeTests
{
    [Test]
    public void OneTwoJumpsAlternateBetweenTwoSpots()
    {
        // Circles every 1/2 beat; find a seed whose first combo uses the 1-2 shape.
        var events = Enumerable.Range(0, 64).Select(k => new PlannedEvent
        {
            GridIndex = 2 * k, TimeUs = 2 * k * 80_000L, Kind = EventKind.Attack, Importance = 0.5, Intensity = 2, TemplateId = 0, DistanceRadii = 4,
        }).ToList();
        var plan = new MappingPlan(events, new Dictionary<int, int>());
        var found = Enumerable.Range(0, 60).Select(seed => ProceduralPlacement.Place(plan,
                new PlacementSettings { CircleSize = 4, SliderMultiplier = 1.8, SpacingFactor = 1, Seed = seed, Tier = DifficultyTier.Insane }, 320_000))
            .FirstOrDefault(p => p.Any(o => o.Pattern == "Alternate"));
        Assert.That(found, Is.Not.Null, "some seed picks the 1-2 shape");
        var alt = Enumerable.Range(2, found!.Count - 2).Where(i => found[i].Pattern == "Alternate" && found[i - 1].Pattern == "Alternate" && !found[i].NewCombo && !found[i - 1].NewCombo).ToList();
        Assert.That(alt, Is.Not.Empty);
        Assert.That(alt.Count(i => found[i].Position == found[i - 2].Position), Is.GreaterThan(alt.Count / 2), "X Z X Z: back on the spot two objects before");
    }
}
