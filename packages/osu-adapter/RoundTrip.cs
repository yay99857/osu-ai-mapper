using System.Globalization;
using System.Text;
using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter;

public sealed record SemanticDifference(string Path, string Expected, string Actual)
{
    public override string ToString() => $"{Path}: expected {Expected}, got {Actual}";
}

public sealed record RoundTripReport
{
    public required IReadOnlyList<Diagnostic> ImportDiagnostics { get; init; }
    public required IReadOnlyList<SemanticDifference> Differences { get; init; }
    public required DifficultyReport OriginalDifficulty { get; init; }
    public required DifficultyReport RoundTripDifficulty { get; init; }

    /// <summary>Encoding the re-imported document gives byte-identical text: the encoder output is a fixed point.</summary>
    public required bool EncoderIsStable { get; init; }

    public required string EncodedText { get; init; }

    /// <summary>Source data the IR profile is known not to carry. Reported, but not a round-trip failure.</summary>
    public IEnumerable<Diagnostic> DeclaredLosses => ImportDiagnostics.Where(d => d.Code.StartsWith(OsuImporter.ProfileLossPrefix, StringComparison.Ordinal));

    /// <summary>No unexpected loss: every difference is either absent or declared by the importer.</summary>
    public bool IsLossless => Differences.Count == 0 && EncoderIsStable && !ImportDiagnostics.HasErrors()
                              && Math.Abs(OriginalDifficulty.StarRating - RoundTripDifficulty.StarRating) < RoundTrip.StarTolerance;
}

/// <summary>
/// parse → IR → official encode → parse, compared on meaning rather than text: the encoder is free
/// to canonicalise fields, so textual equality would be the wrong test.
/// </summary>
public static class RoundTrip
{
    public const double StarTolerance = 1e-9;

    public static RoundTripReport Run(byte[] osuBytes)
    {
        var first = OsuImporter.Import(osuBytes);
        string encoded = OsuExporter.ToOsuText(first.Beatmap);
        var second = OsuImporter.Import(Encoding.UTF8.GetBytes(encoded));
        string reencoded = OsuExporter.ToOsuText(second.Beatmap);

        return new RoundTripReport
        {
            ImportDiagnostics = first.Diagnostics,
            Differences = SemanticComparer.Compare(first.Beatmap, second.Beatmap),
            OriginalDifficulty = DifficultyService.CalculateFromOsuText(Encoding.UTF8.GetString(osuBytes)),
            RoundTripDifficulty = DifficultyService.CalculateFromOsuText(encoded),
            EncoderIsStable = encoded == reencoded,
            EncodedText = encoded,
        };
    }
}

/// <summary>
/// Field-by-field comparison of two documents. Identity fields (IDs, provenance, import profile)
/// are skipped; everything a player or the difficulty calculator can observe is compared.
/// </summary>
public static class SemanticComparer
{
    private const double position_tolerance = 1e-3;
    private const double end_position_tolerance = 1e-2;
    private const long time_tolerance_us = 1;

    public static IReadOnlyList<SemanticDifference> Compare(BeatmapIr expected, BeatmapIr actual)
    {
        var c = new Collector();

        var em = expected.Metadata;
        var am = actual.Metadata;
        c.Equal("metadata.title", em.Title, am.Title);
        c.Equal("metadata.titleUnicode", em.TitleUnicode, am.TitleUnicode);
        c.Equal("metadata.artist", em.Artist, am.Artist);
        c.Equal("metadata.artistUnicode", em.ArtistUnicode, am.ArtistUnicode);
        c.Equal("metadata.creator", em.Creator, am.Creator);
        c.Equal("metadata.difficultyName", em.DifficultyName, am.DifficultyName);
        c.Equal("metadata.source", em.Source, am.Source);
        c.Equal("metadata.tags", string.Join(' ', em.Tags), string.Join(' ', am.Tags));
        c.Equal("metadata.onlineIds", em.OnlineIds, am.OnlineIds);
        c.Equal("assets.audio", expected.Assets.Audio.RelativePath, actual.Assets.Audio.RelativePath);
        c.Equal("assets.background", expected.Assets.Background?.RelativePath, actual.Assets.Background?.RelativePath);

        c.Equal("settings", expected.Settings, actual.Settings);

        c.Count("timing.segments", expected.Timing.Segments.Count, actual.Timing.Segments.Count);

        for (int i = 0; i < Math.Min(expected.Timing.Segments.Count, actual.Timing.Segments.Count); i++)
        {
            var e = expected.Timing.Segments[i];
            var a = actual.Timing.Segments[i];
            string p = $"timing.segments[{i}]";
            c.Time($"{p}.startTimeUs", e.StartTimeUs, a.StartTimeUs);
            c.Equal($"{p}.startBeat", e.StartBeat, a.StartBeat);
            c.Relative($"{p}.beatLengthUs", e.BeatLengthUs, a.BeatLengthUs, 1e-12);
            c.Equal($"{p}.meter", e.Meter, a.Meter);
            c.Equal($"{p}.omitFirstBarLine", e.OmitFirstBarLine, a.OmitFirstBarLine);
        }

        c.Sequence("controls.effects", expected.Controls.Effects, actual.Controls.Effects);
        c.Sequence("breaks", expected.Breaks, actual.Breaks);

        c.Count("objects", expected.Objects.Count, actual.Objects.Count);

        for (int i = 0; i < Math.Min(expected.Objects.Count, actual.Objects.Count); i++)
            CompareObject($"objects[{i}]", expected.Objects[i], actual.Objects[i], c);

        return c.Differences;
    }

