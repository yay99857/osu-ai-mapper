namespace OsuAi.BeatmapCore;

/// <summary>
/// Structural invariants of <see cref="BeatmapIr"/>. Errors mean the document must not be exported;
/// warnings mark things that are valid in the format but outside what the product generates.
/// Playability judgements that need the official engine (stacking, slider ends) live in the adapter.
/// </summary>
public static class BeatmapValidator
{
    public const double PlayfieldWidth = 512;
    public const double PlayfieldHeight = 384;

    private static readonly HashSet<string> sample_names = [HitSample.Normal, HitSample.Whistle, HitSample.Finish, HitSample.Clap];
    private static readonly HashSet<string> sample_banks = ["normal", "soft", "drum"];

    public static IReadOnlyList<Diagnostic> Validate(BeatmapIr map)
    {
        var d = new List<Diagnostic>();

        if (map.SchemaVersion != BeatmapIr.CurrentSchemaVersion)
            d.Add(Error("schema.version", $"Unsupported schemaVersion '{map.SchemaVersion}'."));

        if (map.Ruleset != BeatmapIr.OsuStandard)
            d.Add(Error("schema.ruleset", $"Unsupported ruleset '{map.Ruleset}'."));

        ValidateAssets(map.Assets, d);
        ValidateSettings(map.Settings, d);
        ValidateTiming(map.Timing, d);
        ValidateObjects(map, d);
        ValidateBreaks(map.Breaks, d);
        ValidateLocks(map, d);

        for (int i = 1; i < map.Controls.Effects.Count; i++)
        {
            if (map.Controls.Effects[i].TimeUs < map.Controls.Effects[i - 1].TimeUs)
                d.Add(Error("controls.effects.order", "Effect controls are not sorted by time.", timeUs: map.Controls.Effects[i].TimeUs));
        }

        return d;
    }

    private static void ValidateAssets(BeatmapAssets assets, List<Diagnostic> d)
    {
        AssetRef?[] all = [assets.Audio, assets.Background, .. assets.Samples];

        foreach (var asset in all)
        {
            if (asset is not null && !IsContainedRelativePath(asset.RelativePath))
                d.Add(Error("asset.path", $"Asset '{asset.Id}' path '{asset.RelativePath}' must be relative and stay inside the project."));
        }
    }

    /// <summary>Forward-slash relative path with no root, drive, backslash or parent segment.</summary>
    public static bool IsContainedRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.Contains('\\')
        && !path.Contains(':')
        && path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static void ValidateSettings(DifficultySettings s, List<Diagnostic> d)
    {
        RequireRange(d, "settings.ar", s.Ar, 0, 10);
        RequireRange(d, "settings.od", s.Od, 0, 10);
        RequireRange(d, "settings.cs", s.Cs, 0, 10);
        RequireRange(d, "settings.hp", s.Hp, 0, 10);
        RequireRange(d, "settings.slider-multiplier", s.SliderMultiplier, 0.4, 3.6);
        RequireRange(d, "settings.slider-tick-rate", s.SliderTickRate, 0.5, 8);
        RequireRange(d, "settings.stack-leniency", s.StackLeniency, 0, 1);
    }

