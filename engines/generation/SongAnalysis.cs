using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuAi.Generation;

/// <summary>
/// The proof-of-concept analysis written by engines/audio-analysis (schema "poc-0.3"; "poc-0.2" lacks the HPSS features, which only the
/// rhythm models read, and is still accepted). Observations
/// and a single constant timing hypothesis; features sampled on its 1/4 grid. It contains no
/// mapping decisions.
/// </summary>
public sealed record SongAnalysis
{
    public const string SupportedSchema = "poc-0.3";
    public static readonly IReadOnlyList<string> ReadableSchemas = ["poc-0.2", SupportedSchema];

    public required string SchemaVersion { get; init; }
    public required AnalysedAudio Audio { get; init; }
    public required AnalysedTiming Timing { get; init; }
    public required int GridDivisor { get; init; }
    public required IReadOnlyList<GridPoint> Grid { get; init; }
    public required IReadOnlyList<AnalysedSection> Sections { get; init; }

    private static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static SongAnalysis Load(string path)
    {
        var analysis = JsonSerializer.Deserialize<SongAnalysis>(File.ReadAllText(path), options)
                       ?? throw new InvalidDataException("Analysis file is empty.");

        if (!ReadableSchemas.Contains(analysis.SchemaVersion))
            throw new InvalidDataException($"Unsupported analysis schema '{analysis.SchemaVersion}', expected '{SupportedSchema}'.");

        if (analysis.GridDivisor != 4)
            throw new InvalidDataException("Only a 1/4 analysis grid is supported.");

        return analysis;
    }
}

public sealed record AnalysedAudio(string Sha256, long DurationUs, int SampleRate);

public sealed record AnalysedTiming
{
    public required double Bpm { get; init; }
    public required double BeatLengthUs { get; init; }
    public required long OffsetUs { get; init; }
    public required double Confidence { get; init; }
    public double TrackerBpm { get; init; }

    /// <summary>"detected", or "manual" when the user corrected the BPM (and possibly the offset).</summary>
    public required string Source { get; init; }

    public bool IsManual => Source == "manual";
}

/// <summary>One 1/4-beat grid position. Index 0 is a downbeat; values are normalised to about [0, 1].</summary>
public sealed record GridPoint
{
    public required int Index { get; init; }
    public required long TimeUs { get; init; }
    public required double Onset { get; init; }
    public required double Low { get; init; }
    public required double High { get; init; }
    public required double Energy { get; init; }
    public required double Sustain { get; init; }
}

/// <summary>Bar range with an intensity level from 0 (quiet) to 3 (peak). Not a semantic label.</summary>
public sealed record AnalysedSection(long StartUs, long EndUs, int FirstBar, int BarCount, int Intensity, double Energy);
