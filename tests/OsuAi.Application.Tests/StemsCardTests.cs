using OsuAi.Application;

namespace OsuAi.Application.Tests;

public class StemsCardTests
{
    [Test]
    public void OnlyModelsWhoseCardAsksForStemsGetThem()
    {
        string dir = Path.Combine(Path.GetTempPath(), "osu-ai-cards", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string plain = Path.Combine(dir, "pulso.joblib");
            string stems = Path.Combine(dir, "pulso-v5.joblib");
            string broken = Path.Combine(dir, "broken.joblib");
            File.WriteAllText(Path.ChangeExtension(stems, ".card.json"), "{\"name\": \"v5\", \"needsStems\": true}");
            File.WriteAllText(Path.ChangeExtension(plain, ".card.json"), "{\"name\": \"v2\"}");
            File.WriteAllText(Path.ChangeExtension(broken, ".card.json"), "{ not json");

            Assert.That(MappingService.NeedsStems(stems));
            Assert.That(MappingService.NeedsStems(plain), Is.False);
            Assert.That(MappingService.NeedsStems(broken), Is.False);
            Assert.That(MappingService.NeedsStems(Path.Combine(dir, "nocard.joblib")), Is.False);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
