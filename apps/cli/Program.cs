using System.Globalization;
using System.Text;
using OsuAi.Application;
using OsuAi.ProjectStore;

namespace OsuAi.Cli;

/// <summary>
/// Command-line front end over <see cref="MappingService"/>. It parses flags and prints results;
/// every operation lives in the application layer, shared with the desktop app.
/// </summary>
public static class Program
{
    private const string usage = """
        osu-ai: turn a local song into an editable osu!standard draft.

          generate <audio> <project-dir> [options]   create (or reuse) a project, generate, export
          analyse <project-dir>                      analyse only (cached by audio, analyzer and timing)
          regenerate <project-dir> [options]         generate a new revision with changed settings
          preview <project-dir> [options]            Mapperatorinator on the loudest 30 s only, to judge stars and style
          mapset <project-dir> --difficulties "Hard=3.3,Insane=4.6" [options]
                                                     generate several difficulties into one package
          check <project-dir>                        what the map checks find in the active revision
          package <project-dir> [--add <rev>] [--remove <rev>]
                                                     choose the revisions the .osz carries (one per difficulty)
          metadata <project-dir> [metadata options]  change metadata; objects are kept, nothing regenerated
          export <project-dir> [--to <dir>]          package the revisions of the package as .osz
          info <project-dir>                         show the project and its revisions

        metadata options:
          --title <t> --artist <a> --creator <mapper> --difficulty <name>
          --title-unicode <t> --artist-unicode <a> --source <s> --tags "<tag tag ...>"

        generation options:
          --stars <n>  --seed <n>  --ar <n> --od <n> --cs <n> --hp <n>
          --bpm <n> [--offset-ms <n>]   correct the detected timing (offset is acoustic, in ms)
          --model <id>                  mapperatorinator:v32 (the default for new projects when installed),
                                        a Pulso or B2 model file for a quick draft, or "rules" for the B1 rules
          --descriptors "<tag,tag>"     Mapperatorinator style tags, e.g. "skillset/jumps,style/clean"; "" clears
          --mapper <id|sotarks|none>    imitate a mapper Mapperatorinator learned (osu! user id)
          --strong-style <true|false>   follow the style harder (classifier-free guidance, about twice the time)
          --year <2007-2024>            the year whose mapping style to imitate (default 2023)
          --style <auto|jumps|streams>  mapping style, for Pulso models conditioned on one
          --to <dir>                    also copy the exported .osz there
          --analysis-cache <dir>        share analyses between projects (content-addressed)

        Nothing is uploaded. Ctrl+C cancels the running step and leaves the project as it was.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
            Console.Error.WriteLine("cancelling...");
        };

        try
        {
            return args switch
            {
                ["generate", var audio, var dir, .. var rest] => await Generate(audio, dir, Flags(rest), cancel.Token),
                ["analyse", var dir, .. var rest] => await Analyse(dir, Flags(rest), cancel.Token),
                ["regenerate", var dir, .. var rest] => await Regenerate(dir, Flags(rest), cancel.Token),
                ["preview", var dir, .. var rest] => await Regenerate(dir, Flags(rest), cancel.Token, preview: true),
                ["mapset", var dir, .. var rest] => await Mapset(dir, Flags(rest), cancel.Token),
                ["check", var dir] => Check(dir),
                ["package", var dir, .. var rest] => Package(dir, Flags(rest)),
                ["metadata", var dir, .. var rest] => Metadata(dir, Flags(rest)),
                ["export", var dir, .. var rest] => Export(dir, Flags(rest)),
                ["info", var dir] => Info(dir),
                _ => Usage(),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled; the project keeps its previous state");
            return 130;
        }
        catch (Exception e) when (e is InvalidMetadataException or AnalysisFailedException or GenerationFailedException or InvalidOperationException
                                       or ArgumentException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static async Task<int> Generate(string audio, string dir, Dictionary<string, string> flags, CancellationToken token)
    {
        var service = Service(flags);
        Project project;

        if (Project.Exists(dir))
        {
            project = Project.Open(dir);

            if (project.Manifest.Original.Sha256 != Project.Sha256Of(audio))
                throw new InvalidOperationException($"'{dir}' is a project for a different audio file; use another folder.");

            // The revision about to be generated carries the new metadata; no separate metadata revision.
            service.SetMetadata(project, MetadataFrom(flags, project.Manifest.Metadata));
        }
        else
        {
            string stem = Path.GetFileNameWithoutExtension(audio);
            var metadata = MetadataFrom(flags, new ProjectMetadata { Title = stem, Artist = "Unknown", Creator = "osu-ai-mapper", DifficultyName = "AI" });
            var defaults = new ProjectSettings { RhythmModel = ToolEnvironment.Discover().MapperatorinatorAvailable ? MapperatorinatorEngine.ModelPrefix + "v32" : null };
            project = service.CreateProject(dir, audio, metadata, SettingsFrom(flags, defaults));
        }

        service.UpdateSettings(project, s => SettingsFrom(flags, s));
        return await GenerateAndExport(service, project, flags, token);
    }

    private static async Task<int> Regenerate(string dir, Dictionary<string, string> flags, CancellationToken token, bool preview = false)
    {
        var service = Service(flags);
        var project = Project.Open(dir);
        service.SetMetadata(project, MetadataFrom(flags, project.Manifest.Metadata));
        service.UpdateSettings(project, s => SettingsFrom(flags, s));
        return await GenerateAndExport(service, project, flags, token, preview);
    }

    private static async Task<int> Mapset(string dir, Dictionary<string, string> flags, CancellationToken token)
    {
        var service = Service(flags);
        var project = Project.Open(dir);
        service.SetMetadata(project, MetadataFrom(flags, project.Manifest.Metadata));
        service.UpdateSettings(project, s => SettingsFrom(flags, s));
        var difficulties = (flags.GetValueOrDefault("difficulties") ?? throw new ArgumentException("mapset needs --difficulties \"Name=stars,...\"."))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split('=') is [var name, var stars]
                ? new MappingService.MapsetDifficulty(name.Trim(), double.Parse(stars, CultureInfo.InvariantCulture))
                : throw new ArgumentException($"'{d}' is not Name=stars."))
            .ToArray();

        var outcomes = await service.GenerateMapsetAsync(project, difficulties, ProgressPrinter(), token);
        foreach (var o in outcomes)
            Console.WriteLine($"{o.Revision.Id}  [{o.Revision.Metadata.DifficultyName}]  {o.Revision.Generation.ActualStars:0.00} stars for {o.Revision.Generation.TargetStars:0.##}");
        Console.WriteLine($"package       {service.Export(project, flags.GetValueOrDefault("to"))}");
        return 0;
    }

    private static int Check(string dir)
    {
        var project = Project.Open(dir);
        var active = project.ActiveRevision ?? throw new InvalidOperationException("Nothing generated yet.");
        var issues = MappingService.ReportOf(project, active);
        PrintIssues(issues);
        return issues.Any(i => i.Severity == IssueSeverity.Problem) ? 3 : 0;
    }

    private static void PrintIssues(IReadOnlyList<MapIssue> issues)
    {
        if (issues.Count == 0)
            Console.WriteLine("checks        nothing found");
        foreach (var i in issues)
        {
            string when = i.EndMs is { } end ? $"{MapReport.Clock(i.TimeMs)}-{MapReport.Clock(end)}" : MapReport.Clock(i.TimeMs);
            Console.WriteLine($"{i.Severity.ToString().ToLowerInvariant(),-8} {when,-10} {i.Message}");
        }
    }

    private static int Package(string dir, Dictionary<string, string> flags)
    {
        var service = Service(flags);
        var project = Project.Open(dir);
        if (flags.GetValueOrDefault("add") is { } add)
            service.SetInPackage(project, add, include: true);
        if (flags.GetValueOrDefault("remove") is { } remove)
            service.SetInPackage(project, remove, include: false);
        foreach (var r in MappingService.PackageOf(project))
            Console.WriteLine($"{r.Id}  [{r.Metadata.DifficultyName}]  {r.Generation.ActualStars:0.00} stars");
        return 0;
    }

    private static async Task<int> GenerateAndExport(MappingService service, Project project, Dictionary<string, string> flags, CancellationToken token, bool preview = false)
    {
        var outcome = await service.GenerateAsync(project, ProgressPrinter(), token, preview);
        var g = outcome.Revision.Generation;

        foreach (var warning in outcome.Warnings)
            Console.WriteLine($"  {warning}");

        string package = service.Export(project, flags.GetValueOrDefault("to"));

        Console.WriteLine();
        Console.WriteLine($"revision      {outcome.Revision.Id} ({g.Generator}{(g.RhythmModelSha256 is { } sha ? $", model {sha[..12]}" : "")}, seed {g.Seed})");
        Console.WriteLine($"objects       {outcome.Beatmap.Objects.Count}, {outcome.Beatmap.Breaks.Count} breaks");
        Console.WriteLine($"stars         {g.ActualStars:0.00} for a target of {g.TargetStars:0.##} ({(g.TargetReached ? "reached" : "TARGET NOT REACHED")}; {g.DifficultyProfile})"
                          + (g.RequestedStars is { } asked ? $"; asked the model for {asked:0.##} to compensate" : ""));
        if (g.PreviewStartMs is { } from && g.PreviewEndMs is { } to)
            Console.WriteLine($"preview       {MapReport.Clock(from)}-{MapReport.Clock(to)} only");
        if (outcome.Candidates.Count > 0)
            Console.WriteLine($"candidates    {string.Join("  ", outcome.Candidates.Select(c => $"{c.SpacingFactor:0.###}→{c.Stars:0.00}"))}");
        PrintIssues(MappingService.ReportOf(project, outcome.Revision));
        Console.WriteLine($"analysis      {project.Manifest.Analysis!.Key}");
        Console.WriteLine($"package       {package}");
        return 0;
    }

    private static async Task<int> Analyse(string dir, Dictionary<string, string> flags, CancellationToken token)
    {
        var service = Service(flags);
        var project = Project.Open(dir);
        service.UpdateSettings(project, s => SettingsFrom(flags, s));
        var outcome = await service.AnalyseAsync(project, ProgressPrinter(), token);
        var t = outcome.Analysis.Timing;
        Console.WriteLine($"{(outcome.Reused ? "reused" : "analysed")} {outcome.Ref.Key}: {t.Source} {t.Bpm:0.###} BPM, offset {t.OffsetUs / 1000.0:0.#} ms (acoustic), confidence {t.Confidence:0.00}");
        return 0;
    }

    private static int Metadata(string dir, Dictionary<string, string> flags)
    {
        var service = Service(flags);
        var project = Project.Open(dir);
        var revision = ApplyMetadata(service, project, flags);
        Console.WriteLine(revision is null ? "metadata saved (no revision yet, or nothing changed)" : $"metadata saved as {revision.Id}; objects and timing unchanged");
        return 0;
    }

    private static int Export(string dir, Dictionary<string, string> flags)
    {
        var project = Project.Open(dir);
        Console.WriteLine(Service(flags).Export(project, flags.GetValueOrDefault("to")));
        return 0;
    }

    private static int Info(string dir)
    {
        var m = Project.Open(dir).Manifest;
        Console.WriteLine($"{m.Metadata.Artist} - {m.Metadata.Title} [{m.Metadata.DifficultyName}] by {m.Metadata.Creator}");
        Console.WriteLine($"audio         {m.Original.OriginalName} ({m.Original.Sha256[..12]})");
        Console.WriteLine($"analysis      {m.Analysis?.Key ?? "none"}");
        var s = m.Settings;
        string model = s.RhythmModel is null ? "B1 rules" : MapperatorinatorEngine.IsEngine(s.RhythmModel) ? s.RhythmModel : Path.GetFileName(s.RhythmModel);
        Console.WriteLine($"target        {s.TargetStars:0.##} stars, {model}");
        if (MapperatorinatorEngine.IsEngine(s.RhythmModel))
            Console.WriteLine($"style         {(s.Descriptors.Count > 0 ? string.Join(", ", s.Descriptors) : "model decides")}; mapper {s.MapperId?.ToString(CultureInfo.InvariantCulture) ?? "none"}; year {s.Year}{(s.StrongStyle ? "; strong" : "")}");
        else
            Console.WriteLine($"style         {s.Style ?? "auto"}");

        var project = Project.Open(dir);
        var package = MappingService.PackageOf(project).Select(r => r.Id).ToHashSet();
        foreach (var r in m.Revisions)
        {
            string active = r.Id == m.ActiveRevision ? "*" : package.Contains(r.Id) ? "+" : " ";
            string kind = r.Generation.IsPreview ? "preview" : r.Kind.ToString().ToLowerInvariant();
            Console.WriteLine($"{active} {r.Id}  {kind,-9} {r.Generation.Generator}  {r.Generation.ActualStars:0.00} stars  {r.CreatedAt:yyyy-MM-dd HH:mm}  [{r.Metadata.DifficultyName}]");
        }
        Console.WriteLine("(* active, + also in the package)");

        return 0;
    }

    private static RevisionRecord? ApplyMetadata(MappingService service, Project project, Dictionary<string, string> flags)
    {
        var current = project.Manifest.Metadata;
        var next = MetadataFrom(flags, current);
        return SameMetadata(next, current) ? null : service.UpdateMetadata(project, next);
    }

    private static bool SameMetadata(ProjectMetadata a, ProjectMetadata b) =>
        a with { Tags = [] } == b with { Tags = [] } && a.Tags.SequenceEqual(b.Tags);

    private static ProjectMetadata MetadataFrom(Dictionary<string, string> f, ProjectMetadata current) => current with
    {
        Title = f.GetValueOrDefault("title") ?? current.Title,
        TitleUnicode = f.GetValueOrDefault("title-unicode") ?? current.TitleUnicode,
        Artist = f.GetValueOrDefault("artist") ?? current.Artist,
        ArtistUnicode = f.GetValueOrDefault("artist-unicode") ?? current.ArtistUnicode,
        Creator = f.GetValueOrDefault("creator") ?? current.Creator,
        DifficultyName = f.GetValueOrDefault("difficulty") ?? current.DifficultyName,
        Source = f.GetValueOrDefault("source") ?? current.Source,
        Tags = f.TryGetValue("tags", out string? tags) ? tags.Split(' ', StringSplitOptions.RemoveEmptyEntries) : current.Tags,
    };

    private static ProjectSettings SettingsFrom(Dictionary<string, string> f, ProjectSettings current)
    {
        double? Number(string key) => f.TryGetValue(key, out string? v) ? double.Parse(v, CultureInfo.InvariantCulture) : null;

        var timing = current.Timing;
        if (Number("bpm") is { } bpm)
            timing = new TimingCorrection(bpm, Number("offset-ms"));
        else if (f.ContainsKey("offset-ms"))
            throw new ArgumentException("--offset-ms needs --bpm.");

        return current with
        {
            TargetStars = Number("stars") ?? current.TargetStars,
            Seed = f.TryGetValue("seed", out string? seed) ? int.Parse(seed, CultureInfo.InvariantCulture) : current.Seed,
            Ar = Number("ar") ?? current.Ar,
            Od = Number("od") ?? current.Od,
            Cs = Number("cs") ?? current.Cs,
            Hp = Number("hp") ?? current.Hp,
            Timing = timing,
            RhythmModel = (f.GetValueOrDefault("model") ?? f.GetValueOrDefault("rhythm-model")) is { } model
                ? model == "rules" ? null : MapperatorinatorEngine.IsEngine(model) ? model : Path.GetFullPath(model)
                : current.RhythmModel,
            Style = f.TryGetValue("style", out string? style) ? StyleOf(style) : current.Style,
            Descriptors = f.TryGetValue("descriptors", out string? tags) ? tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : current.Descriptors,
            MapperId = f.TryGetValue("mapper", out string? mapper) ? MapperOf(mapper) : current.MapperId,
            StrongStyle = f.TryGetValue("strong-style", out string? strong) ? bool.Parse(strong) : current.StrongStyle,
            Year = f.TryGetValue("year", out string? year) ? int.Parse(year, CultureInfo.InvariantCulture) : current.Year,
        };
    }

    private static long? MapperOf(string value) => value.ToLowerInvariant() switch
    {
        "none" or "" => null,
        "sotarks" => MapperatorinatorEngine.SotarksId,
        _ => long.Parse(value, CultureInfo.InvariantCulture) is var id && MapperatorinatorEngine.KnowsMapper(id) != false
            ? id
            : throw new ArgumentException($"Mapperatorinator v32 did not learn mapper {value}; its style would be ignored."),
    };

    private static string? StyleOf(string value)
    {
        if (value == "auto")
            return null;
        if (!ProjectSettings.Styles.Contains(value))
            throw new ArgumentException($"--style must be auto, {string.Join(" or ", ProjectSettings.Styles)}.");
        return value;
    }

    private static MappingService Service(Dictionary<string, string> flags) =>
        new(ToolEnvironment.Discover(), flags.TryGetValue("analysis-cache", out string? cache) ? Path.GetFullPath(cache) : null);

    private static IProgress<PipelineProgress> ProgressPrinter() => new StagePrinter();

    /// <summary>
    /// Prints each stage once, in order; a step that reports how far it is (Mapperatorinator's
    /// windows) redraws one line with its percentage and time left. Progress&lt;T&gt; would post to
    /// the thread pool and could print stages out of order in a console app.
    /// </summary>
    private sealed class StagePrinter : IProgress<PipelineProgress>
    {
        private readonly Lock gate = new();
        private string? last;
        private bool redrawing;

        public void Report(PipelineProgress value)
        {
            lock (gate)
            {
                if (value.Fraction is { } fraction)
                {
                    string left = value.Remaining is { } r ? $", {r.TotalMinutes:0} min left" : "";
                    string line = $"  {value.Message} ({fraction:0%}{left})";
                    Console.Write(Console.IsOutputRedirected ? line + Environment.NewLine : $"\r{line,-90}");
                    redrawing = !Console.IsOutputRedirected;
                    last = value.Message;
                    return;
                }

                if (value.Message == last)
                    return;
                if (redrawing)
                    Console.WriteLine();
                redrawing = false;
                last = value.Message;
                Console.WriteLine($"  {value.Message}");
            }
        }
    }

    private static Dictionary<string, string> Flags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");

            flags[args[i][2..]] = args[i + 1];
        }

        return flags;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(usage);
        return 2;
    }
}
