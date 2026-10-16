using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OsuAi.Application;

/// <summary>An installed rhythm model and what its card (model.card.json) says about it.</summary>
public sealed record ModelInfo(
    string Name,
    string Path,
    string Features,
    bool NeedsStems,
    IReadOnlyList<string> Styles,
    string Summary,
    long SizeBytes,
    DateTime Modified,
    bool HasBackup,
    bool CanLearn);

/// <summary>One micro-training round run by osu-ai-learn (artifacts/models/learn/STAMP/report.json).</summary>
public sealed record LearnRound(string Stamp, string Focus, int Maps, double ChangePercent, string Comment, string CandidatePath);

/// <summary>An unattended job's status file (artifacts/overnight/status*.json).</summary>
public sealed record BackgroundJob(string Name, string Stage, string Detail, DateTime? Updated);

public sealed record LearnOptions(string Model, string Focus, int Maps, int Rounds, bool Install);

public sealed record LearnProgress(double Percent, string Stage);

public sealed record LearnResult(bool Succeeded, double? ChangePercent, string Comment, bool Installed, string Output);

/// <summary>
/// What the model manager window reads and does: lists installed models with their cards, the
/// history of learning rounds and the unattended jobs, restores a model's previous version, and
/// runs osu-ai-learn with live progress.
/// </summary>
public sealed class ModelAdmin(ToolEnvironment tools)
{
    public string ModelsFolder => Path.Combine(tools.RepositoryRoot, "artifacts", "models", "pulso");

