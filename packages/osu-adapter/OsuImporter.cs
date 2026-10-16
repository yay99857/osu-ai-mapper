using System.Security.Cryptography;
using System.Text;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Objects;
using OsuAi.BeatmapCore;
using SegmentType = OsuAi.BeatmapCore.SegmentType;

namespace OsuAi.OsuAdapter;

public sealed record ImportResult(BeatmapIr Beatmap, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Reads a .osu file through the official decoder and converter into <see cref="BeatmapIr"/>.</summary>
public static class OsuImporter
{
    public static ImportResult Import(byte[] osuBytes)
    {
        string sha = Convert.ToHexStringLower(SHA256.HashData(osuBytes));
        string text = Encoding.UTF8.GetString(osuBytes);

        // The official decoder skips a Mode line whose ruleset assembly is not loaded and carries on
        // as osu!standard, so a mania map would import as a broken standard map. Check the text itself.
        int declaredMode = ReadDeclaredMode(text);
        if (declaredMode != 0)
            throw new NotSupportedException($"Only osu!standard beatmaps can be imported; this file declares Mode {declaredMode}.");

        var decoded = OfficialEngine.Decode(text);
        var diagnostics = new List<Diagnostic>();

        if (decoded.BeatmapInfo.Ruleset.OnlineID != OfficialEngine.Ruleset.RulesetInfo.OnlineID)
        {
            throw new NotSupportedException(
                $"Only osu!standard beatmaps can be imported; this file declares ruleset {decoded.BeatmapInfo.Ruleset.OnlineID}.");
        }

        var playable = OfficialEngine.ToPlayable(decoded);
        var objects = new List<HitObjectIr>(playable.HitObjects.Count);

        for (int i = 0; i < playable.HitObjects.Count; i++)
        {
            var id = DeterministicGuid.Create($"{sha}:object:{i}");
            objects.Add(ConvertObject(playable.HitObjects[i], id, diagnostics));
        }

        var metadata = decoded.Metadata;

        var ir = new BeatmapIr
        {
            Id = DeterministicGuid.Create($"{sha}:beatmap"),
            RevisionId = DeterministicGuid.Create($"{sha}:revision"),
            Metadata = new BeatmapCore.BeatmapMetadata
            {
                Title = metadata.Title,
                TitleUnicode = NullIfEmpty(metadata.TitleUnicode),
                Artist = metadata.Artist,
                ArtistUnicode = NullIfEmpty(metadata.ArtistUnicode),
                Creator = metadata.Author.Username,
                DifficultyName = decoded.BeatmapInfo.DifficultyName,
                Source = NullIfEmpty(metadata.Source),
                Tags = metadata.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                OnlineIds = ReadOnlineIds(decoded.BeatmapInfo),
            },
            Assets = new BeatmapAssets
            {
                Audio = new AssetRef { Id = "audio", RelativePath = metadata.AudioFile },
                Background = string.IsNullOrEmpty(metadata.BackgroundFile)
                    ? null
                    : new AssetRef { Id = "background", RelativePath = metadata.BackgroundFile },
            },
            Settings = new DifficultySettings
            {
                Ar = Units.FromFloat(decoded.Difficulty.ApproachRate),
                Od = Units.FromFloat(decoded.Difficulty.OverallDifficulty),
                Cs = Units.FromFloat(decoded.Difficulty.CircleSize),
                Hp = Units.FromFloat(decoded.Difficulty.DrainRate),
                SliderMultiplier = decoded.Difficulty.SliderMultiplier,
                SliderTickRate = decoded.Difficulty.SliderTickRate,
                StackLeniency = Units.FromFloat(decoded.StackLeniency),
                PreviewTimeUs = metadata.PreviewTime >= 0 ? Units.MsToUs(metadata.PreviewTime) : null,
            },
            Timing = ReadTiming(decoded),
            Controls = new BeatmapControls { Effects = ReadEffects(decoded) },
            Breaks = decoded.Breaks.Select(b => new BreakPeriod(Units.MsToUs(b.StartTime), Units.MsToUs(b.EndTime))).ToArray(),
            Objects = objects,
            Provenance = new Provenance
            {
                CreatedBy = "osu-adapter.import",
                EngineVersion = OfficialEngine.Version,
                SourceSha256 = sha,
                SourceFormatVersion = decoded.BeatmapVersion,
            },
            ImportProfile = $"osu-file-v{decoded.BeatmapVersion}",
        };

        ReportProfileLosses(decoded, playable, objects, diagnostics);
        diagnostics.AddRange(BeatmapValidator.Validate(ir));
        return new ImportResult(ir, diagnostics);
    }

    public const string ProfileLossPrefix = "profile.loss.";

    /// <summary>The Mode value of the [General] section; 0 when absent, as in the format.</summary>
    internal static int ReadDeclaredMode(string osuText)
    {
        bool inGeneral = false;

        foreach (string raw in osuText.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith('['))
            {
                if (inGeneral)
                    break;

                inGeneral = line == "[General]";
                continue;
            }

            if (!inGeneral || !line.StartsWith("Mode", StringComparison.Ordinal))
                continue;

            int colon = line.IndexOf(':');

            if (colon < 0 || line[..colon].Trim() != "Mode")
                continue;

            string value = line[(colon + 1)..].Trim();
            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int mode)
                ? mode
                : throw new NotSupportedException($"Unreadable Mode value '{value}'.");
        }

        return 0;
    }

    /// <summary>
    /// Data the source carries but <see cref="BeatmapIr"/> 1.0 does not. Declared so that a lossy import
    /// is visible instead of silently passing a round trip that dropped the same thing on both sides.
    /// </summary>
    private static void ReportProfileLosses(Beatmap decoded, osu.Game.Beatmaps.IBeatmap playable, List<HitObjectIr> objects, List<Diagnostic> d)
    {
        void Loss(string code, string message, Severity severity = Severity.Warning, IReadOnlyList<Guid>? ids = null, long? timeUs = null) =>
            d.Add(new Diagnostic { Code = ProfileLossPrefix + code, Severity = severity, Message = message, ObjectIds = ids ?? [], TimeUs = timeUs });

        if (decoded.AudioLeadIn != 0)
            Loss("audio-lead-in", $"AudioLeadIn {decoded.AudioLeadIn} ms is not represented.");

        if (decoded.Countdown != CountdownType.None)
            Loss("countdown", $"Countdown {decoded.Countdown} is not represented.");

        if (decoded.EpilepsyWarning)
            Loss("epilepsy-warning", "EpilepsyWarning is not represented.");

        if (decoded.LetterboxInBreaks)
            Loss("letterbox-in-breaks", "LetterboxInBreaks is not represented.");

        if (decoded.SamplesMatchPlaybackRate)
            Loss("samples-match-playback-rate", "SamplesMatchPlaybackRate is not represented.");

        if (decoded.Bookmarks.Length > 0)
            Loss("bookmarks", $"{decoded.Bookmarks.Length} editor bookmarks are not represented.", Severity.Info);

        for (int i = 0; i < playable.HitObjects.Count; i++)
        {
            var hitObject = playable.HitObjects[i];
            var nodes = hitObject is IHasRepeats repeats ? repeats.NodeSamples.SelectMany(n => n) : [];

            if (hitObject.Samples.Concat(nodes).Any(s => s is ConvertHitObjectParser.FileHitSampleInfo))
                Loss("sample-file", "Custom sample file names are not represented.", ids: [objects[i].Id], timeUs: objects[i].TimeUs);
        }
    }

    private static TimingMap ReadTiming(Beatmap decoded)
    {
        var segments = new List<TimingSegment>();

        foreach (var point in decoded.ControlPointInfo.TimingPoints)
        {
            var startBeat = Rational.Zero;

            if (segments.Count > 0)
            {
                var prev = segments[^1];
                double elapsedBeats = (point.Time * 1000 - prev.StartTimeUs) / prev.BeatLengthUs;

                // osu! restarts the bar at every red line, so beat numbering continues from the next
                // whole beat. The tolerance keeps a red line placed exactly on a beat from skipping one.
                long whole = Math.Max(1, (long)Math.Ceiling(elapsedBeats - 1e-6));
                startBeat = prev.StartBeat + Rational.FromInteger(whole);
            }

            segments.Add(new TimingSegment
            {
                Id = $"t{segments.Count}",
                StartTimeUs = Units.MsToUs(point.Time),
                StartBeat = startBeat,
                BeatLengthUs = point.BeatLength * 1000,
                Meter = new Meter(point.TimeSignature.Numerator, 4),
                OmitFirstBarLine = point.OmitFirstBarLine,
                Source = TimingSource.Imported,
            });
        }

        return new TimingMap { Segments = segments };
    }

    private static EffectControl[] ReadEffects(Beatmap decoded)
    {
        var effects = new List<EffectControl>();
        bool kiai = false;

        foreach (var point in decoded.ControlPointInfo.EffectPoints)
        {
            if (point.KiaiMode == kiai)
                continue;

            kiai = point.KiaiMode;
            effects.Add(new EffectControl(Units.MsToUs(point.Time), kiai));
        }

        return effects.ToArray();
    }

    private static HitObjectIr ConvertObject(HitObject hitObject, Guid id, List<Diagnostic> diagnostics)
    {
        var osuObject = (OsuHitObject)hitObject;
        long timeUs = Units.MsToUs(hitObject.StartTime);
        var position = new Vector2D(Units.FromFloat(osuObject.Position.X), Units.FromFloat(osuObject.Position.Y));
        var samples = ConvertSamples(hitObject.Samples);

        switch (hitObject)
        {
            case Slider slider:
                return new SliderIr
                {
                    Id = id,
                    TimeUs = timeUs,
                    Position = position,
                    NewCombo = osuObject.NewCombo,
                    ComboSkip = osuObject.ComboOffset,
                    Samples = samples,
                    Path = ConvertPath(slider),
                    ExpectedDistancePx = slider.Path.ExpectedDistance.Value,
                    SpanCount = slider.RepeatCount + 1,
                    SvMultiplier = slider.SliderVelocityMultiplier,
                    NodeSamples = slider.NodeSamples.Select(ConvertSamples).ToArray(),
                    GenerateTicks = slider.GenerateTicks,
                    Computed = new SliderComputed
                    {
                        DurationUs = Units.MsToUs(slider.Duration),
                        EndPosition = new Vector2D(Units.FromFloat(slider.EndPosition.X), Units.FromFloat(slider.EndPosition.Y)),
                        TickCount = slider.NestedHitObjects.OfType<SliderTick>().Count(),
                        DistancePx = slider.Path.Distance,
                        Engine = OfficialEngine.Version,
                    },
                };

            case Spinner spinner:
                return new SpinnerIr
                {
                    Id = id,
                    TimeUs = timeUs,
                    Position = position,
                    NewCombo = osuObject.NewCombo,
                    ComboSkip = osuObject.ComboOffset,
                    Samples = samples,
                    EndTimeUs = Units.MsToUs(spinner.EndTime),
                };

            case HitCircle:
                return new CircleIr
                {
                    Id = id,
                    TimeUs = timeUs,
                    Position = position,
                    NewCombo = osuObject.NewCombo,
                    ComboSkip = osuObject.ComboOffset,
                    Samples = samples,
                };

            default:
                diagnostics.Add(new Diagnostic
                {
                    Code = "import.unknown-object",
                    Severity = Severity.Error,
                    Message = $"Unsupported hit object type {hitObject.GetType().Name}.",
                    TimeUs = timeUs,
                });
                return new CircleIr { Id = id, TimeUs = timeUs, Position = position };
        }
    }

    private static SliderPathIr ConvertPath(Slider slider) => new()
    {
        ControlPoints = slider.Path.ControlPoints.Select(p => new PathControlPointIr
        {
            X = Units.FromFloat(p.Position.X),
            Y = Units.FromFloat(p.Position.Y),
            SegmentType = p.Type is { } type ? ToSegmentType(type) : null,
            Degree = p.Type is { Type: SplineType.BSpline, Degree: { } degree } ? degree : null,
        }).ToArray(),
    };

    internal static SegmentType ToSegmentType(PathType type) => type.Type switch
    {
        SplineType.Linear => SegmentType.Linear,
        SplineType.PerfectCurve => SegmentType.PerfectCurve,
        SplineType.Catmull => SegmentType.Catmull,
        SplineType.BSpline when type.Degree is null => SegmentType.Bezier,
        SplineType.BSpline => SegmentType.BSpline,
        _ => throw new NotSupportedException($"Unknown spline type {type.Type}."),
    };

    internal static HitSample[] ConvertSamples(IList<HitSampleInfo> samples) =>
        samples.Select(s => new HitSample
        {
            Name = s.Name,
            Bank = s.Bank,
            Volume = s.Volume,
            Suffix = NullIfEmpty(s.Suffix),
            AutoBank = s.Name != HitSampleInfo.HIT_NORMAL && s.EditorAutoBank,
        }).ToArray();

    private static OnlineIds? ReadOnlineIds(BeatmapInfo info)
    {
        int? beatmapId = info.OnlineID > 0 ? info.OnlineID : null;
        int? setId = info.BeatmapSet?.OnlineID > 0 ? info.BeatmapSet.OnlineID : null;
        return beatmapId is null && setId is null ? null : new OnlineIds(beatmapId, setId);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
