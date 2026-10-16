using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using OsuAi.Application;
using OsuAi.Desktop.Services;
using OsuAi.Desktop.ViewModels;
using OsuAi.Desktop.Views;

namespace OsuAi.Desktop.Tests;

public class ModelsWindowTests
{
    private string root = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "osu-ai-models-window", Guid.NewGuid().ToString("N"));
        string models = Path.Combine(root, "artifacts", "models", "pulso");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "pulso.joblib"), "v2");
        File.WriteAllText(Path.Combine(models, "pulso.joblib.bak"), "older v2");
        File.WriteAllText(Path.Combine(models, "pulso.card.json"),
            """{"name": "Pulso v2", "features": "pulso-features-2", "validation": {"clickF1": 0.738, "gapHistogramL1": 0.34, "maps": 84}}""");
        File.WriteAllText(Path.Combine(models, "pulso-voz.joblib"), "v5");
        File.WriteAllText(Path.Combine(models, "pulso-voz.card.json"), """{"name": "Pulso voz", "needsStems": true, "styles": ["jumps", "streams"]}""");
        string round = Path.Combine(root, "artifacts", "models", "learn", "20261003-101500");
        Directory.CreateDirectory(round);
        File.WriteAllText(Path.Combine(round, "report.json"), """{"focus": "mapper:sotarks", "maps": ["a"], "change": 0.54, "comment": "Estudou 1 mapas de sotarks. Aprendeu algo."}""");
        Directory.CreateDirectory(Path.Combine(root, "artifacts", "overnight"));
        File.WriteAllText(Path.Combine(root, "artifacts", "overnight", "status.json"), """{"stage": "separating", "detail": "2 shards", "updated": "2026-10-03T07:48:59"}""");
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ModelsViewModel ViewModel(Preferences preferences) => new(new ModelAdmin(new ToolEnvironment(root, "uv-not-needed")), preferences);

    [AvaloniaTest]
    public void ShowsModelsHistoryAndJobs()
    {
        var vm = ViewModel(new Preferences());

        Assert.That(vm.Models.Select(m => m.Name), Is.EquivalentTo(new[] { "Pulso v2", "Pulso voz" }));
        Assert.That(vm.Models.Single(m => m.Name == "Pulso v2").Summary, Does.Contain("F1 0.738"));
        Assert.That(vm.Models.Single(m => m.Name == "Pulso voz").Details, Does.Contain("usa voz separada"));
        Assert.That(vm.History.Single(), Does.Contain("mapper sotarks").And.Contain("+000").And.Contain("Aprendeu algo"));
        Assert.That(vm.Jobs.Single(), Does.Contain("separando voz e instrumentos"));
    }

    [AvaloniaTest]
    public void SettingTheDefaultAndRestoringABackupAreRemembered()
    {
        var preferences = new Preferences();
        var vm = ViewModel(preferences);
        var voice = vm.Models.Single(m => m.Name == "Pulso voz");

        vm.SetDefaultCommand.Execute(voice);
        Assert.That(preferences.DefaultModel, Is.EqualTo(voice.Info.Path));
        Assert.That(voice.IsDefault && vm.Changed);

        vm.RestoreCommand.Execute(vm.Models.Single(m => m.Name == "Pulso v2"));
        Assert.That(File.ReadAllText(Path.Combine(root, "artifacts", "models", "pulso", "pulso.joblib")), Is.EqualTo("older v2"));
        Assert.That(vm.Models.Single(m => m.Name == "Pulso v2").HasBackup, Is.False);
    }

#pragma warning disable CS0618
    [AvaloniaTest]
    public void RendersForReview()
    {
        var window = new ModelsWindow { DataContext = ViewModel(new Preferences()), Width = 980, Height = 760 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        string output = Path.Combine(AppContext.BaseDirectory, "models-window.png");
        window.CaptureRenderedFrame()?.Save(output);
        Assert.That(File.Exists(output));
    }
#pragma warning restore CS0618
}
