namespace OsuAi.ProjectStore;

/// <summary>
/// project.json: what a beatmap project is. Paths are relative to the project folder and use forward
/// slashes. The manifest is the only mutable file; everything it points at is written once.
/// </summary>
public sealed record ProjectManifest
{
    public const string CurrentSchema = "project-1";

    public string SchemaVersion { get; init; } = CurrentSchema;
    public required Guid ProjectId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The file the user imported, copied into the project unchanged.</summary>
    public required ManagedFile Original { get; init; }

    /// <summary>The audio that is analysed and packaged (a transcode of the original), once prepared.</summary>
    public ManagedFile? Audio { get; init; }

    /// <summary>The background image the user chose, copied into the project; packaged on export.</summary>
    public ManagedFile? Background { get; init; }

    public required ProjectMetadata Metadata { get; init; }
    public required ProjectSettings Settings { get; init; }

    /// <summary>The analysis in use, identified by its cache key.</summary>
    public AnalysisRef? Analysis { get; init; }

    public string? ActiveRevision { get; init; }
    public IReadOnlyList<RevisionRecord> Revisions { get; init; } = [];

    /// <summary>
    /// The revisions that go into the exported .osz, one per difficulty name (a mapset). Null means
    /// only the active revision, as before mapsets existed; the active one is always included.
    /// </summary>
    public IReadOnlyList<string>? Package { get; init; }

    /// <summary>
    /// What Mapperatorinator gave for what it was asked, newest last: its star requests are
    /// approximate, so the next request is corrected by how far the recent ones landed.
    /// </summary>
    public IReadOnlyList<StarObservation> StarObservations { get; init; } = [];
}

public sealed record StarObservation(double Requested, double Actual, bool Preview, DateTimeOffset At);

public sealed record ManagedFile(string RelativePath, string Sha256, long ByteSize, string? OriginalName = null);

/// <summary>Beatmap metadata as the user wrote it. Artist is the music's performer; Creator is the mapper.</summary>
public sealed record ProjectMetadata
{
    public required string Title { get; init; }
    public string? TitleUnicode { get; init; }
    public required string Artist { get; init; }
    public string? ArtistUnicode { get; init; }
    public required string Creator { get; init; }
    public required string DifficultyName { get; init; }
    public string? Source { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record TimingCorrection(double Bpm, double? OffsetMs);

public sealed record ProjectSettings
{
    public double TargetStars { get; init; } = 5.0;
    public double? Ar { get; init; }
    public double? Od { get; init; }
    public double? Cs { get; init; }
    public double? Hp { get; init; }
    public int Seed { get; init; } = 7319;
    public TimingCorrection? Timing { get; init; }

    /// <summary>Local path of a trusted rhythm model (B2 or Pulso). Null means the B1 rules.</summary>
    public string? RhythmModel { get; init; }

    /// <summary>
    /// Mapping style for models conditioned on one (Pulso v3): "jumps", "streams" or null for the
    /// typical style at the target stars. Other models ignore it.
    /// </summary>
    public string? Style { get; init; }

    public static readonly IReadOnlyList<string> Styles = ["jumps", "streams"];

    /// <summary>
    /// Mapperatorinator style: beatmap tags the model was trained with ("skillset/jumps",
    /// "style/clean"), an osu! user id of a mapper it knows, and whether to follow them harder
    /// (classifier-free guidance, about twice the time). Other engines ignore these.
    /// </summary>
    public IReadOnlyList<string> Descriptors { get; init; } = [];

    public long? MapperId { get; init; }
    public bool StrongStyle { get; init; }

    /// <summary>The year whose style to imitate; the model's authors advise always giving one.</summary>
    public int Year { get; init; } = 2023;
}

/// <param name="Key">Hash of the original audio, the analyzer's identity and the timing correction.</param>
public sealed record AnalysisRef(string Key, string RelativePath, string AnalyzerId, TimingCorrection? Timing);

public enum RevisionKind
{
    /// <summary>Objects produced by the generator.</summary>
    Generated,

    /// <summary>The parent's objects with new metadata; nothing regenerated.</summary>
    Metadata,
}

public sealed record RevisionRecord
{
    public required string Id { get; init; }
    public string? Parent { get; init; }
    public required RevisionKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>BeatmapIR JSON of this revision.</summary>
    public required string BeatmapIr { get; init; }

    public required string AnalysisKey { get; init; }
    public required GenerationRecord Generation { get; init; }
    public required ProjectMetadata Metadata { get; init; }
}

/// <summary>Everything needed to tell how a revision's objects were made, without parsing free text.</summary>
public sealed record GenerationRecord
{
    /// <summary>"b1" (rules) or "b2" (learned rhythm model).</summary>
    public required string Generator { get; init; }

    public string? RhythmModelSha256 { get; init; }
    public required int Seed { get; init; }
    public required double TargetStars { get; init; }
    public required double ActualStars { get; init; }
    public required bool TargetReached { get; init; }
    public required double SpacingFactor { get; init; }
    public required string DifficultyProfile { get; init; }

    /// <summary>What was asked of the engine when it differs from the target (star calibration).</summary>
    public double? RequestedStars { get; init; }

    /// <summary>Set on a preview: only this stretch of the song was mapped, in milliseconds.</summary>
    public long? PreviewStartMs { get; init; }
    public long? PreviewEndMs { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPreview => PreviewStartMs is not null;
}