    private static void CompareObject(string p, HitObjectIr e, HitObjectIr a, Collector c)
    {
        if (e.GetType() != a.GetType())
        {
            c.Add($"{p}.kind", e.GetType().Name, a.GetType().Name);
            return;
        }

        c.Time($"{p}.timeUs", e.TimeUs, a.TimeUs);
        c.Position($"{p}.position", e.Position, a.Position, position_tolerance);
        c.Equal($"{p}.newCombo", e.NewCombo, a.NewCombo);
        c.Equal($"{p}.comboSkip", e.ComboSkip, a.ComboSkip);
        c.Sequence($"{p}.samples", e.Samples, a.Samples);

        switch (e, a)
        {
            case (SliderIr es, SliderIr @as):
                c.Count($"{p}.path.controlPoints", es.Path.ControlPoints.Count, @as.Path.ControlPoints.Count);

                for (int j = 0; j < Math.Min(es.Path.ControlPoints.Count, @as.Path.ControlPoints.Count); j++)
                {
                    var ep = es.Path.ControlPoints[j];
                    var ap = @as.Path.ControlPoints[j];
                    c.Position($"{p}.path.controlPoints[{j}]", new Vector2D(ep.X, ep.Y), new Vector2D(ap.X, ap.Y), position_tolerance);
                    c.Equal($"{p}.path.controlPoints[{j}].segmentType", ep.SegmentType, ap.SegmentType);
                    c.Equal($"{p}.path.controlPoints[{j}].degree", ep.Degree, ap.Degree);
                }

                c.Nullable($"{p}.expectedDistancePx", es.ExpectedDistancePx, @as.ExpectedDistancePx, position_tolerance);
                c.Equal($"{p}.spanCount", es.SpanCount, @as.SpanCount);
                c.Relative($"{p}.svMultiplier", es.SvMultiplier, @as.SvMultiplier, 1e-9);
                c.Equal($"{p}.generateTicks", es.GenerateTicks, @as.GenerateTicks);
                c.Count($"{p}.nodeSamples", es.NodeSamples.Count, @as.NodeSamples.Count);

                for (int j = 0; j < Math.Min(es.NodeSamples.Count, @as.NodeSamples.Count); j++)
                    c.Sequence($"{p}.nodeSamples[{j}]", es.NodeSamples[j], @as.NodeSamples[j]);

                if (es.Computed is { } ec && @as.Computed is { } ac)
                {
                    c.Time($"{p}.computed.durationUs", ec.DurationUs, ac.DurationUs);
                    c.Position($"{p}.computed.endPosition", ec.EndPosition, ac.EndPosition, end_position_tolerance);
                    c.Equal($"{p}.computed.tickCount", ec.TickCount, ac.TickCount);
                    c.Nullable($"{p}.computed.distancePx", ec.DistancePx, ac.DistancePx, position_tolerance);
                }

                break;

            case (SpinnerIr esp, SpinnerIr asp):
                c.Time($"{p}.endTimeUs", esp.EndTimeUs, asp.EndTimeUs);
                break;
        }
    }

    private sealed class Collector
    {
        public List<SemanticDifference> Differences { get; } = [];

        public void Add(string path, object? expected, object? actual) =>
            Differences.Add(new SemanticDifference(path, Format(expected), Format(actual)));

        public void Equal<T>(string path, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                Add(path, expected, actual);
        }

        public void Count(string path, int expected, int actual)
        {
            if (expected != actual)
                Add($"{path}.count", expected, actual);
        }

        public void Time(string path, long expected, long actual)
        {
            if (Math.Abs(expected - actual) > time_tolerance_us)
                Add(path, expected, actual);
        }

        public void Relative(string path, double expected, double actual, double tolerance)
        {
            if (Math.Abs(expected - actual) > tolerance * Math.Max(1, Math.Abs(expected)))
                Add(path, expected, actual);
        }

        public void Nullable(string path, double? expected, double? actual, double tolerance)
        {
            if (expected is null != actual is null || (expected is { } e && actual is { } a && Math.Abs(e - a) > tolerance))
                Add(path, expected, actual);
        }

        public void Position(string path, Vector2D expected, Vector2D actual, double tolerance)
        {
            if (Math.Abs(expected.X - actual.X) > tolerance || Math.Abs(expected.Y - actual.Y) > tolerance)
                Add(path, expected, actual);
        }

        public void Sequence<T>(string path, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
        {
            if (!expected.SequenceEqual(actual))
                Add(path, string.Join(", ", expected), string.Join(", ", actual));
        }

        private static string Format(object? value) => value switch
        {
            null => "null",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
        };
    }
}
