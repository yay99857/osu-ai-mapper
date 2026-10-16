using osu.Game.Rulesets.Osu.Difficulty;
using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter;

/// <summary>
/// Difficulty attributes from the official calculator. A value is only meaningful together with
/// <see cref="Engine"/>, <see cref="Mods"/> and <see cref="ClockRate"/>; an engine upgrade produces new labels.
/// </summary>
public sealed record DifficultyReport
{
    public required double StarRating { get; init; }
    public required double Aim { get; init; }
    public required double Speed { get; init; }
    public required double Reading { get; init; }
    public required double Flashlight { get; init; }
    public required double SliderFactor { get; init; }
    public required int MaxCombo { get; init; }
    public required int CircleCount { get; init; }
    public required int SliderCount { get; init; }
    public required int SpinnerCount { get; init; }
    public required string Engine { get; init; }
    public string Mods { get; init; } = "nomod";
    public double ClockRate { get; init; } = 1;

    /// <summary>For example "osu-2026.916.0-nomod": the label namespace these numbers belong to.</summary>
    public string Profile => $"{Engine.Replace("ppy.osu.Game ", "osu-")}-{Mods}";
}

public static class DifficultyService
{
    /// <summary>Rates the document by encoding it and running the official decoder and calculator, the same path the game uses.</summary>
    public static DifficultyReport Calculate(BeatmapIr ir) => CalculateFromOsuText(OsuExporter.ToOsuText(ir));

    public static DifficultyReport CalculateFromOsuText(string osuText)
    {
        var working = OfficialEngine.ToWorking(OfficialEngine.Decode(osuText));
        var attributes = (OsuDifficultyAttributes)OfficialEngine.Ruleset.CreateDifficultyCalculator(working).Calculate();

        return new DifficultyReport
        {
            StarRating = attributes.StarRating,
            Aim = attributes.AimDifficulty,
            Speed = attributes.SpeedDifficulty,
            Reading = attributes.ReadingDifficulty,
            Flashlight = attributes.FlashlightDifficulty,
            SliderFactor = attributes.SliderFactor,
            MaxCombo = attributes.MaxCombo,
            CircleCount = attributes.HitCircleCount,
            SliderCount = attributes.SliderCount,
            SpinnerCount = attributes.SpinnerCount,
            Engine = OfficialEngine.Version,
        };
    }
}
