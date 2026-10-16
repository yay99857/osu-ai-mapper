using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using OsuAi.BeatmapCore;

namespace OsuAi.BeatmapCore.Tests;

/// <summary>The C# model and the JSON Schemas in packages/contracts must describe the same documents.</summary>
public class ContractTests
{
    private static readonly string contracts = Path.Combine(TestContext.CurrentContext.TestDirectory, "contracts");

    private static JsonSchema beatmapSchema = null!;

    [OneTimeSetUp]
    public void LoadSchemas()
    {
        // Building a schema registers it under its $id, which is how the beatmap schema resolves the timing one.
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        JsonSchema.FromText(File.ReadAllText(Path.Combine(contracts, "schemas", "timing-map-1.0.schema.json")), options);
        beatmapSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(contracts, "schemas", "beatmap-ir-1.0.schema.json")), options);
    }

    [Test]
    public void ExampleConformsToSchema() =>
        AssertValid(File.ReadAllText(Path.Combine(contracts, "examples", "beatmap-ir-minimal.json")));

    [Test]
    public void ExampleDeserialisesAndIsValid()
    {
        var map = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(Path.Combine(contracts, "examples", "beatmap-ir-minimal.json")));

        Assert.That(map.Objects, Has.Count.EqualTo(3));
        Assert.That(map.Objects[1], Is.TypeOf<SliderIr>());
        Assert.That(BeatmapValidator.Validate(map), Is.Empty);
    }

    [Test]
    public void SerialisedModelConformsToSchema() => AssertValid(IrJson.Serialize(SampleMaps.Minimal()));

    [Test]
    public void SerialisationRoundTripsExactly()
    {
        string json = IrJson.Serialize(SampleMaps.Minimal());
        Assert.That(IrJson.Serialize(IrJson.Deserialize<BeatmapIr>(json)), Is.EqualTo(json));
    }

    [Test]
    public void UnknownFieldsAreRejected()
    {
        var node = JsonNode.Parse(IrJson.Serialize(SampleMaps.Minimal()))!;
        node["recommendedMappingDensity"] = 3;

        Assert.Throws<JsonException>(() => IrJson.Deserialize<BeatmapIr>(node.ToJsonString()));
        Assert.That(Evaluate(node.ToJsonString()).IsValid, Is.False);
    }

    [Test]
    public void MissingRequiredFieldIsRejected()
    {
        var node = JsonNode.Parse(IrJson.Serialize(SampleMaps.Minimal()))!;
        node["objects"]![0]!.AsObject().Remove("timeUs");

        Assert.Throws<JsonException>(() => IrJson.Deserialize<BeatmapIr>(node.ToJsonString()));
        Assert.That(Evaluate(node.ToJsonString()).IsValid, Is.False);
    }

    [Test]
    public void SchemaRejectsEscapingAssetPaths()
    {
        foreach (string path in new[] { "../audio.mp3", "C:/music/audio.mp3", "/etc/audio.mp3" })
        {
            var node = JsonNode.Parse(IrJson.Serialize(SampleMaps.Minimal()))!;
            node["assets"]!["audio"]!["relativePath"] = path;
            Assert.That(Evaluate(node.ToJsonString()).IsValid, Is.False, path);
        }
    }

    private static EvaluationResults Evaluate(string json) =>
        beatmapSchema.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

    private static void AssertValid(string json)
    {
        var result = Evaluate(json);

        if (!result.IsValid)
        {
            var errors = (result.Details ?? [])
                               .Where(d => d.Errors is { Count: > 0 })
                               .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"));
            Assert.Fail(string.Join(Environment.NewLine, errors));
        }
    }
}
