using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuAi.ProjectStore;

/// <summary>
/// A project folder:
/// <code>
/// project.json                    manifest (the only file ever rewritten, atomically)
/// assets/original.&lt;ext&gt;          the imported file, unchanged
/// assets/audio.mp3                the delivered audio
/// analysis/&lt;key&gt;/analysis.json   cached analyses, by content key
/// revisions/rev-0001/...          immutable revisions
/// exports/                        packages written on request
/// </code>
/// Revisions are assembled in a staging folder and moved into place before the manifest names them,
/// so an interrupted operation leaves the previous state intact. A leftover staging folder is
/// garbage, never state.
/// </summary>
public sealed class Project
{
    public const string ManifestName = "project.json";

    public static JsonSerializerOptions Json { get; } = CreateOptions();

    private Project(string root, ProjectManifest manifest)
    {
        Root = root;
        Manifest = manifest;
    }

    public string Root { get; }

    public ProjectManifest Manifest { get; private set; }

    public RevisionRecord? ActiveRevision => Manifest.Revisions.FirstOrDefault(r => r.Id == Manifest.ActiveRevision);

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static bool Exists(string root) => File.Exists(Path.Combine(root, ManifestName));

    /// <summary>Creates a project around an audio file, copied in unchanged.</summary>
    public static Project Create(string root, string audioPath, ProjectMetadata metadata, ProjectSettings settings)
    {
        root = Path.GetFullPath(root);

        if (Exists(root))
            throw new InvalidOperationException($"'{root}' already holds a project.");

        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException($"'{root}' is not empty; choose an empty or new folder for the project.");

        Directory.CreateDirectory(Path.Combine(root, "assets"));

        string extension = Path.GetExtension(audioPath).ToLowerInvariant();
        string relative = $"assets/original{extension}";
        string destination = Path.Combine(root, "assets", $"original{extension}");
        File.Copy(audioPath, destination);

        var now = DateTimeOffset.UtcNow;
        var manifest = new ProjectManifest
        {
            ProjectId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            Original = new ManagedFile(relative, Sha256Of(destination), new FileInfo(destination).Length, Path.GetFileName(audioPath)),
            Metadata = metadata,
            Settings = settings,
        };

        var project = new Project(root, manifest);
        project.SaveManifest(manifest);
        return project;
    }

    public static Project Open(string root)
    {
        root = Path.GetFullPath(root);
        string path = Path.Combine(root, ManifestName);

        if (!File.Exists(path))
            throw new FileNotFoundException("No project.json in this folder.", path);

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path), Json)
                       ?? throw new InvalidDataException("project.json is empty.");

        if (manifest.SchemaVersion != ProjectManifest.CurrentSchema)
            throw new InvalidDataException($"Unsupported project schema '{manifest.SchemaVersion}'.");

        return new Project(root, manifest);
    }

    /// <summary>Copies an image into assets/background.ext and records it; replaces an earlier one.</summary>
    public ManagedFile SetBackground(string imagePath)
    {
        string extension = Path.GetExtension(imagePath).ToLowerInvariant();
        string relative = $"assets/background{extension}";
        string destination = Path.Combine(Root, "assets", $"background{extension}");
        Directory.CreateDirectory(Path.Combine(Root, "assets"));
        string temp = destination + ".tmp";
        File.Copy(imagePath, temp, overwrite: true);
        if (Manifest.Background is { } old && old.RelativePath != relative && File.Exists(PathOf(old.RelativePath)))
            File.Delete(PathOf(old.RelativePath));
        File.Move(temp, destination, overwrite: true);
        var file = new ManagedFile(relative, Sha256Of(destination), new FileInfo(destination).Length, Path.GetFileName(imagePath));
        Update(m => m with { Background = file });
        return file;
    }

    public void ClearBackground()
    {
        if (Manifest.Background is { } old && File.Exists(PathOf(old.RelativePath)))
            File.Delete(PathOf(old.RelativePath));
        Update(m => m with { Background = null });
    }

    /// <summary>Replaces the manifest atomically: write a temporary file, then rename over the old one.</summary>
    public void Update(Func<ProjectManifest, ProjectManifest> change)
    {
        var next = change(Manifest) with { UpdatedAt = DateTimeOffset.UtcNow };
        SaveManifest(next);
    }

    /// <summary>
    /// Adds a revision: <paramref name="write"/> fills a staging folder, which is then moved to
    /// revisions/&lt;id&gt; and only afterwards recorded (and made active) in the manifest.
    /// </summary>
    public RevisionRecord AddRevision(Func<string, string, RevisionRecord> write)
    {
        string id = $"rev-{Manifest.Revisions.Count + 1:0000}";
        string revisions = Path.Combine(Root, "revisions");
        Directory.CreateDirectory(revisions);
        string staging = Path.Combine(revisions, $".staging-{Guid.NewGuid():N}");
        string final = Path.Combine(revisions, id);

        if (Directory.Exists(final))
            throw new InvalidOperationException($"Revision folder '{id}' already exists but is not in the manifest.");

        Directory.CreateDirectory(staging);

        try
        {
            var record = write(staging, $"revisions/{id}");
            Directory.Move(staging, final);
            Update(m => m with { Revisions = [.. m.Revisions, record], ActiveRevision = record.Id });
            return record;
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public string NextRevisionId => $"rev-{Manifest.Revisions.Count + 1:0000}";

    /// <summary>Writes bytes to a project path through a temporary file and a rename.</summary>
    public string WriteFile(string relative, byte[] data)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, data);
        return relative;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static void AtomicWrite(string path, byte[] data)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    private void SaveManifest(ProjectManifest manifest)
    {
        AtomicWrite(Path.Combine(Root, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        Manifest = manifest;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
