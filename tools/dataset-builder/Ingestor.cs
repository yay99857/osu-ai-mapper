using System.Security.Cryptography;
using OsuAi.BeatmapCore;
using OsuAi.OsuAdapter;

namespace OsuAi.DatasetBuilder;

public sealed record IngestSummary(int Packages, int SkippedPackages, int Accepted, int Excluded, int Quarantined);

/// <summary>
/// DATA-01: brings user-provided .osz files and beatmap folders into the dataset. Every package is
/// read under limits, every difficulty goes through the official parser and a round trip, and the
/// audio is taken only from inside the package. Idempotent: packages and difficulties already
/// recorded are skipped by hash.
/// </summary>
public sealed class Ingestor(DatasetStore store, RightsGrant rights, TextWriter log)
{
    public IngestSummary Ingest(string source)
    {
        int packages = 0, skipped = 0, accepted = 0, excluded = 0, quarantined = 0;
        var knownPackages = store.Packages.Select(p => p.PackageSha256).ToHashSet();
        var knownMaps = store.Maps.Where(m => m.Status != IngestStatus.Quarantined).Select(m => m.MapSha256).ToHashSet();

        foreach (string item in Sources(source))
        {
            string sha;
            try
            {
                sha = Directory.Exists(item) ? SafePackageReader.Read(item).SourceSha256 : DatasetStore.Sha256Of(item);
            }
            catch (UnsafePackageException e)
            {
                RecordPackage(item, "unreadable-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item))), 0, 0, 0, IngestStatus.Quarantined, [new Reason("package.unsafe", e.Message)], []);
                quarantined++;
                continue;
            }

            if (knownPackages.Contains(sha))
            {
                skipped++;
                continue;
            }

            packages++;
            var result = IngestPackage(item, knownMaps);
            accepted += result.Accepted;
            excluded += result.Excluded;
            quarantined += result.Quarantined;
            knownPackages.Add(sha);
        }

        return new IngestSummary(packages, skipped, accepted, excluded, quarantined);
    }

    /// <summary>.osz files, and folders that directly contain .osu files, under the source (or the source itself).</summary>
    internal static IEnumerable<string> Sources(string source)
    {
        if (File.Exists(source))
        {
            yield return source;
            yield break;
        }

        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Source '{source}' does not exist.");

        if (Directory.EnumerateFiles(source, "*.osu").Any())
        {
            yield return source;
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(source, "*.osz", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            yield return file;

        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Directory.EnumerateFiles(dir, "*.osu").Any())
                yield return dir;
        }
    }

    private (int Accepted, int Excluded, int Quarantined) IngestPackage(string path, HashSet<string> knownMaps)
    {
        PackageContents contents;

        try
        {
            contents = SafePackageReader.Read(path);

            // Second pass with the audio each difficulty names, now that the .osu files are known.
            var audio = contents.OsuFiles
                                .Select(e => AudioFileName(contents.Read(e)))
                                .Where(a => a is not null)
                                .Select(a => contents.Resolve(a!)?.Path)
                                .Where(p => p is not null)
                                .Distinct()
                                .ToArray();

            if (audio.Length > 0)
                contents = SafePackageReader.ReadWith(path, audio!);
        }
        catch (UnsafePackageException e)
        {
            string sha = Directory.Exists(path) ? "folder" : DatasetStore.Sha256Of(path);
            RecordPackage(path, sha, new FileInfo(path).Exists ? new FileInfo(path).Length : 0, 0, 0, IngestStatus.Quarantined, [new Reason("package.unsafe", e.Message)], []);
            log.WriteLine($"  quarantined package {Path.GetFileName(path)}: {e.Message}");
            return (0, 0, 1);
        }

        int accepted = 0, excluded = 0, quarantined = 0;

        foreach (var osu in contents.OsuFiles)
        {
            var record = IngestMap(contents, osu, knownMaps);

            if (record is null)
                continue;

            store.Append("manifests/maps.jsonl", record);
            knownMaps.Add(record.MapSha256);

            switch (record.Status)
            {
                case IngestStatus.Accepted: accepted++; break;
                case IngestStatus.Excluded: excluded++; break;
                default:
                    quarantined++;
                    log.WriteLine($"  quarantined {osu.Path}: {string.Join("; ", record.Reasons.Select(r => r.Code))}");
                    break;
            }
        }

        var status = accepted > 0 ? IngestStatus.Accepted : quarantined > 0 ? IngestStatus.Quarantined : IngestStatus.Excluded;
        RecordPackage(path, contents.SourceSha256, contents.SourceBytes, contents.Entries.Count, contents.OsuFiles.Count(), status, [], contents.Warnings);
        log.WriteLine($"{Path.GetFileName(path)}: {accepted} accepted, {excluded} excluded, {quarantined} quarantined");
        return (accepted, excluded, quarantined);
    }

    private MapRecord? IngestMap(PackageContents contents, PackageEntry osu, HashSet<string> knownMaps)
    {
        byte[] bytes = contents.Read(osu);
        string mapSha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (knownMaps.Contains(mapSha))
            return null;

        var baseRecord = new MapRecord
        {
            MapSha256 = mapSha,
            PackageSha256 = contents.SourceSha256,
            OsuEntry = osu.Path,
            Status = IngestStatus.Accepted,
            RightsRef = rights.Id,
            IngestedAt = DateTimeOffset.UtcNow,
            Builder = DatasetStore.BuilderVersion,
        };

        RoundTripReport report;

        try
        {
            report = RoundTrip.Run(bytes);
        }
        catch (NotSupportedException e)
        {
            return baseRecord with { Status = IngestStatus.Excluded, Reasons = [new Reason("mode.not-standard", e.Message)] };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return baseRecord with { Status = IngestStatus.Quarantined, Reasons = [new Reason("parse.failed", e.Message)] };
        }

        var ir = OsuImporter.Import(bytes).Beatmap;

        // Our own output must never pass for human mapping, in training or in evaluation. The generator
        // and the harness demo always tag their maps "osu-ai-mapper".
        if (ir.Metadata.Tags.Contains("osu-ai-mapper"))
            return baseRecord with { Status = IngestStatus.Excluded, Reasons = [new Reason("generated.machine-made", "Made by osu-ai-mapper, not by a human mapper.")] };

        var reasons = new List<Reason>();

        foreach (var error in report.ImportDiagnostics.Where(d => d.Severity == Severity.Error))
            reasons.Add(new Reason("ir.invalid", error.ToString()));

        if (ir.Objects.Count == 0)
            reasons.Add(new Reason("map.empty", "No hit objects."));

        var audioEntry = contents.Resolve(ir.Assets.Audio.RelativePath);
        string? audioRef = null;
        string? container = null;

        if (audioEntry is null)
        {
            reasons.Add(new Reason("audio.missing", $"'{ir.Assets.Audio.RelativePath}' is not in the package."));
        }
        else
        {
            byte[] audio = contents.Read(audioEntry);
            container = AudioSniffer.Container(audio);

            if (container is null)
                reasons.Add(new Reason("audio.unknown-format", $"'{audioEntry.Path}' is not MP3, OGG, WAV, FLAC or MP4 audio."));
            else
                audioRef = store.StoreContent("audio", audio, "." + container);

            ir = ir with
            {
                Assets = ir.Assets with
                {
                    Audio = ir.Assets.Audio with { Sha256 = audioEntry.Sha256, ByteSize = audioEntry.Size, RightsRef = rights.Id },
                },
            };
        }

        string? irRef = null;

        if (reasons.Count == 0)
        {
            irRef = $"beatmaps/{mapSha}.json";
            store.WriteText(irRef, IrJson.Serialize(ir));
        }

        var segments = ir.Timing.Segments;
        var circles = ir.Objects.OfType<CircleIr>().Count();
        var sliders = ir.Objects.OfType<SliderIr>().Count();
        var spinners = ir.Objects.OfType<SpinnerIr>().Count();

        return baseRecord with
        {
            Status = reasons.Count == 0 ? IngestStatus.Accepted : IngestStatus.Quarantined,
            Reasons = reasons,
            Title = ir.Metadata.Title,
            Artist = ir.Metadata.Artist,
            TitleUnicode = ir.Metadata.TitleUnicode,
            ArtistUnicode = ir.Metadata.ArtistUnicode,
            Creator = ir.Metadata.Creator,
            Version = ir.Metadata.DifficultyName,
            BeatmapId = ir.Metadata.OnlineIds?.BeatmapId,
            BeatmapSetId = ir.Metadata.OnlineIds?.BeatmapSetId,
            FormatVersion = ir.Provenance.SourceFormatVersion,
            AudioEntry = audioEntry?.Path,
            AudioSha256 = audioEntry?.Sha256,
            AudioContainer = container,
            AudioRef = audioRef,
            IrRef = irRef,
            MainBpm = MainBpm(ir),
            RedLines = segments.Count,
            TempoSpreadPercent = segments.Count == 0 ? null : Math.Round((segments.Max(t => t.BeatLengthUs) / segments.Min(t => t.BeatLengthUs) - 1) * 100, 4),
            FirstObjectUs = ir.Objects.Count > 0 ? ir.Objects[0].TimeUs : null,
            LastObjectUs = ir.Objects.Count > 0 ? ir.Objects[^1].TimeUs : null,
            Circles = circles,
            Sliders = sliders,
            Spinners = spinners,
            StarRating = report.OriginalDifficulty.StarRating,
            DifficultyProfile = report.OriginalDifficulty.Profile,
            RoundTripLossless = report.IsLossless,
            DeclaredLosses = report.DeclaredLosses.Select(d => d.Code).Distinct().ToArray(),
        };
    }

    /// <summary>The tempo that covers most of the mapped time.</summary>
    internal static double? MainBpm(BeatmapIr ir)
    {
        if (ir.Objects.Count == 0 || ir.Timing.Segments.Count == 0)
            return null;

        long start = ir.Objects[0].TimeUs;
        long end = ir.Objects[^1].TimeUs;
        var segments = ir.Timing.Segments;
        var weight = new Dictionary<double, long>();

        for (int i = 0; i < segments.Count; i++)
        {
            long from = Math.Max(start, i == 0 ? long.MinValue : segments[i].StartTimeUs);
            long to = Math.Min(end, i + 1 < segments.Count ? segments[i + 1].StartTimeUs : long.MaxValue);
            double bpm = Math.Round(60_000_000 / segments[i].BeatLengthUs, 3);

            if (to > from)
                weight[bpm] = weight.GetValueOrDefault(bpm) + (to - from);
        }

        return weight.Count == 0 ? Math.Round(60_000_000 / segments[0].BeatLengthUs, 3) : weight.MaxBy(kv => kv.Value).Key;
    }

    private static string? AudioFileName(byte[] osu)
    {
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(osu));

        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.StartsWith("[Editor]", StringComparison.Ordinal) || line.StartsWith("[Metadata]", StringComparison.Ordinal))
                break;

