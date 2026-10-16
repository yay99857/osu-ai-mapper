using System.Text.Json.Serialization;

namespace OsuAi.BeatmapCore;

/// <summary>
/// Playable objects of one osu!standard difficulty, independent of any file format.
/// Times are integer microseconds from the start of the decoded audio; coordinates are osu!
/// pixels in the nominal 512x384 playfield. Positions are authored positions, never stacked ones.
/// </summary>
public sealed record BeatmapIr
{
    public const string CurrentSchemaVersion = "1.0";
    public const string OsuStandard = "osu-standard";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid Id { get; init; }

    public required Guid RevisionId { get; init; }

    public Guid? ParentRevisionId { get; init; }

    public string Ruleset { get; init; } = OsuStandard;

    public required BeatmapMetadata Metadata { get; init; }

    public required BeatmapAssets Assets { get; init; }

    public required DifficultySettings Settings { get; init; }

    public required TimingMap Timing { get; init; }

    public BeatmapControls Controls { get; init; } = new();

    public IReadOnlyList<BreakPeriod> Breaks { get; init; } = [];

    public required IReadOnlyList<HitObjectIr> Objects { get; init; }

    public IReadOnlyList<ObjectLock> Locks { get; init; } = [];

    public required Provenance Provenance { get; init; }

    public string? ImportProfile { get; init; }

    public string ExportProfile { get; init; } = Profiles.OsuInterchangeV1;
}

public static class Profiles
{
    /// <summary>.osu/.osz as written by the official lazer encoder, circles and simple sliders.</summary>
    public const string OsuInterchangeV1 = "osu-interchange-v1";
}

public sealed record BeatmapMetadata
{
    public required string Title { get; init; }
    public string? TitleUnicode { get; init; }
    public required string Artist { get; init; }
    public string? ArtistUnicode { get; init; }
    public required string Creator { get; init; }
    public required string DifficultyName { get; init; }
    public string? Source { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Only ever carried over from an import. Never invented.</summary>
    public OnlineIds? OnlineIds { get; init; }
}

public sealed record OnlineIds(int? BeatmapId, int? BeatmapSetId);

public sealed record BeatmapAssets
{
    public required AssetRef Audio { get; init; }
    public AssetRef? Background { get; init; }
    public IReadOnlyList<AssetRef> Samples { get; init; } = [];
}

/// <summary>
/// A file belonging to the project. <see cref="RelativePath"/> is relative to the project or
/// package root; the hash and size are null when the bytes have not been seen (a bare .osu import).
/// </summary>
public sealed record AssetRef
{
    public required string Id { get; init; }
    public required string RelativePath { get; init; }
    public string? Sha256 { get; init; }
    public string? MimeType { get; init; }
    public long? ByteSize { get; init; }
    public string? RightsRef { get; init; }
}

public sealed record DifficultySettings
{
    public required double Ar { get; init; }
    public required double Od { get; init; }
    public required double Cs { get; init; }
    public required double Hp { get; init; }
    public required double SliderMultiplier { get; init; }
    public required double SliderTickRate { get; init; }
    public double StackLeniency { get; init; } = 0.7;
    public long? PreviewTimeUs { get; init; }
}

/// <summary>
/// Global controls that are not derivable from objects. Slider velocity and hit samples are
/// deliberately absent: the per-object values are the single source of truth, and the encoder
/// rebuilds legacy control points from them.
/// </summary>
public sealed record BeatmapControls
{
    public IReadOnlyList<EffectControl> Effects { get; init; } = [];
}

public sealed record EffectControl(long TimeUs, bool Kiai);

public sealed record BreakPeriod(long StartTimeUs, long EndTimeUs);

public sealed record ObjectLock(Guid ObjectId, IReadOnlyList<string> Fields);

public sealed record Provenance
{
    public required string CreatedBy { get; init; }
    public string? EngineVersion { get; init; }
    public string? SourceSha256 { get; init; }
    public int? SourceFormatVersion { get; init; }
}

public sealed record HitSample
{
    public const string Normal = "hitnormal";
    public const string Whistle = "hitwhistle";
    public const string Finish = "hitfinish";
    public const string Clap = "hitclap";

    public required string Name { get; init; }

    /// <summary>"normal", "soft" or "drum".</summary>
    public required string Bank { get; init; }

    /// <summary>0-100. Zero means "inherit from the timing point" in the legacy format.</summary>
    public int Volume { get; init; }

    /// <summary>Custom sample set suffix, e.g. "2" for hitnormal2.wav. Null for the default set.</summary>
    public string? Suffix { get; init; }

    /// <summary>
    /// For additions: the bank follows the hitnormal bank instead of being chosen explicitly.
    /// The legacy format writes an addition bank of 0 ("auto") for these.
    /// </summary>
    public bool AutoBank { get; init; }
}

public readonly record struct Vector2D(double X, double Y);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CircleIr), "circle")]
[JsonDerivedType(typeof(SliderIr), "slider")]
[JsonDerivedType(typeof(SpinnerIr), "spinner")]
public abstract record HitObjectIr
{
    public required Guid Id { get; init; }
    public required long TimeUs { get; init; }
    public required Vector2D Position { get; init; }
    public bool NewCombo { get; init; }
    public int ComboSkip { get; init; }
    public IReadOnlyList<HitSample> Samples { get; init; } = [];
    public string? Origin { get; init; }
    public Guid? PlanEventId { get; init; }
    public IReadOnlyList<string> LockedFields { get; init; } = [];
}

public sealed record CircleIr : HitObjectIr;

public sealed record SliderIr : HitObjectIr
{
    public required SliderPathIr Path { get; init; }

    /// <summary>Requested length. The official path semantics extend or cut the curve to it.</summary>
    public double? ExpectedDistancePx { get; init; }

    /// <summary>1 = head to tail; 2 = there and back. Official repeat count is SpanCount - 1.</summary>
    public required int SpanCount { get; init; }

    public double SvMultiplier { get; init; } = 1;

    /// <summary>One list per node: head, each reversal, tail. Empty when unknown.</summary>
    public IReadOnlyList<IReadOnlyList<HitSample>> NodeSamples { get; init; } = [];

    public bool GenerateTicks { get; init; } = true;

    /// <summary>Derived by the official engine. Invalidated whenever authored data changes.</summary>
    public SliderComputed? Computed { get; init; }
}

public sealed record SliderComputed
{
    public required long DurationUs { get; init; }
    public required Vector2D EndPosition { get; init; }
    public required int TickCount { get; init; }
    public required double DistancePx { get; init; }
    public required string Engine { get; init; }
}

public sealed record SpinnerIr : HitObjectIr
{
    public required long EndTimeUs { get; init; }
}

public enum SegmentType
{
    Linear,
    Bezier,
    PerfectCurve,
    Catmull,
    BSpline,
}

public sealed record SliderPathIr
{
    public const string RelativeToHead = "relative-to-head";

    public string CoordinateSpace { get; init; } = RelativeToHead;

    /// <summary>First point is the head at (0, 0).</summary>
    public required IReadOnlyList<PathControlPointIr> ControlPoints { get; init; }
}

public sealed record PathControlPointIr
{
    public required double X { get; init; }
    public required double Y { get; init; }

    /// <summary>Set on the point that starts a new segment; null continues the previous one.</summary>
    public SegmentType? SegmentType { get; init; }

    /// <summary>B-spline degree, only meaningful with <see cref="BeatmapCore.SegmentType.BSpline"/>.</summary>
    public int? Degree { get; init; }
}
