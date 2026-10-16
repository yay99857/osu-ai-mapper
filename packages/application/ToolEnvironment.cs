using System.Security.Cryptography;
using System.Text;

namespace OsuAi.Application;

/// <summary>
/// Where the Python tools are and what version of them is running. In the development setup the
/// tools run from the repository through uv; a packaged runtime (roadmap M5) will replace this.
/// </summary>
public sealed class ToolEnvironment
{
    public ToolEnvironment(string repositoryRoot, string uv = "uv")
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        Uv = uv;
        AnalyzerId = HashSources("analyzer-", (AnalyzerProject, "osu_ai_audio"));
        RhythmToolId = HashSources("rhythm-", (RhythmProject, "osu_ai_rhythm"), (SequenceProject, "osu_ai_sequence"));
    }

    public string RepositoryRoot { get; }

    public string Uv { get; }

    public string AnalyzerProject => Path.Combine(RepositoryRoot, "engines", "audio-analysis");

    public string RhythmProject => Path.Combine(RepositoryRoot, "ml", "rhythm");

    /// <summary>A local Mapperatorinator checkout with its own virtual environment (optional engine).</summary>
    public string MapperatorinatorRoot => Path.Combine(RepositoryRoot, "artifacts", "external", "Mapperatorinator");

    public string MapperatorinatorPython => Path.Combine(MapperatorinatorRoot, ".venv", "Scripts", "python.exe");

    public bool MapperatorinatorAvailable => File.Exists(MapperatorinatorPython) && File.Exists(Path.Combine(MapperatorinatorRoot, "inference.py"));

    /// <summary>Demucs source separation (PyTorch, its own environment), for models that read stems.</summary>
    public string SeparationProject => Path.Combine(RepositoryRoot, "engines", "source-separation");

    /// <summary>Pulso-S, the sequence model; it needs PyTorch and has its own environment.</summary>
    public string SequenceProject => Path.Combine(RepositoryRoot, "ml", "sequence");

    /// <summary>
    /// Identity of the analyzer: a hash of its source files and lock file. Any change to the analysis
    /// code changes it, so analyses cached by an older analyzer are not reused.
    /// </summary>
    public string AnalyzerId { get; }

    /// <summary>
    /// Identity of the rhythm inference code, like <see cref="AnalyzerId"/>. Predictions depend on
    /// the decoder as much as on the model weights, so cached predictions are keyed by both.
    /// </summary>
    public string RhythmToolId { get; }

    /// <summary>Written by the installer next to an installed app: where the repository and uv are.</summary>
    public const string ConfigFileName = "osu-ai-mapper.json";

    /// <summary>
    /// The repository with the Python tools and Mapperatorinator: the one named in
    /// osu-ai-mapper.json beside the program (an installed app), else the repository containing the
    /// running build, found by walking up to OsuAiMapper.slnx.
    /// </summary>
    public static ToolEnvironment Discover()
    {
        string config = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (File.Exists(config))
        {
            var installed = ReadConfig(config);
            if (!File.Exists(Path.Combine(installed.RepositoryRoot, "OsuAiMapper.slnx")))
                throw new InvalidOperationException($"O osu-ai-mapper foi instalado para usar as ferramentas de '{installed.RepositoryRoot}', mas a pasta não está mais lá. Reinstale a partir do repositório.");
            return new ToolEnvironment(installed.RepositoryRoot, installed.Uv ?? "uv");
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OsuAiMapper.slnx")))
                return new ToolEnvironment(dir.FullName);
        }

        throw new InvalidOperationException("The Python tools are only available when running a build inside the osu-ai-mapper repository.");
    }

    public sealed record InstalledConfig(string RepositoryRoot, string? Uv);

    public static InstalledConfig ReadConfig(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return new InstalledConfig(
            root.GetProperty("repositoryRoot").GetString() ?? throw new InvalidDataException($"{path} has no repositoryRoot."),
            root.TryGetProperty("uv", out var uv) ? uv.GetString() : null);
    }

    private static string HashSources(string prefix, params (string Project, string Package)[] parts)
    {
        var files = parts.SelectMany(p =>
                             (Directory.Exists(Path.Combine(p.Project, p.Package)) ? Directory.EnumerateFiles(Path.Combine(p.Project, p.Package), "*.py") : [])
                             .Append(Path.Combine(p.Project, "uv.lock")))
                         .Where(File.Exists)
                         .Order(StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            // Line endings vary between checkouts; they are not part of what the code does.
            hash.AppendData(Encoding.UTF8.GetBytes(File.ReadAllText(file).Replace("\r\n", "\n")));
        }

        return prefix + Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }
}
