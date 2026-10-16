using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Models;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Objects;
using OsuAi.BeatmapCore;
using osuTK;
using SegmentType = OsuAi.BeatmapCore.SegmentType;
using TimeSignature = osu.Game.Beatmaps.Timing.TimeSignature;

namespace OsuAi.OsuAdapter;

/// <summary>
/// Builds official osu!standard objects from <see cref="BeatmapIr"/> and serialises them with the
/// official encoder (profile <see cref="Profiles.OsuInterchangeV1"/>). Refuses invalid documents.
/// </summary>
public static class OsuExporter
{
    public static string ToOsuText(BeatmapIr ir)
    {
        var errors = BeatmapValidator.Validate(ir)
                                     .Concat(InterchangeProfile.Validate(ir))
                                     .Where(d => d.Severity == Severity.Error)
                                     .ToArray();

        if (errors.Length > 0)
            throw new InvalidBeatmapException(errors);

        return OfficialEngine.Encode(ToOfficial(ir));
    }

    /// <summary>The official model of the document, defaults applied, ready for the encoder.</summary>
    public static Beatmap ToOfficial(BeatmapIr ir)
    {
        var difficulty = new BeatmapDifficulty
        {
            ApproachRate = (float)ir.Settings.Ar,
            OverallDifficulty = (float)ir.Settings.Od,
            CircleSize = (float)ir.Settings.Cs,
            DrainRate = (float)ir.Settings.Hp,
            SliderMultiplier = ir.Settings.SliderMultiplier,
            SliderTickRate = ir.Settings.SliderTickRate,
        };

        var metadata = new osu.Game.Beatmaps.BeatmapMetadata
        {
            Title = ir.Metadata.Title,
            TitleUnicode = ir.Metadata.TitleUnicode ?? string.Empty,
            Artist = ir.Metadata.Artist,
            ArtistUnicode = ir.Metadata.ArtistUnicode ?? string.Empty,
            Author = new RealmUser { Username = ir.Metadata.Creator },
            Source = ir.Metadata.Source ?? string.Empty,
            Tags = string.Join(' ', ir.Metadata.Tags),
            AudioFile = ir.Assets.Audio.RelativePath,
            BackgroundFile = ir.Assets.Background?.RelativePath ?? string.Empty,
            PreviewTime = ir.Settings.PreviewTimeUs is { } preview ? (int)Math.Round(Units.UsToMs(preview)) : -1,
        };

        var info = new BeatmapInfo(OfficialEngine.Ruleset.RulesetInfo, difficulty, metadata)
        {
            DifficultyName = ir.Metadata.DifficultyName,
        };

        if (ir.Metadata.OnlineIds is { } online)
        {
            if (online.BeatmapId is { } beatmapId)
                info.OnlineID = beatmapId;

            if (online.BeatmapSetId is { } setId)
                info.BeatmapSet = new BeatmapSetInfo { OnlineID = setId };
        }

        var beatmap = new Beatmap
        {
            BeatmapInfo = info,
            StackLeniency = (float)ir.Settings.StackLeniency,
        };

        foreach (var segment in ir.Timing.Segments)
        {
            beatmap.ControlPointInfo.Add(Units.UsToMs(segment.StartTimeUs), new TimingControlPoint
            {
                BeatLength = segment.BeatLengthUs / 1000,
                TimeSignature = new TimeSignature(segment.Meter?.Numerator ?? 4),
                OmitFirstBarLine = segment.OmitFirstBarLine,
            });
        }

        foreach (var effect in ir.Controls.Effects)
            beatmap.ControlPointInfo.Add(Units.UsToMs(effect.TimeUs), new EffectControlPoint { KiaiMode = effect.Kiai });

        foreach (var period in ir.Breaks)
            beatmap.Breaks.Add(new osu.Game.Beatmaps.Timing.BreakPeriod(Units.UsToMs(period.StartTimeUs), Units.UsToMs(period.EndTimeUs)));

        foreach (var o in ir.Objects)
        {
            var hitObject = ToOfficialObject(o);
            hitObject.ApplyDefaults(beatmap.ControlPointInfo, beatmap.Difficulty);
            beatmap.HitObjects.Add(hitObject);
        }

        return beatmap;
    }

    private static OsuHitObject ToOfficialObject(HitObjectIr o)
    {
        var position = new Vector2((float)o.Position.X, (float)o.Position.Y);
        double startTime = Units.UsToMs(o.TimeUs);

        OsuHitObject result = o switch
        {
            CircleIr => new HitCircle(),
            SliderIr s => new Slider
            {
                Path = new SliderPath(s.Path.ControlPoints.Select(ToOfficialControlPoint).ToArray(), s.ExpectedDistancePx),
                RepeatCount = s.SpanCount - 1,
                SliderVelocityMultiplier = s.SvMultiplier,
                NodeSamples = s.NodeSamples.Select(n => (IList<HitSampleInfo>)ToOfficialSamples(n)).ToList(),
                GenerateTicks = s.GenerateTicks,
            },
            SpinnerIr sp => new Spinner { Duration = Units.UsToMs(sp.EndTimeUs) - startTime },
            _ => throw new NotSupportedException($"Unsupported object kind {o.GetType().Name}."),
        };

        result.StartTime = startTime;
        result.Position = position;
        result.NewCombo = o.NewCombo;
        result.ComboOffset = o.ComboSkip;
        result.Samples = ToOfficialSamples(o.Samples);
        return result;
    }

    private static PathControlPoint ToOfficialControlPoint(PathControlPointIr p) =>
        new(new Vector2((float)p.X, (float)p.Y), p.SegmentType is { } type ? ToPathType(type, p.Degree) : null);

    private static PathType ToPathType(SegmentType type, int? degree) => type switch
    {
        SegmentType.Linear => PathType.LINEAR,
        SegmentType.Bezier => PathType.BEZIER,
        SegmentType.PerfectCurve => PathType.PERFECT_CURVE,
        SegmentType.Catmull => PathType.CATMULL,
        SegmentType.BSpline => PathType.BSpline(degree ?? throw new InvalidOperationException("B-spline segment without degree.")),
        _ => throw new NotSupportedException($"Unknown segment type {type}."),
    };

    private static List<HitSampleInfo> ToOfficialSamples(IReadOnlyList<HitSample> samples) =>
        samples.Select(s => new HitSampleInfo(s.Name, s.Bank, s.Suffix, s.Volume, editorAutoBank: s.AutoBank)).ToList();
}

public sealed class InvalidBeatmapException(IReadOnlyList<Diagnostic> errors)
    : Exception($"Beatmap is invalid: {string.Join("; ", errors.Take(5))}{(errors.Count > 5 ? $" (+{errors.Count - 5} more)" : "")}")
{
    public IReadOnlyList<Diagnostic> Errors { get; } = errors;
}
