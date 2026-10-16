namespace OsuAi.DatasetBuilder;

/// <summary>What a rights grant allows. Absent means unknown, never permissive (blueprint 05).</summary>
public enum RightsScope
{
    /// <summary>Local measurement only: no training, no redistribution.</summary>
    Evaluation,

    /// <summary>Training models that are not distributed.</summary>
    Training,

    /// <summary>Training models that may be distributed.</summary>
    Distribution,
}

/// <summary>One entry of the rights ledger. Every ingested package points to one.</summary>
public sealed record RightsGrant
{
    public required string Id { get; init; }
    public required RightsScope Scope { get; init; }
    public required string Source { get; init; }
    public required string GrantedBy { get; init; }
    public required DateTimeOffset GrantedAt { get; init; }
    public string? Note { get; init; }
}

public enum IngestStatus
{
    Accepted,

    /// <summary>Valid but out of scope, for example another ruleset. Not an error.</summary>
    Excluded,

    /// <summary>Failed a check; kept out with a reason for review.</summary>
    Quarantined,
}

public sealed record Reason(string Code, string Detail);

public sealed record PackageRecord
{
    public required string PackageSha256 { get; init; }
    public required string SourcePath { get; init; }
    public required long Bytes { get; init; }
    public required int Entries { get; init; }
    public required int OsuFiles { get; init; }
    public required IngestStatus Status { get; init; }
    public IReadOnlyList<Reason> Reasons { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public required string RightsRef { get; init; }
    public required DateTimeOffset IngestedAt { get; init; }
    public required string Builder { get; init; }
}

/// <summary>One difficulty. Its identity is the SHA-256 of the .osu bytes.</summary>
public sealed record MapRecord
{
    public required string MapSha256 { get; init; }
    public required string PackageSha256 { get; init; }
    public required string OsuEntry { get; init; }
    public required IngestStatus Status { get; init; }
    public IReadOnlyList<Reason> Reasons { get; init; } = [];

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? TitleUnicode { get; init; }
    public string? ArtistUnicode { get; init; }
    public string? Creator { get; init; }
    public string? Version { get; init; }
    public int? BeatmapId { get; init; }
    public int? BeatmapSetId { get; init; }
    public int? FormatVersion { get; init; }

    public string? AudioEntry { get; init; }
    public string? AudioSha256 { get; init; }
    public string? AudioContainer { get; init; }

    /// <summary>Relative to the dataset root.</summary>
    public string? AudioRef { get; init; }

    public string? IrRef { get; init; }

    public double? MainBpm { get; init; }
    public int? RedLines { get; init; }

    /// <summary>(max - min) / min of the red-line tempos, in percent. 0 for a single tempo.</summary>
    public double? TempoSpreadPercent { get; init; }
    public long? FirstObjectUs { get; init; }
    public long? LastObjectUs { get; init; }
    public int? Circles { get; init; }
    public int? Sliders { get; init; }
    public int? Spinners { get; init; }

    public double? StarRating { get; init; }
    public string? DifficultyProfile { get; init; }
    public bool? RoundTripLossless { get; init; }
    public IReadOnlyList<string> DeclaredLosses { get; init; } = [];

    public required string RightsRef { get; init; }
    public required DateTimeOffset IngestedAt { get; init; }
    public required string Builder { get; init; }
}

public enum Split
{
    Train,
    Validation,
    Test,
}

public sealed record FamilyAssignment
{
    public required string FamilyId { get; init; }

    /// <summary>The smallest normalised "artist|title" of the family; what the split hash is taken from.</summary>
    public required string Key { get; init; }

    public required Split Split { get; init; }
    public required IReadOnlyList<string> MapSha256 { get; init; }
    public required IReadOnlyList<string> AudioSha256 { get; init; }
}

public sealed record SplitManifest
{
    public required int Version { get; init; }
    public required DateTimeOffset FrozenAt { get; init; }
    public required string Rule { get; init; }
    public required IReadOnlyList<FamilyAssignment> Families { get; init; }
}
