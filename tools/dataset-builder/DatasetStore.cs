using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuAi.DatasetBuilder;

/// <summary>
/// The dataset directory (blueprint 05 layout, without the parts not needed yet):
/// <code>
/// manifests/  rights.jsonl, packages.jsonl, maps.jsonl, split-vN.json
/// audio/      content-addressed copies, &lt;sha256&gt;.&lt;ext&gt;
/// beatmaps/   BeatmapIR JSON per difficulty, &lt;map sha256&gt;.json
/// reports/    generated summaries
/// </code>
/// Manifests are append-only JSON lines; the newest line for an ID wins. Files are written to a
/// temporary name and renamed, so an interrupted run never leaves a half-written file.
/// The directory must stay out of git: it holds third-party material.
/// </summary>
public sealed class DatasetStore
{
    public const string BuilderVersion = "dataset-builder 0.1";

    public static JsonSerializerOptions Json { get; } = CreateOptions();

    public DatasetStore(string root)
    {
        Root = Path.GetFullPath(root);

        foreach (string dir in new[] { "manifests", "audio", "beatmaps", "reports" })
            Directory.CreateDirectory(Path.Combine(Root, dir));
    }

    public string Root { get; }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public IReadOnlyList<RightsGrant> Rights => ReadLines<RightsGrant>("manifests/rights.jsonl");

    /// <summary>Latest record per package.</summary>
    public IReadOnlyList<PackageRecord> Packages => Latest(ReadLines<PackageRecord>("manifests/packages.jsonl"), p => p.PackageSha256);

    /// <summary>Latest record per difficulty.</summary>
    public IReadOnlyList<MapRecord> Maps => Latest(ReadLines<MapRecord>("manifests/maps.jsonl"), m => m.MapSha256);

    public void AddRights(RightsGrant grant)
    {
        if (Rights.Any(r => r.Id == grant.Id))
            throw new InvalidOperationException($"Rights grant '{grant.Id}' already exists; grants are immutable, add a new one.");

        Append("manifests/rights.jsonl", grant);
    }

    public void Append<T>(string relative, T record)
    {
        string line = JsonSerializer.Serialize(record, Json) + "\n";
        File.AppendAllText(PathOf(relative), line, new UTF8Encoding(false));
    }

    /// <summary>Stores bytes under their hash; returns the relative path. Existing identical files are kept.</summary>
    public string StoreContent(string directory, byte[] data, string extension)
    {
        string sha = Convert.ToHexStringLower(SHA256.HashData(data));
        string relative = $"{directory}/{sha}{extension}";
        string path = PathOf(relative);

        if (!File.Exists(path))
            WriteAtomic(path, data);

        return relative;
    }

    public void WriteText(string relative, string text) => WriteAtomic(PathOf(relative), new UTF8Encoding(false).GetBytes(text));

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    private List<T> ReadLines<T>(string relative)
    {
        string path = PathOf(relative);

        if (!File.Exists(path))
            return [];

        return File.ReadLines(path)
                   .Where(l => !string.IsNullOrWhiteSpace(l))
                   .Select(l => JsonSerializer.Deserialize<T>(l, Json)!)
                   .ToList();
    }

    private static List<T> Latest<T>(List<T> records, Func<T, string> key)
    {
        var latest = new Dictionary<string, T>();
        foreach (var r in records)
            latest[key(r)] = r;

        return latest.Values.ToList();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
