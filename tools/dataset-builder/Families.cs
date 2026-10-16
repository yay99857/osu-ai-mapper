using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OsuAi.DatasetBuilder;

/// <summary>
/// DATA-02: groups difficulties into music families and freezes a split by family, so no family
/// (and no audio file) is ever on both sides of a train/test boundary (blueprint 05, leakage).
///
/// Linked into one family: same audio bytes, same package, same online set, and the same
/// normalised artist and title, where normalisation drops version markers such as nightcore,
/// sped up, cut, TV size and featuring credits. Acoustic fingerprints (for re-encodes and cuts
/// with edited metadata) are not implemented yet; until then such pairs can leak, and the report
/// says so.
/// </summary>
public static partial class Families
{
    public const string Rule = "union(audio sha, package sha, online set id, normalised artist+title); hash(key) → 80/10/10";

    public static List<FamilyAssignment> Group(IReadOnlyList<MapRecord> maps)
    {
        var accepted = maps.Where(m => m.Status == IngestStatus.Accepted).OrderBy(m => m.MapSha256, StringComparer.Ordinal).ToList();
        var parent = Enumerable.Range(0, accepted.Count).ToArray();

        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        var firstByLink = new Dictionary<string, int>();

        for (int i = 0; i < accepted.Count; i++)
        {
            foreach (string link in Links(accepted[i]))
            {
                if (firstByLink.TryGetValue(link, out int other))
                    Union(i, other);
                else
                    firstByLink[link] = i;
            }
        }

        return Enumerable.Range(0, accepted.Count)
                         .GroupBy(Find)
                         .Select(g =>
                         {
                             var members = g.Select(i => accepted[i]).ToList();
                             string key = members.Select(Key).Order(StringComparer.Ordinal).First();
                             return new FamilyAssignment
                             {
                                 FamilyId = "fam-" + Hash(key)[..12],
                                 Key = key,
                                 Split = SplitFor(key),
                                 MapSha256 = members.Select(m => m.MapSha256).Order(StringComparer.Ordinal).ToArray(),
                                 AudioSha256 = members.Select(m => m.AudioSha256!).Distinct().Order(StringComparer.Ordinal).ToArray(),
                             };
                         })
                         .OrderBy(f => f.Key, StringComparer.Ordinal)
                         .ToList();
    }

    /// <summary>
    /// Keeps every assignment of the frozen split and assigns only new families. Fails when new data
    /// would merge families that were frozen in different splits: that needs a new split version,
    /// not a silent move of test data.
    /// </summary>
    public static SplitManifest Freeze(IReadOnlyList<MapRecord> maps, SplitManifest? previous)
    {
        var families = Group(maps);

        if (previous is null)
            return new SplitManifest { Version = 1, FrozenAt = DateTimeOffset.UtcNow, Rule = Rule, Families = families };

        var frozenSplitOfMap = previous.Families.SelectMany(f => f.MapSha256.Select(m => (m, f.Split))).ToDictionary(x => x.m, x => x.Split);
        var result = new List<FamilyAssignment>();

        foreach (var family in families)
        {
            var frozen = family.MapSha256.Where(frozenSplitOfMap.ContainsKey).Select(m => frozenSplitOfMap[m]).Distinct().ToArray();

            if (frozen.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Family '{family.Key}' now joins maps frozen in {string.Join(" and ", frozen)}. Create a new split version deliberately.");
            }

            result.Add(frozen.Length == 1 ? family with { Split = frozen[0] } : family);
        }

        return new SplitManifest { Version = previous.Version, FrozenAt = previous.FrozenAt, Rule = previous.Rule, Families = result };
    }

    internal static IEnumerable<string> Links(MapRecord map)
    {
        if (map.AudioSha256 is { } audio)
            yield return "audio:" + audio;

        yield return "package:" + map.PackageSha256;

        if (map.BeatmapSetId is { } set)
            yield return "set:" + set;

        yield return "song:" + Key(map);

        if (map.TitleUnicode is not null || map.ArtistUnicode is not null)
            yield return "song:" + NormaliseArtist(map.ArtistUnicode ?? map.Artist ?? "") + "|" + NormaliseTitle(map.TitleUnicode ?? map.Title ?? "");
    }

    internal static string Key(MapRecord map) => NormaliseArtist(map.Artist ?? "") + "|" + NormaliseTitle(map.Title ?? "");

    /// <summary>Version markers that do not make a different composition.</summary>
    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*(nightcore|sped|speed|slowed|cut|tv|size|ver\b|version|short|edit|extended|full|game|feat|ft\.)[^\)\]\}]*[\)\]\}]", RegexOptions.IgnoreCase)]
    private static partial Regex VersionMarker();

    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingTail();

    [GeneratedRegex(@"\s*(,|&|\bx\b|\band\b|\bvs\.?|\bfeat\.?|\bft\.?|\bfeaturing\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparator();

    internal static string NormaliseTitle(string title)
    {
        string t = VersionMarker().Replace(title, " ");
        t = FeaturingTail().Replace(t, "");
        return Simplify(t);
    }

    /// <summary>Artist credits as a sorted set, so "A & B", "B, A" and "A feat. B" all match.</summary>
    internal static string NormaliseArtist(string artist)
    {
        var names = ArtistSeparator().Split(artist)
                                     .Select(Simplify)
                                     .Where(n => n.Length > 0 && n is not ("feat" or "ft" or "x" or "and" or "vs"))
                                     .Distinct()
                                     .Order(StringComparer.Ordinal);
        return string.Join("+", names);
    }

    private static string Simplify(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static Split SplitFor(string key)
    {
        // First 8 bytes of SHA-256 as a fraction in [0, 1): stable across runs and machines.
        double u = BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 8)) / (double)ulong.MaxValue;
        return u < 0.8 ? Split.Train : u < 0.9 ? Split.Validation : Split.Test;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
