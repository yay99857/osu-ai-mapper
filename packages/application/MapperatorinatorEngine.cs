using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OsuAi.BeatmapCore;
using OsuAi.OsuAdapter;

namespace OsuAi.Application;

/// <summary>
/// Mapperatorinator (github.com/OliBomby/Mapperatorinator, MIT), a ~200M-parameter transformer with
/// learned placement, run as an optional engine from a local checkout with its own Python
/// environment. Our timing goes in through a reference .osu (in_context=[TIMING]); the map it
/// writes comes back through our importer, validator and exporter like any other revision.
/// On a CPU it takes about ten minutes per minute of music.
/// </summary>
public static class MapperatorinatorEngine
{
    public const string ModelPrefix = "mapperatorinator:";

    public static bool IsEngine(string? model) => model?.StartsWith(ModelPrefix, StringComparison.Ordinal) == true;

    private static readonly Regex window = new(@"(?<done>\d+)/(?<total>\d+) \[", RegexOptions.Compiled);
    private static readonly Regex remaining = new(@"\[[\d:]+<(?<left>\d+(:\d+){1,2})", RegexOptions.Compiled);

    /// <summary>
    /// The style tags Mapperatorinator v32 was trained with for osu!standard (its tokenizer's
    /// descriptor list, without the mania, taiko and catch ones). Anything else is refused rather
    /// than silently ignored by the model.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownDescriptors = new HashSet<string>(StringComparer.Ordinal)
    {
        "style/messy", "expression/simple", "expression/chaotic", "style/geometric", "style/freeform", "style/grid snap",
        "style/hexgrid", "style/symmetrical", "gimmick/playfield constraint", "expression/old-style revival",
        "expression/repetition", "expression/progression", "expression/high contrast", "expression/improvisation",
        "skillset/tech", "tech/slider tech", "tech/finger control", "sliders/complex sv", "skillset/jumps", "jumps/sharp",
        "jumps/wide", "jumps/linear", "tech/aim control", "streams/flow aim", "streams/bursts", "skillset/streams",
        "streams/spaced streams", "streams/stamina", "streams/cutstreams", "skillset/reading", "reading/visually dense",
        "reading/overlaps", "skillset/precision", "skillset/alt", "style/clean", "sliders/complex slidershapes",
        "style/distance snap", "tech/complex snap", "expression/iNiS-style", "expression/playfield usage",
        "expression/difficulty spike", "style/avant-garde", "sliders/low sv", "sliders/high sv", "gimmick/slider only",
        "gimmick/circle only", "reading/perfect stacks", "gimmick/ninja spinners", "gimmick/mirrored", "style/vocal",
        "tech/flow", "tech/antiflow", "tech/jump", "gimmick/long sliders", "gimmick/spinner-heavy", "tech/wiggles",
        "jumps/squares", "jumps/triangles", "jumps/cross-screen", "jumps/stamina", "jumps/stars", "jumps/back and forth",
        "jumps/freeform", "streams/doubles", "streams/quads", "expression/inspo", "expression/conceptual",
    };
    private static readonly Regex decimal_field = new(@"^-?\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>A path as a quoted Hydra override value, so parentheses, spaces, commas or '=' are not read as grammar.</summary>
    public static string HydraValue(string path) =>
        "'" + path.Replace('\\', '/').Replace("'", @"\'", StringComparison.Ordinal) + "'";

    public const long SotarksId = 4452992;

    private static readonly Lazy<HashSet<long>?> known_mappers = new(LoadKnownMappers);

    /// <summary>
    /// Whether v32 learned this mapper (osu! user id) as a style, from its tokenizer in the Hugging
    /// Face cache; null when the model has not been downloaded. An unknown id is read as "no mapper".
    /// </summary>
    public static bool? KnowsMapper(long userId) => known_mappers.Value?.Contains(userId);

    /// <summary>Where Hugging Face keeps the downloaded v32 weights (they come on the first run).</summary>
    public static string WeightsFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } home
            ? Path.Combine(home, "hub")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface", "hub"),
            "models--OliBomby--Mapperatorinator-v32");

    private static HashSet<long>? LoadKnownMappers()
    {
        string snapshots = Path.Combine(WeightsFolder, "snapshots");
        string? tokenizer = Directory.Exists(snapshots)
            ? Directory.EnumerateFiles(snapshots, "tokenizer.json", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("gamemode=0", StringComparison.Ordinal))
            : null;
        if (tokenizer is null)
            return null;
        try
        {
            using var stream = File.OpenRead(tokenizer);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            return doc.RootElement.GetProperty("mapper_idx").EnumerateObject()
                .Select(p => long.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : -1)
                .Where(id => id >= 0).ToHashSet();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The time tqdm expects the current pass to still take ("[03:54<08:09"), when it says.</summary>
    public static TimeSpan? ParseRemaining(string segment)
    {
        var m = remaining.Match(segment);
        if (!m.Success)
            return null;
        var parts = m.Groups["left"].Value.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 3 ? new TimeSpan(parts[0], parts[1], parts[2]) : new TimeSpan(0, parts[0], parts[1]);
    }

    /// <summary>The inference.py command line for a request, Hydra overrides included.</summary>
    public static IReadOnlyList<string> Arguments(MapperatorinatorRequest request)
    {
        var unknown = request.Descriptors.Where(d => !KnownDescriptors.Contains(d)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException($"Mapperatorinator does not know the style tag(s): {string.Join(", ", unknown)}.");

        var arguments = new List<string>
        {
            "inference.py", "-cn", request.Version, $"beatmap_path={HydraValue(request.ReferenceOsu)}", $"audio_path={HydraValue(request.AudioWav)}",
            $"output_path={HydraValue(request.OutputDir)}", "gamemode=0", $"difficulty={request.Stars.ToString("0.##", CultureInfo.InvariantCulture)}",
            "in_context=[TIMING]", "device=auto", $"seed={request.Seed}", $"year={request.Year}",
        };
        if (request.Span is var (from, to))
        {
            arguments.Add($"start_time={from.ToString(CultureInfo.InvariantCulture)}");
            arguments.Add($"end_time={to.ToString(CultureInfo.InvariantCulture)}");
        }
        if (request.CircleSize is { } cs)
            arguments.Add($"circle_size={cs.ToString("0.#", CultureInfo.InvariantCulture)}");
        if (request.MapperId is { } mapper)
            arguments.Add($"mapper_id={mapper.ToString(CultureInfo.InvariantCulture)}");
        if (request.Descriptors.Count > 0)
            arguments.Add($"descriptors=[{string.Join(',', request.Descriptors.Select(HydraValue))}]");
        // Guidance runs the model twice per step: a style it follows clearly, at about twice the time.
        if (request.StrongStyle && (request.MapperId is not null || request.Descriptors.Count > 0))
            arguments.Add("cfg_scale=2");
        return arguments;
    }

    /// <summary>A tqdm redraw ("34%|███▍ | 11/32 [03:54<08:09, ...]") as windows done and total.</summary>
    public static (int Done, int Total)? ParseProgress(string segment)
    {
        var m = window.Match(segment);
        return m.Success ? (int.Parse(m.Groups["done"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture)) : null;
    }

    /// <summary>
    /// Mapperatorinator's .osu reader wants whole milliseconds and coordinates in [HitObjects]; the
    /// official lazer encoder may write fractions (spinner ends, slider timings).
    /// </summary>
    public static string WholeNumberHitObjects(string osuText)
    {
        var lines = osuText.Replace("\r\n", "\n").Split('\n');
        bool objects = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith('['))
            {
                objects = line.Trim() == "[HitObjects]";
                continue;
            }
            if (!objects || line.Trim().Length == 0)
                continue;
            var parts = line.Split(',');
            int fields = parts.Length > 3 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type) && (type & 8) != 0 ? 6 : 5;
            for (int k = 0; k < Math.Min(fields, parts.Length); k++)
            {
                if (decimal_field.IsMatch(parts[k]))
                    parts[k] = Math.Round(double.Parse(parts[k], CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
            }
            lines[i] = string.Join(',', parts);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Drops objects that go back in time in the order the model wrote them. In a full Beauty And A
    /// Beat it wrote, after 2:16, 23 objects at time 0 ("Invalid slider duration at 0" in its log):
    /// dozens of notes at the very start and 12.09 stars; without them the map rates 4.96 for a 5
    /// target. The official decoder sorts objects by time, so this has to happen on the text.
    /// </summary>
    public static string DropTimeReversedObjects(string osuText, out int dropped)
    {
        var lines = osuText.Replace("\r\n", "\n").Split('\n').ToList();
        int start = lines.FindIndex(l => l.Trim() == "[HitObjects]");
        dropped = 0;
        if (start < 0)
            return osuText;
        var kept = new List<string>(lines.Take(start + 1));
        double last = double.NegativeInfinity;
        foreach (string line in lines.Skip(start + 1))
        {
            var parts = line.Split(',');
            if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
            {
                if (t < last - 1)
                {
                    dropped++;
                    continue;
                }
                last = Math.Max(last, t);
            }
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    private static double TimeOf(string hitObjectLine) =>
        double.Parse(hitObjectLine.Split(',')[2], NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool IsInherited(string timingLine)
    {
        var p = timingLine.Split(',');
        return p.Length > 6 && p[6].Trim() == "0";
    }

    private static Dictionary<string, List<string>> Sections(string osuText)
    {
        var sections = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (string raw in osuText.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('['))
                sections[line.Trim()] = current = [];
            else if (current is not null && line.Length > 0)
                current.Add(line);
        }
        return sections;
    }

    /// <summary>
    /// Stretches of at least <paramref name="minSeconds"/> between objects while the music plays
    /// (mean loudness at least half the song's median), in milliseconds. In the same Beauty And A
    /// Beat the windows that wrote objects at time 0 left 2:16.8-2:45.6 empty over the song at
    /// 1.18x its median loudness.
    /// </summary>
    public static IReadOnlyList<(long StartMs, long EndMs)> EmptyStretches(string osuText, OsuAi.Generation.SongAnalysis analysis, double minSeconds = 3)
    {
        if (!Sections(osuText).TryGetValue("[HitObjects]", out var objects) || objects.Count < 2)
            return [];
        return MapReport.EmptyStretches(objects.Select(TimeOf).Order().ToArray(), analysis, minSeconds);
    }

    /// <summary>
    /// The objects and slider-velocity lines a patch generated strictly inside (fromMs, toMs), put
    /// into the map in place of whatever it had there. Red lines stay the map's.
    /// </summary>
    public static string Splice(string mapText, string patchText, double fromMs, double toMs)
    {
        var map = Sections(mapText);
        var patch = Sections(patchText);
        bool Inside(double t) => t > fromMs && t < toMs;
        var objects = map.GetValueOrDefault("[HitObjects]", []).Where(l => !Inside(TimeOf(l)))
                         .Concat(patch.GetValueOrDefault("[HitObjects]", []).Where(l => Inside(TimeOf(l))))
                         .OrderBy(TimeOf).ToList();
        double TimingTime(string l) => double.Parse(l.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture);
        var timing = map.GetValueOrDefault("[TimingPoints]", []).Where(l => !(IsInherited(l) && Inside(TimingTime(l))))
                        .Concat(patch.GetValueOrDefault("[TimingPoints]", []).Where(l => IsInherited(l) && Inside(TimingTime(l))))
                        .OrderBy(TimingTime).ThenBy(IsInherited).ToList();

        var output = new List<string>();
        string? current = null;
        foreach (string raw in mapText.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith('['))
            {
                current = line.Trim();
                output.Add(line);
                if (current == "[TimingPoints]")
                    output.AddRange(timing);
                else if (current == "[HitObjects]")
                    output.AddRange(objects);
                continue;
            }
            if (current is "[TimingPoints]" or "[HitObjects]" && line.Length > 0)
                continue;
            output.Add(line);
        }
        return string.Join('\n', output);
    }

    /// <summary>Runs inference and returns the .osu text it wrote.</summary>
    public static async Task<string> GenerateAsync(ToolEnvironment tools, MapperatorinatorRequest request, IProgress<PipelineProgress>? progress,
        string label, CancellationToken cancellationToken)
    {
        if (Directory.Exists(request.OutputDir))
            Directory.Delete(request.OutputDir, recursive: true);
        Directory.CreateDirectory(request.OutputDir);
        var start = new ProcessStartInfo(tools.MapperatorinatorPython)
        {
            WorkingDirectory = tools.MapperatorinatorRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in Arguments(request))
            start.ArgumentList.Add(a);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONWARNINGS"] = "ignore";

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start '{tools.MapperatorinatorPython}'.");

        var stdout = ReadSegmentsAsync(process.StandardOutput, s => Report(s, label, progress));
        var stderr = ReadSegmentsAsync(process.StandardError, s => Report(s, label, progress));
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

        string output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        var written = Directory.EnumerateFiles(request.OutputDir, "*.osu").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (process.ExitCode != 0 || written is null)
        {
            string tail = string.Join(Environment.NewLine, output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains('%', StringComparison.Ordinal)).TakeLast(6));
            throw new GenerationFailedException("Mapperatorinator failed: " + tail);
        }
        string text = DropTimeReversedObjects(File.ReadAllText(written), out int dropped);
        if (dropped > 0)
            progress?.Report(new PipelineProgress(PipelineStage.Generating, $"{label}: {dropped} objetos fora de ordem descartados"));
        return text;
    }

    private static void Report(string segment, string label, IProgress<PipelineProgress>? progress)
    {
        if (progress is null)
            return;
        if (ParseProgress(segment) is var (done, total) && total > 0)
            progress.Report(new PipelineProgress(PipelineStage.Generating, $"{label}: {done}/{total} trechos", (double)done / total, ParseRemaining(segment)));
        else if (segment.StartsWith("Precomputing", StringComparison.Ordinal))
            progress.Report(new PipelineProgress(PipelineStage.Generating, $"{label}: ouvindo a música", 0));
        else if (segment.StartsWith("Generated beatmap saved", StringComparison.Ordinal))
            progress.Report(new PipelineProgress(PipelineStage.Generating, $"{label}: mapa pronto", 1));
    }

    /// <summary>Reads a stream as it arrives, splitting on carriage returns and newlines (progress bars redraw with \r).</summary>
    internal static async Task<string> ReadSegmentsAsync(StreamReader reader, Action<string> onSegment)
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
                    if (segment.Length > 0)
                        onSegment(segment.ToString());
                    segment.Clear();
                }
                else
                {
                    segment.Append(c);
                }
            }
        }
        if (segment.Length > 0)
            onSegment(segment.ToString());
        return all.ToString();
    }

    /// <summary>The map Mapperatorinator wrote, as a revision-ready IR: our metadata and audio, its objects.</summary>
    public static BeatmapIr Adopt(string osuText, BeatmapIr reference)
    {
        var imported = OsuImporter.Import(Encoding.UTF8.GetBytes(osuText)).Beatmap;
        return imported with
        {
            Metadata = reference.Metadata,
            Assets = reference.Assets,
            Settings = imported.Settings with { PreviewTimeUs = reference.Settings.PreviewTimeUs },
        };
    }
}

/// <summary>One inference run. <see cref="Span"/> maps only that stretch of the song (milliseconds).</summary>
public sealed record MapperatorinatorRequest(string Version, string ReferenceOsu, string AudioWav, string OutputDir, double Stars, int Seed)
{
    public (long StartMs, long EndMs)? Span { get; init; }
    public IReadOnlyList<string> Descriptors { get; init; } = [];
    public long? MapperId { get; init; }
    public bool StrongStyle { get; init; }
    public int Year { get; init; } = 2023;
    public double? CircleSize { get; init; }
}