    /// <summary>Mapperatorinator first when installed (the main engine), then the local Pulso and B2 models.</summary>
    public IReadOnlyList<ModelInfo> Models()
    {
        var engines = tools.MapperatorinatorAvailable ? new[] { DescribeMapperatorinator() } : [];
        if (!Directory.Exists(ModelsFolder))
            return engines;

        return engines.Concat(Directory.EnumerateFiles(ModelsFolder)
                        .Where(f => f.EndsWith(".joblib", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pt", StringComparison.OrdinalIgnoreCase))
                        .Select(Describe)
                        .OrderBy(m => m.Name, StringComparer.CurrentCulture))
                      .ToList();
    }

    /// <summary>
    /// The engine as the manager shows it: where it runs from and whether its weights are here yet.
    /// It is not trained locally, so it has no learning rounds or backups.
    /// </summary>
    private ModelInfo DescribeMapperatorinator()
    {
        string blobs = Path.Combine(MapperatorinatorEngine.WeightsFolder, "blobs");
        var files = Directory.Exists(blobs) ? new DirectoryInfo(blobs).GetFiles() : [];
        long size = files.Sum(f => f.Length);
        string weights = files.Length == 0
            ? "Pesos ainda não baixados: a primeira geração baixa o modelo do Hugging Face."
            : $"Pesos em {MapperatorinatorEngine.WeightsFolder}.";
        return new ModelInfo("Mapperatorinator v32", MapperatorinatorEngine.ModelPrefix + "v32",
            "motor externo, ~200M parâmetros, estilo por tags e mapper", NeedsStems: false, Styles: [],
            $"Roda de {tools.MapperatorinatorRoot}. {weights}",
            size, files.Length == 0 ? DateTime.MinValue : files.Max(f => f.LastWriteTime), HasBackup: false, CanLearn: false);
    }

    public static ModelInfo Describe(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path), features = "", summary = "";
        bool stems = false;
        var styles = new List<string>();
        string card = System.IO.Path.ChangeExtension(path, ".card.json");

        if (File.Exists(card))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(card));
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } text)
                    name = text;
                if (root.TryGetProperty("features", out var f))
                    features = f.GetString() ?? "";
                stems = root.TryGetProperty("needsStems", out var s) && s.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("styles", out var st) && st.ValueKind == JsonValueKind.Array)
                    styles.AddRange(st.EnumerateArray().Select(e => e.GetString() ?? "").Where(e => e.Length > 0));
                summary = Summarise(root);
            }
            catch (JsonException)
            {
                summary = "Ficha ilegível.";
            }
        }

        var file = new FileInfo(path);
        return new ModelInfo(name, path, features, stems, styles, summary, file.Length, file.LastWriteTime,
                             File.Exists(path + ".bak"), path.EndsWith(".joblib", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The validation or held-out numbers a card records, in one line.</summary>
    private static string Summarise(JsonElement card)
    {
        foreach (string block in new[] { "validation", "heldOut" })
        {
            if (!card.TryGetProperty(block, out var v) || v.ValueKind != JsonValueKind.Object)
                continue;
            var parts = new List<string>();
            if (v.TryGetProperty("clickF1", out var f1) && f1.ValueKind == JsonValueKind.Number)
                parts.Add($"F1 {f1.GetDouble():0.000}");
            if (v.TryGetProperty("gapHistogramL1", out var gap) && gap.ValueKind == JsonValueKind.Number)
                parts.Add($"ritmo (erro) {gap.GetDouble():0.000}");
            if (v.TryGetProperty("sliderShareMeanAbsError", out var sl) && sl.ValueKind == JsonValueKind.Number)
                parts.Add($"sliders (erro) {sl.GetDouble():0.000}");
            if (v.TryGetProperty("maps", out var maps) && maps.ValueKind == JsonValueKind.Number)
                parts.Add($"{maps.GetInt32()} mapas");
            if (parts.Count > 0)
                return (block == "validation" ? "Validação: " : "Mapas guardados: ") + string.Join(" · ", parts);
        }

        return card.TryGetProperty("note", out var note) ? note.GetString() ?? "" : "";
    }

    public IReadOnlyList<LearnRound> History()
    {
        string folder = Path.Combine(tools.RepositoryRoot, "artifacts", "models", "learn");
        if (!Directory.Exists(folder))
            return [];

        var rounds = new List<LearnRound>();
        foreach (string dir in Directory.EnumerateDirectories(folder))
        {
            string report = Path.Combine(dir, "report.json");
            if (!File.Exists(report))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(report));
                var r = doc.RootElement;
                rounds.Add(new LearnRound(Path.GetFileName(dir), r.GetProperty("focus").GetString() ?? "",
                                          r.TryGetProperty("maps", out var m) ? m.GetArrayLength() : 0,
                                          r.GetProperty("change").GetDouble(), r.GetProperty("comment").GetString() ?? "",
                                          Path.Combine(dir, "candidate.joblib")));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // A round stopped half way leaves no usable report; it is simply not listed.
            }
        }

        return rounds.OrderByDescending(r => r.Stamp, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<BackgroundJob> Jobs()
    {
        string folder = Path.Combine(tools.RepositoryRoot, "artifacts", "overnight");
        if (!Directory.Exists(folder))
            return [];

        var jobs = new List<BackgroundJob>();
        foreach (string file in Directory.EnumerateFiles(folder, "status*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                DateTime? updated = r.TryGetProperty("updated", out var u) && DateTime.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
                string name = Path.GetFileNameWithoutExtension(file) == "status" ? "Pulso v5 (completo)" : "Pulso v5 (parcial)";
                jobs.Add(new BackgroundJob(name, r.GetProperty("stage").GetString() ?? "", r.GetProperty("detail").GetString() ?? "", updated));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException)
            {
                // Written while we read it; the next refresh will show it.
            }
        }

        return jobs;
    }

    /// <summary>Puts the previous version back; the replaced one is kept as model.undone.</summary>
    public static void RestoreBackup(string path)
    {
        string backup = path + ".bak";
        if (!File.Exists(backup))
            throw new FileNotFoundException("Não há versão anterior desse modelo.", backup);
        File.Copy(path, path + ".undone", overwrite: true);
        File.Copy(backup, path, overwrite: true);
        File.Delete(backup);
    }

    private static readonly Regex progress_line = new(@"\]\s+(?<pct>\d+(?:[.,]\d+)?)%\s+(?<stage>.+?)\s{2,}", RegexOptions.Compiled);
    private static readonly Regex change_line = new(@"Melhoria:\s*(?<v>[+-]?\d+(?:[.,]\d+)?)%", RegexOptions.Compiled);

    /// <summary>Reads one progress redraw of osu-ai-learn ("[###---]  45.3%  stage   ...").</summary>
    public static LearnProgress? ParseProgress(string segment)
    {
        var m = progress_line.Match(segment);
        return m.Success ? new LearnProgress(double.Parse(m.Groups["pct"].Value.Replace(',', '.'), CultureInfo.InvariantCulture), m.Groups["stage"].Value.Trim()) : null;
    }

    public static LearnResult ParseResult(int exitCode, string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var change = change_line.Match(output);
        double? value = change.Success ? double.Parse(change.Groups["v"].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : null;
        int at = Array.FindIndex(lines, l => l.StartsWith("Melhoria:", StringComparison.Ordinal));
        string comment = at >= 0 && at + 1 < lines.Length ? lines[at + 1] : "";
        bool installed = lines.Any(l => l.StartsWith("Instalado em", StringComparison.Ordinal));
        return new LearnResult(exitCode == 0 && value is not null, value, comment, installed, output);
    }

    /// <summary>Runs osu-ai-learn from the repository, reporting each progress redraw.</summary>
    public async Task<LearnResult> LearnAsync(LearnOptions options, IProgress<LearnProgress>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tools.Uv)
        {
            WorkingDirectory = tools.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in new[] { "run", "--project", tools.RhythmProject, "osu-ai-learn", "--model", options.Model, "--focus", options.Focus,
                                     "--maps", options.Maps.ToString(CultureInfo.InvariantCulture), "--rounds", options.Rounds.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(a);
        if (options.Install)
            start.ArgumentList.Add("--install");
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONWARNINGS"] = "ignore";

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start '{tools.Uv}'.");

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = ReadProgressAsync(process.StandardError, progress);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }

        string output = await stdout.ConfigureAwait(false);
        string errors = await stderr.ConfigureAwait(false);
        var result = ParseResult(process.ExitCode, output);
        return result.Succeeded ? result : result with { Output = output + Environment.NewLine + LastLines(errors) };
    }

    /// <summary>The progress bar redraws with carriage returns; each redraw is reported as it arrives.</summary>
    private static async Task<string> ReadProgressAsync(StreamReader reader, IProgress<LearnProgress>? progress)
    {
        var all = new StringBuilder();
        var segment = new StringBuilder();
        var buffer = new char[256];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                char c = buffer[i];
                all.Append(c);
                if (c is '\r' or '\n')
                {
                    if (ParseProgress(segment.ToString()) is { } p)
                        progress?.Report(p);
                    segment.Clear();
                }
                else
                {
                    segment.Append(c);
                }
            }
        }

        return all.ToString();
    }

    private static string LastLines(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains("Warn", StringComparison.Ordinal)).TakeLast(6));
}