    private static void ValidateTiming(TimingMap timing, List<Diagnostic> d)
    {
        if (timing.MappingConvention != TimingMap.QuarterNoteBeat)
            d.Add(Error("timing.convention", $"Unsupported mapping convention '{timing.MappingConvention}'."));

        if (timing.Segments.Count == 0)
        {
            d.Add(Error("timing.empty", "Timing map has no segments."));
            return;
        }

        var ids = new HashSet<string>();

        for (int i = 0; i < timing.Segments.Count; i++)
        {
            var s = timing.Segments[i];

            if (!ids.Add(s.Id))
                d.Add(Error("timing.duplicate-id", $"Duplicate timing segment id '{s.Id}'."));

            if (!double.IsFinite(s.BeatLengthUs) || s.BeatLengthUs <= 0)
                d.Add(Error("timing.beat-length", $"Segment '{s.Id}' has non-positive or non-finite beat length.", timeUs: s.StartTimeUs));

            if (s.Confidence is { } c && !(c >= 0 && c <= 1))
                d.Add(Error("timing.confidence", $"Segment '{s.Id}' confidence must be in [0, 1].", timeUs: s.StartTimeUs));

            if (s.Meter is { } m && (m.Numerator <= 0 || m.Denominator <= 0))
                d.Add(Error("timing.meter", $"Segment '{s.Id}' has a non-positive meter.", timeUs: s.StartTimeUs));

            if (i > 0)
            {
                var prev = timing.Segments[i - 1];

                if (s.StartTimeUs <= prev.StartTimeUs || s.StartBeat <= prev.StartBeat)
                    d.Add(Error("timing.order", $"Segment '{s.Id}' does not start strictly after '{prev.Id}' in both time and beat.", timeUs: s.StartTimeUs));
            }
        }
    }

    private static void ValidateObjects(BeatmapIr map, List<Diagnostic> d)
    {
        var ids = new HashSet<Guid>();
        long firstTimingUs = map.Timing.Segments.Count > 0 ? map.Timing.Segments[0].StartTimeUs : long.MinValue;

        for (int i = 0; i < map.Objects.Count; i++)
        {
            var o = map.Objects[i];
            Guid[] self = [o.Id];

            if (!ids.Add(o.Id))
                d.Add(Error("object.duplicate-id", "Duplicate object id.", self, o.TimeUs));

            if (i > 0 && o.TimeUs < map.Objects[i - 1].TimeUs)
                d.Add(Error("object.order", "Objects are not sorted by time.", self, o.TimeUs));

            if (!IsFinite(o.Position))
            {
                d.Add(Error("object.position", "Position is not finite.", self, o.TimeUs));
                continue;
            }

            if (o.ComboSkip < 0)
                d.Add(Error("object.combo-skip", "Combo skip cannot be negative.", self, o.TimeUs));

            if (o.TimeUs < firstTimingUs)
                d.Add(Warning("object.before-timing", "Object starts before the first timing segment.", self, o.TimeUs));

            if (!InsidePlayfield(o.Position))
                d.Add(Warning("object.outside-playfield", "Head position is outside the 512x384 playfield.", self, o.TimeUs));

            ValidateSamples(o.Samples, self, o.TimeUs, d);

            switch (o)
            {
                case SliderIr slider:
                    ValidateSlider(slider, d);
                    break;

                case SpinnerIr spinner when spinner.EndTimeUs <= spinner.TimeUs:
                    d.Add(Error("spinner.duration", "Spinner must end after it starts.", self, o.TimeUs));
                    break;
            }
        }
    }

    private static void ValidateSlider(SliderIr s, List<Diagnostic> d)
    {
        Guid[] self = [s.Id];
        var points = s.Path.ControlPoints;

        if (s.Path.CoordinateSpace != SliderPathIr.RelativeToHead)
            d.Add(Error("slider.coordinate-space", $"Unsupported coordinate space '{s.Path.CoordinateSpace}'.", self, s.TimeUs));

        if (points.Count < 2)
        {
            d.Add(Error("slider.path.too-short", "Slider path needs at least two control points.", self, s.TimeUs));
        }
        else
        {
            if (points[0].X != 0 || points[0].Y != 0)
                d.Add(Error("slider.path.origin", "First control point must be the head at (0, 0).", self, s.TimeUs));

            if (points[0].SegmentType is null)
                d.Add(Error("slider.path.first-type", "First control point must declare a segment type.", self, s.TimeUs));

            foreach (var p in points)
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                    d.Add(Error("slider.path.finite", "Control point is not finite.", self, s.TimeUs));

                if (p.Degree is not null && p.SegmentType != SegmentType.BSpline)
                    d.Add(Error("slider.path.degree", "Degree is only valid on a B-spline segment.", self, s.TimeUs));

                if (p.Degree is <= 0)
                    d.Add(Error("slider.path.degree", "B-spline degree must be positive.", self, s.TimeUs));
            }
        }

