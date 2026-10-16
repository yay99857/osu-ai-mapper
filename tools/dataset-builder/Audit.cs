using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OsuAi.DatasetBuilder;

public static class SplitFiles
{
    public static SplitManifest? Load(DatasetStore store)
    {
        string path = store.PathOf("manifests/split.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SplitManifest>(File.ReadAllText(path), DatasetStore.Json) : null;
    }

    public static void Save(DatasetStore store, SplitManifest manifest)
    {
        var options = new JsonSerializerOptions(DatasetStore.Json) { WriteIndented = true };
        store.WriteText("manifests/split.json", JsonSerializer.Serialize(manifest, options) + "\n");
    }
}

/// <summary>Checks a dataset before anything is measured on it. Every problem is listed; none is fixed silently.</summary>
public static class Audit
{
    public static IReadOnlyList<string> Run(DatasetStore store)
    {
        var problems = new List<string>();
        var maps = store.Maps;
        var rights = store.Rights.Select(r => r.Id).ToHashSet();
        var split = SplitFiles.Load(store);

        foreach (var m in maps)
        {
            if (!rights.Contains(m.RightsRef))
                problems.Add($"map {m.MapSha256[..12]} refers to unknown rights '{m.RightsRef}'");

            if (m.Status != IngestStatus.Accepted)
                continue;

            foreach (var (relative, expected) in new[] { (m.AudioRef, m.AudioSha256), (m.IrRef, (string?)null) })
            {
                if (relative is null)
                {
                    problems.Add($"map {m.MapSha256[..12]} is accepted but lacks a file reference");
                    continue;
                }

                string path = store.PathOf(relative);

                if (!File.Exists(path))
                    problems.Add($"missing file {relative}");
                else if (expected is not null && DatasetStore.Sha256Of(path) != expected)
                    problems.Add($"file {relative} does not match its hash");
            }
        }

        if (split is null)
        {
            problems.Add("no frozen split yet (run split)");
            return problems;
        }

        var accepted = maps.Where(m => m.Status == IngestStatus.Accepted).Select(m => m.MapSha256).ToHashSet();
        var splitOfMap = new Dictionary<string, Split>();
        var splitOfAudio = new Dictionary<string, Split>();

        foreach (var family in split.Families)
        {
            foreach (string map in family.MapSha256)
            {
                if (!splitOfMap.TryAdd(map, family.Split))
                    problems.Add($"map {map[..12]} is in two families");
            }

            foreach (string audio in family.AudioSha256)
            {
                if (splitOfAudio.TryGetValue(audio, out var other) && other != family.Split)
                    problems.Add($"audio {audio[..12]} appears in {other} and {family.Split}");

                splitOfAudio[audio] = family.Split;
            }
        }

        foreach (string map in accepted.Where(m => !splitOfMap.ContainsKey(m)))
            problems.Add($"accepted map {map[..12]} has no split (run split to assign new families)");

        // Regrouping current data must not put any frozen family across two splits.
        foreach (var family in Families.Group(maps))
        {
            var splits = family.MapSha256.Where(splitOfMap.ContainsKey).Select(m => splitOfMap[m]).Distinct().ToArray();
            if (splits.Length > 1)
                problems.Add($"family '{family.Key}' crosses {string.Join(" and ", splits)}");
        }

        return problems;
    }
}

public static class Report
{
    public static string Build(DatasetStore store)
    {
        var maps = store.Maps;
        var packages = store.Packages;
        var accepted = maps.Where(m => m.Status == IngestStatus.Accepted).ToList();
        var split = SplitFiles.Load(store);
        var s = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        s.AppendLine($"# Dataset report ({DateTimeOffset.UtcNow:yyyy-MM-dd})");
        s.AppendLine();
        s.AppendLine($"Rights grants: {string.Join(", ", store.Rights.Select(r => $"{r.Id} ({r.Scope.ToString().ToLowerInvariant()})"))}");
        s.AppendLine();
        s.AppendLine("| | Count |");
        s.AppendLine("|---|---:|");
        s.AppendLine($"| Packages | {packages.Count} |");
        s.AppendLine($"| Difficulties accepted | {accepted.Count} |");
        s.AppendLine($"| Excluded (other rulesets) | {maps.Count(m => m.Status == IngestStatus.Excluded)} |");
        s.AppendLine($"| Quarantined | {maps.Count(m => m.Status == IngestStatus.Quarantined) + packages.Count(p => p.Status == IngestStatus.Quarantined && p.OsuFiles == 0)} |");
        s.AppendLine($"| Distinct audio files | {accepted.Select(m => m.AudioSha256).Distinct().Count()} |");
        s.AppendLine($"| Constant timing (one red line) | {accepted.Count(m => m.RedLines == 1)} |");
        s.AppendLine($"| Single tempo (red lines within 0.01%) | {accepted.Count(m => m.TempoSpreadPercent <= 0.01)} |");
        s.AppendLine($"| Round trip without unexpected loss | {accepted.Count(m => m.RoundTripLossless == true)} |");
        s.AppendLine();

        var reasons = maps.SelectMany(m => m.Reasons).Concat(packages.SelectMany(p => p.Reasons)).GroupBy(r => r.Code).OrderByDescending(g => g.Count()).ToList();
        if (reasons.Count > 0)
        {
            s.AppendLine("## Quarantine and exclusion reasons");
            s.AppendLine();
            foreach (var g in reasons)
                s.AppendLine($"- `{g.Key}`: {g.Count()}");
            s.AppendLine();
        }

        if (accepted.Count > 0)
        {
            s.AppendLine("## Distributions (accepted)");
            s.AppendLine();
            s.AppendLine("Stars (" + accepted.First().DifficultyProfile + "): " + Histogram(accepted.Select(m => m.StarRating ?? 0), [0, 2, 3, 4, 5, 6, 7], inv));
            s.AppendLine();
            s.AppendLine("Main BPM: " + Histogram(accepted.Select(m => m.MainBpm ?? 0), [0, 120, 150, 180, 210, 240], inv));
            s.AppendLine();
        }

        if (split is not null)
        {
            s.AppendLine($"## Split v{split.Version} (frozen {split.FrozenAt:yyyy-MM-dd})");
            s.AppendLine();
            s.AppendLine($"Rule: {split.Rule}");
            s.AppendLine();
            foreach (var g in split.Families.GroupBy(f => f.Split).OrderBy(g => g.Key))
                s.AppendLine($"- {g.Key}: {g.Count()} families, {g.Sum(f => f.MapSha256.Count)} difficulties");
            s.AppendLine();
            s.AppendLine("Not linked yet: re-encodes or cuts whose metadata differs (no acoustic fingerprint). Treat test results as optimistic until that exists.");
        }

        return s.ToString();
    }

    private static string Histogram(IEnumerable<double> values, double[] edges, CultureInfo inv)
    {
        var list = values.ToList();
        var parts = new List<string>();

        for (int i = 0; i < edges.Length; i++)
        {
            double lo = edges[i];
            double hi = i + 1 < edges.Length ? edges[i + 1] : double.PositiveInfinity;
            int count = list.Count(v => v >= lo && v < hi);
            string label = double.IsPositiveInfinity(hi) ? $"{lo.ToString(inv)}+" : $"{lo.ToString(inv)}–{hi.ToString(inv)}";
            parts.Add($"{label}: {count}");
        }

        return string.Join(", ", parts);
    }
}
