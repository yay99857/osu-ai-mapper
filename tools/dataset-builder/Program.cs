using System.Globalization;
using System.Text;

namespace OsuAi.DatasetBuilder;

public static class Program
{
    private const string usage = """
        osu-ai-dataset: build a local, audited dataset from beatmaps you provide.

          rights add <dataset> <id> <evaluation|training|distribution> <source description> [note]
          ingest <dataset> <source> <rights-id>   .osz files and beatmap folders under <source>
          split <dataset>                          group families; freeze or extend the split
          audit <dataset>                          integrity, rights and leakage checks
          report <dataset>                         counts and distributions (also written to reports/)

        The dataset directory holds third-party material: keep it outside git (artifacts/ is ignored).
        Nothing here reads the osu! client's own storage; export the beatmaps you want to use.
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            return args switch
            {
                ["rights", "add", var dataset, var id, var scope, var source, .. var rest] => AddRights(dataset, id, scope, source, rest.FirstOrDefault()),
                ["ingest", var dataset, var source, var rightsId] => Ingest(dataset, source, rightsId),
                ["split", var dataset] => Split(dataset),
                ["audit", var dataset] => RunAudit(dataset),
                ["report", var dataset] => WriteReport(dataset),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int AddRights(string dataset, string id, string scope, string source, string? note)
    {
        var store = new DatasetStore(dataset);
        store.AddRights(new RightsGrant
        {
            Id = id,
            Scope = Enum.Parse<RightsScope>(scope, ignoreCase: true),
            Source = source,
            GrantedBy = Environment.UserName,
            GrantedAt = DateTimeOffset.UtcNow,
            Note = note,
        });
        Console.WriteLine($"recorded rights '{id}' ({scope})");
        return 0;
    }

    private static int Ingest(string dataset, string source, string rightsId)
    {
        var store = new DatasetStore(dataset);
        var rights = store.Rights.FirstOrDefault(r => r.Id == rightsId)
                     ?? throw new ArgumentException($"Unknown rights '{rightsId}'. Record it first with: rights add.");

        var summary = new Ingestor(store, rights, Console.Out).Ingest(source);
        Console.WriteLine($"packages {summary.Packages} new, {summary.SkippedPackages} already present; difficulties {summary.Accepted} accepted, {summary.Excluded} excluded, {summary.Quarantined} quarantined");
        return 0;
    }

    private static int Split(string dataset)
    {
        var store = new DatasetStore(dataset);
        var previous = SplitFiles.Load(store);
        var manifest = Families.Freeze(store.Maps, previous);
        SplitFiles.Save(store, manifest);

        foreach (var g in manifest.Families.GroupBy(f => f.Split).OrderBy(g => g.Key))
            Console.WriteLine($"{g.Key}: {g.Count()} families, {g.Sum(f => f.MapSha256.Count)} difficulties");

        Console.WriteLine(previous is null ? "split v1 frozen" : $"split v{manifest.Version} extended with new families only");
        return 0;
    }

    private static int RunAudit(string dataset)
    {
        var problems = Audit.Run(new DatasetStore(dataset));

        foreach (string p in problems)
            Console.WriteLine($"problem: {p}");

        Console.WriteLine(problems.Count == 0 ? "audit passed" : $"{problems.Count} problems");
        return problems.Count == 0 ? 0 : 1;
    }

    private static int WriteReport(string dataset)
    {
        var store = new DatasetStore(dataset);
        string report = Report.Build(store);
        store.WriteText($"reports/report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md", report);
        Console.Write(report);
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(usage);
        return 2;
    }
}
