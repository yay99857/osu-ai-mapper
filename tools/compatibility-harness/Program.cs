using System.Globalization;
using System.Text;
using OsuAi.BeatmapCore;
using OsuAi.OsuAdapter;

namespace OsuAi.CompatibilityHarness;

public static class Program
{
    private const string usage = """
        osu-ai-harness: headless checks against the pinned official osu! engine.

          roundtrip <file.osu|dir>...       parse -> IR -> encode -> parse, report semantic losses and SR drift
          import <file.osu> [out.json]       write the BeatmapIR JSON of a .osu file
          export <ir.json> <out.osu>         encode a BeatmapIR JSON document with the official encoder
          difficulty <file.osu>              official difficulty attributes, nomod
          pack <file.osu> <audio> <out.osz>  package a .osu and its audio for import into lazer
          snapshot <dir> <out.json>          record official difficulty of every .osu in dir (golden file)
          demo <out-dir>                     synthesise a click track and a B0 map on it, packed as .osz
          engine                             print the pinned engine version

        Exit code 0 when every check passes, 1 when a check fails, 2 on usage errors.
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length == 0)
            return Usage();

        try
        {
            return args[0] switch
            {
                "roundtrip" when args.Length >= 2 => RoundTripCommand(args[1..]),
                "import" when args.Length is 2 or 3 => ImportCommand(args[1], args.ElementAtOrDefault(2)),
                "export" when args.Length == 3 => ExportCommand(args[1], args[2]),
                "difficulty" when args.Length == 2 => DifficultyCommand(args[1]),
                "pack" when args.Length == 4 => PackCommand(args[1], args[2], args[3]),
                "snapshot" when args.Length == 3 => SnapshotCommand(args[1], args[2]),
                "demo" when args.Length == 2 => DemoCommand(args[1]),
                "engine" => Print(OfficialEngine.Version),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is IOException or InvalidBeatmapException or NotSupportedException or UnsafePackageException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int RoundTripCommand(string[] inputs)
    {
        var files = inputs
                    .SelectMany(i => Directory.Exists(i)
                        ? Directory.EnumerateFiles(i, "*.osu", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                        : (IEnumerable<string>)[i])
                    .ToArray();

        int failures = 0;
        Console.WriteLine($"engine: {OfficialEngine.Version}");

        foreach (string file in files)
        {
            var report = RoundTrip.Run(File.ReadAllBytes(file));
            string name = Path.GetFileName(file);
            double drift = report.RoundTripDifficulty.StarRating - report.OriginalDifficulty.StarRating;

            int declared = report.DeclaredLosses.Count();

            Console.WriteLine(
                $"{(report.IsLossless ? "PASS" : "FAIL")}  {name,-26} SR {report.OriginalDifficulty.StarRating,7:0.0000}  drift {drift,9:0.###e+0}  diffs {report.Differences.Count,3}  declared losses {declared,2}  stable {(report.EncoderIsStable ? "yes" : "no")}");

            foreach (var d in report.ImportDiagnostics.Where(d => d.Severity != Severity.Info))
                Console.WriteLine($"      {d}");

            foreach (var d in report.Differences.Take(20))
                Console.WriteLine($"      {d}");

            if (report.Differences.Count > 20)
                Console.WriteLine($"      ... {report.Differences.Count - 20} more");

            if (!report.IsLossless)
                failures++;
        }

        Console.WriteLine($"{files.Length - failures}/{files.Length} lossless");
        return failures == 0 ? 0 : 1;
    }

    private static int ImportCommand(string file, string? output)
    {
        var result = OsuImporter.Import(File.ReadAllBytes(file));

        foreach (var d in result.Diagnostics)
            Console.Error.WriteLine(d);

        string json = IrJson.Serialize(result.Beatmap);

        if (output is null)
            Console.WriteLine(json);
        else
            File.WriteAllText(output, json);

        return result.Diagnostics.HasErrors() ? 1 : 0;
    }

    private static int ExportCommand(string irFile, string output)
    {
        var ir = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(irFile));
        File.WriteAllText(output, OsuExporter.ToOsuText(ir));
        return 0;
    }

    private static int DifficultyCommand(string file)
    {
        var report = DifficultyService.CalculateFromOsuText(File.ReadAllText(file));
        Console.WriteLine(IrJson.Serialize(report));
        return 0;
    }

    private static int PackCommand(string osuFile, string audioFile, string output)
    {
        var result = OszPackager.Pack(File.ReadAllBytes(osuFile), audioFile, output);
        Console.WriteLine($"wrote {output} ({result.Entries.Count} entries, {result.ByteSize} bytes)");

        foreach (var entry in result.Entries)
            Console.WriteLine($"  {entry}");

        return 0;
    }

    private static int SnapshotCommand(string directory, string output)
    {
        var snapshot = new SortedDictionary<string, DifficultyReport>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(directory, "*.osu"))
            snapshot[Path.GetFileName(file)] = DifficultyService.CalculateFromOsuText(File.ReadAllText(file));

        File.WriteAllText(output, IrJson.Serialize(snapshot) + Environment.NewLine);
        Console.WriteLine($"wrote {snapshot.Count} entries for {OfficialEngine.Version} to {output}");
        return 0;
    }

    private static int DemoCommand(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var map = DemoMap.Create();
        string audio = Path.Combine(outputDirectory, map.Assets.Audio.RelativePath);
        ClickTrack.WriteWav(audio, map.Timing, DemoMap.DurationUs);

        string osuText = OsuExporter.ToOsuText(map);
        string osuFile = Path.Combine(outputDirectory, PackageNames.OsuFileName(map.Metadata));
        File.WriteAllText(osuFile, osuText);
        File.WriteAllText(Path.Combine(outputDirectory, "beatmap-ir.json"), IrJson.Serialize(map));

        string osz = Path.Combine(outputDirectory, "osu-ai-mapper-demo.osz");
        var packed = OszPackager.Pack(File.ReadAllBytes(osuFile), audio, osz);
        var difficulty = DifficultyService.Calculate(map);

        Console.WriteLine($"wrote {osz} ({packed.ByteSize} bytes), {map.Objects.Count} objects, {difficulty.StarRating:0.00} stars ({difficulty.Profile})");
        return 0;
    }

    private static int Print(string text)
    {
        Console.WriteLine(text);
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(usage);
        return 2;
    }
}