        if (s.SpanCount < 1)
            d.Add(Error("slider.span-count", "Span count must be at least 1.", self, s.TimeUs));

        if (!double.IsFinite(s.SvMultiplier) || s.SvMultiplier < 0.1 || s.SvMultiplier > 10)
            d.Add(Error("slider.sv", "Slider velocity multiplier must be in [0.1, 10].", self, s.TimeUs));

        if (s.ExpectedDistancePx is { } dist && (!double.IsFinite(dist) || dist <= 0))
            d.Add(Error("slider.expected-distance", "Expected distance must be positive and finite.", self, s.TimeUs));

        if (s.NodeSamples.Count != 0 && s.NodeSamples.Count != s.SpanCount + 1)
            d.Add(Error("slider.node-samples", $"Expected {s.SpanCount + 1} node sample lists, found {s.NodeSamples.Count}.", self, s.TimeUs));

        foreach (var node in s.NodeSamples)
            ValidateSamples(node, self, s.TimeUs, d);
    }

    private static void ValidateSamples(IReadOnlyList<HitSample> samples, Guid[] self, long timeUs, List<Diagnostic> d)
    {
        foreach (var sample in samples)
        {
            if (!sample_names.Contains(sample.Name))
                d.Add(Error("sample.name", $"Unknown sample name '{sample.Name}'.", self, timeUs));

            if (!sample_banks.Contains(sample.Bank))
                d.Add(Error("sample.bank", $"Unknown sample bank '{sample.Bank}'.", self, timeUs));

            if (sample.Volume is < 0 or > 100)
                d.Add(Error("sample.volume", "Sample volume must be in [0, 100].", self, timeUs));
        }
    }

    private static void ValidateBreaks(IReadOnlyList<BreakPeriod> breaks, List<Diagnostic> d)
    {
        for (int i = 0; i < breaks.Count; i++)
        {
            if (breaks[i].EndTimeUs <= breaks[i].StartTimeUs)
                d.Add(Error("break.duration", "Break must end after it starts.", timeUs: breaks[i].StartTimeUs));

            if (i > 0 && breaks[i].StartTimeUs < breaks[i - 1].EndTimeUs)
                d.Add(Error("break.order", "Breaks overlap or are not sorted.", timeUs: breaks[i].StartTimeUs));
        }
    }

    private static void ValidateLocks(BeatmapIr map, List<Diagnostic> d)
    {
        var ids = map.Objects.Select(o => o.Id).ToHashSet();

        foreach (var l in map.Locks)
        {
            if (!ids.Contains(l.ObjectId))
                d.Add(Error("lock.dangling", "Lock refers to an object that does not exist.", [l.ObjectId]));
        }
    }

    private static void RequireRange(List<Diagnostic> d, string code, double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            d.Add(Error(code, $"Value {value} is outside [{min}, {max}]."));
    }

    private static bool IsFinite(Vector2D v) => double.IsFinite(v.X) && double.IsFinite(v.Y);

    private static bool InsidePlayfield(Vector2D v) => v.X >= 0 && v.X <= PlayfieldWidth && v.Y >= 0 && v.Y <= PlayfieldHeight;

    private static Diagnostic Error(string code, string message, IReadOnlyList<Guid>? ids = null, long? timeUs = null) =>
        new() { Code = code, Severity = Severity.Error, Message = message, ObjectIds = ids ?? [], TimeUs = timeUs };

    private static Diagnostic Warning(string code, string message, IReadOnlyList<Guid>? ids = null, long? timeUs = null) =>
        new() { Code = code, Severity = Severity.Warning, Message = message, ObjectIds = ids ?? [], TimeUs = timeUs };
}