            if (line.StartsWith("AudioFilename", StringComparison.Ordinal) && line.IndexOf(':') is var colon and > 0)
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private void RecordPackage(string path, string sha, long bytes, int entries, int osuFiles, IngestStatus status, IReadOnlyList<Reason> reasons, IReadOnlyList<string> warnings) =>
        store.Append("manifests/packages.jsonl", new PackageRecord
        {
            PackageSha256 = sha,
            SourcePath = path,
            Bytes = bytes,
            Entries = entries,
            OsuFiles = osuFiles,
            Status = status,
            Reasons = reasons,
            Warnings = warnings,
            RightsRef = rights.Id,
            IngestedAt = DateTimeOffset.UtcNow,
            Builder = DatasetStore.BuilderVersion,
        });
}

/// <summary>Identifies audio by its bytes, not its extension: downloads named .mp3 are often MP4.</summary>
public static class AudioSniffer
{
    public static string? Container(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return null;

        if (data[..4].SequenceEqual("OggS"u8))
            return "ogg";

        if (data[..4].SequenceEqual("fLaC"u8))
            return "flac";

        if (data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8))
            return "wav";

        if (data[4..8].SequenceEqual("ftyp"u8))
            return "m4a";

        if (data[..3].SequenceEqual("ID3"u8) || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0))
            return "mp3";

        return null;
    }
}
