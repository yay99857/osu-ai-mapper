namespace OsuAi.BeatmapCore;

public enum TimingSource
{
    Detected,
    Manual,
    Imported,
}

public sealed record Meter(int Numerator, int Denominator);

/// <summary>
/// A stretch of constant tempo. Beat numbering is continuous across segments: a segment's
/// <see cref="StartBeat"/> is the beat at which it starts. Imported maps restart the bar at each
/// red line, so for them <see cref="StartBeat"/> is the previous start rounded up to a whole beat.
/// </summary>
public sealed record TimingSegment
{
    public required string Id { get; init; }

    /// <summary>Microseconds from the start of the decoded audio. May be negative.</summary>
    public required long StartTimeUs { get; init; }

    public required Rational StartBeat { get; init; }

    /// <summary>Length of one editor beat (a quarter note under <see cref="TimingMap.MappingConvention"/>).</summary>
    public required double BeatLengthUs { get; init; }

    public Meter? Meter { get; init; }

    public Rational? BarPhaseBeats { get; init; }

    public bool OmitFirstBarLine { get; init; }

    public required TimingSource Source { get; init; }

    /// <summary>In [0, 1], or null when no confidence was estimated.</summary>
    public double? Confidence { get; init; }
}

public sealed record TimingMap
{
    public const string QuarterNoteBeat = "quarter-note-beat";

    public required IReadOnlyList<TimingSegment> Segments { get; init; }

    public string? ObservationsRef { get; init; }

    public string MappingConvention { get; init; } = QuarterNoteBeat;

    /// <summary>
    /// Time of a beat position. Computed from the owning segment's anchor in one step, so a long
    /// walk along the grid never sums rounded beat lengths.
    /// </summary>
    public double TimeUsAt(Rational beat)
    {
        var segment = SegmentAtBeat(beat);
        return segment.StartTimeUs + (beat - segment.StartBeat).ToDouble() * segment.BeatLengthUs;
    }

    /// <summary>Fractional beat position of a time. Approximate by nature; time is the authority.</summary>
    public double BeatAt(double timeUs)
    {
        var segment = SegmentAtTime(timeUs);
        return segment.StartBeat.ToDouble() + (timeUs - segment.StartTimeUs) / segment.BeatLengthUs;
    }

    public TimingSegment SegmentAtTime(double timeUs)
    {
        RequireSegments();

        var current = Segments[0];

        foreach (var segment in Segments)
        {
            if (segment.StartTimeUs > timeUs)
                break;

            current = segment;
        }

        return current;
    }

    public TimingSegment SegmentAtBeat(Rational beat)
    {
        RequireSegments();

        var current = Segments[0];

        foreach (var segment in Segments)
        {
            if (segment.StartBeat > beat)
                break;

            current = segment;
        }

        return current;
    }

    private void RequireSegments()
    {
        if (Segments.Count == 0)
            throw new InvalidOperationException("Timing map has no segments.");
    }
}
