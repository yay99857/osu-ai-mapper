using OsuAi.Generation;

namespace OsuAi.Generation.Tests;

public class SliderTravelTests
{
    [Test]
    public void LongSlidersTravelOnceOrTwiceAndSlowDownInsteadOfRepeating()
    {
        // Four beats at slider multiplier 1.8: 720 px, which the old rule split into four spans.
        var spans = Enumerable.Range(0, 400).Select(seed => ProceduralPlacement.SliderTravel(16, 720, seed)).ToArray();
        Assert.That(spans.Max(s => s.Spans), Is.LessThanOrEqualTo(2));
        Assert.That(spans.Count(s => s.Spans == 1), Is.GreaterThan(spans.Length * 0.7));
        Assert.That(spans.Where(s => s.Spans == 1).All(s => s.Velocity < 0.5 && 720 * s.Velocity <= 281));
    }

    [Test]
    public void ShortSlidersRarelyRepeatAndKeepTheirVelocity()
    {
        var half = Enumerable.Range(0, 400).Select(seed => ProceduralPlacement.SliderTravel(2, 90, seed)).ToArray();
        Assert.That(half.All(s => s.Spans == 1 && s.Velocity == 1));
        var beat = Enumerable.Range(0, 400).Select(seed => ProceduralPlacement.SliderTravel(4, 180, seed)).ToArray();
        Assert.That(beat.Count(s => s.Spans == 2), Is.InRange(10, 50));
        Assert.That(beat.All(s => s.Velocity == 1));
    }

    [Test]
    public void CalmPassagesSlowSlidersDownAndLoudOnesSpeedThemUp()
    {
        Assert.That(ProceduralPlacement.SectionVelocity(0), Is.LessThan(1));
        Assert.That(ProceduralPlacement.SectionVelocity(3), Is.GreaterThan(1.1));
        var loud = ProceduralPlacement.SliderTravel(2, 90, 1, ProceduralPlacement.SectionVelocity(3));
        Assert.That(loud.Velocity, Is.EqualTo(1.2));
        var longLoud = ProceduralPlacement.SliderTravel(16, 720, 5, ProceduralPlacement.SectionVelocity(3));
        Assert.That(720 * longLoud.Velocity / longLoud.Spans, Is.LessThanOrEqualTo(281), "long bodies are still slowed to fit");
    }
}
