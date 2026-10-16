using OsuAi.Application;

namespace OsuAi.Application.Tests;

public class ModelAdminTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "osu-ai-admin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "artifacts", "models", "pulso"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private ModelAdmin Admin() => new(new ToolEnvironment(root, "uv-not-needed"));

    [Test]
    public void ModelsAreDescribedFromTheirCards()
    {
        string pulso = Path.Combine(root, "artifacts", "models", "pulso", "pulso.joblib");
        File.WriteAllText(pulso, "x");
        File.WriteAllText(Path.ChangeExtension(pulso, ".card.json"),
            """{"name": "Pulso v2", "features": "pulso-features-2", "styles": [], "validation": {"clickF1": 0.738, "gapHistogramL1": 0.34, "maps": 84}}""");
        File.WriteAllText(pulso + ".bak", "old");
        string voice = Path.Combine(root, "artifacts", "models", "pulso", "pulso-voz.joblib");
        File.WriteAllText(voice, "y");
        File.WriteAllText(Path.ChangeExtension(voice, ".card.json"), """{"name": "Pulso voz", "needsStems": true, "styles": ["jumps", "streams"]}""");
        File.WriteAllText(Path.Combine(root, "artifacts", "models", "pulso", "pulso-s.pt"), "z");

        var models = Admin().Models();

        Assert.That(models.Select(m => m.Name), Is.EquivalentTo(new[] { "Pulso v2", "Pulso voz", "pulso-s" }));
        var v2 = models.Single(m => m.Name == "Pulso v2");
        Assert.That(v2.Summary, Does.Contain("F1 0.738").And.Contain("84 mapas"));
        Assert.That(v2.HasBackup && v2.CanLearn);
        Assert.That(models.Single(m => m.Name == "Pulso voz").NeedsStems);
        Assert.That(models.Single(m => m.Name == "pulso-s").CanLearn, Is.False, "osu-ai-learn continues boosted models only");
    }

    [Test]
    public void RestoringPutsThePreviousVersionBackAndKeepsTheReplacedOne()
    {
        string model = Path.Combine(root, "artifacts", "models", "pulso", "pulso.joblib");
        File.WriteAllText(model, "new");
        File.WriteAllText(model + ".bak", "old");

        ModelAdmin.RestoreBackup(model);

        Assert.That(File.ReadAllText(model), Is.EqualTo("old"));
        Assert.That(File.ReadAllText(model + ".undone"), Is.EqualTo("new"));
        Assert.That(File.Exists(model + ".bak"), Is.False);
    }

    [Test]
    public void HistoryAndJobsAreReadFromTheirFiles()
    {
        string round = Path.Combine(root, "artifacts", "models", "learn", "20261003-101500");
        Directory.CreateDirectory(round);
        File.WriteAllText(Path.Combine(round, "report.json"), """{"focus": "mapper:sotarks", "maps": ["a", "b"], "change": 0.54, "comment": "Aprendeu algo."}""");
        Directory.CreateDirectory(Path.Combine(root, "artifacts", "models", "learn", "half-done"));
        string jobs = Path.Combine(root, "artifacts", "overnight");
        Directory.CreateDirectory(jobs);
        File.WriteAllText(Path.Combine(jobs, "status.json"), """{"stage": "separating", "detail": "2 shards", "updated": "2026-10-03T07:48:59"}""");

        var history = Admin().History();
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.That(history[0].ChangePercent, Is.EqualTo(0.54));
        Assert.That(history[0].Maps, Is.EqualTo(2));
        var job = Admin().Jobs().Single();
        Assert.That(job.Stage, Is.EqualTo("separating"));
        Assert.That(job.Updated, Is.EqualTo(new DateTime(2026, 10, 3, 7, 48, 59)));
    }

    [Test]
    public void TheLearnToolsOutputIsUnderstood()
    {
        var p = ModelAdmin.ParseProgress("[#############-----------------]  45.3%  aprendendo onde clicar                 faltam ~4 min    ");
        Assert.That(p, Is.Not.Null);
        Assert.That(p!.Percent, Is.EqualTo(45.3));
        Assert.That(p.Stage, Is.EqualTo("aprendendo onde clicar"));
        Assert.That(ModelAdmin.ParseProgress("some other line"), Is.Null);

        var result = ModelAdmin.ParseResult(0, "Melhoria: +000.54%   (pontuação 0.8553 -> 0.8599, 24 mapas de referência)\nEstudou 100 mapas de sotarks. Aprendeu: x.\nInstalado em artifacts/models/pulso/pulso.joblib (anterior em pulso.joblib.bak).\n");
        Assert.That(result.Succeeded && result.Installed);
        Assert.That(result.ChangePercent, Is.EqualTo(0.54));
        Assert.That(result.Comment, Does.StartWith("Estudou 100 mapas"));

        var worse = ModelAdmin.ParseResult(0, "Melhoria: -002.50%   (...)\nEstudou 12 mapas variados.\nNão instalado: o modelo atual continua melhor nesses mapas.\n");
        Assert.That(worse.ChangePercent, Is.EqualTo(-2.5));
        Assert.That(worse.Installed, Is.False);
    }
}
