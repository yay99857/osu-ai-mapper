using System.Text.Json;

namespace OsuAi.Generation;

/// <summary>
/// Output of the B2 rhythm model (ml/rhythm, schema "rhythm-poc-0.1"): for each 1/4 grid position of
/// an analysis, the probability that a human mapper puts a hit object there at the target stars.
/// </summary>
public sealed record RhythmPrediction
{
    public const string SupportedSchema = "rhythm-poc-0.1";

    public required string ModelSha256 { get; init; }
    public required double Threshold { get; init; }
    public required double Stars { get; init; }
    public required IReadOnlyDictionary<int, double> Probability { get; init; }
    public IReadOnlyDictionary<int, PulsoNote>? Notes { get; init; }
    public string Generator => Notes is null ? "b2" : "pulso";

    public static RhythmPrediction Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        string schema = root.GetProperty("schemaVersion").GetString() ?? "";

        if (schema != SupportedSchema && schema != "pulso-poc-0.1")
            throw new InvalidDataException($"Unsupported rhythm prediction schema '{schema}', expected '{SupportedSchema}'.");

        var indices = root.GetProperty("indices").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var probabilities = root.GetProperty("probabilities").EnumerateArray().Select(e => e.GetDouble()).ToArray();

        if (indices.Length != probabilities.Length)
            throw new InvalidDataException("Rhythm prediction has mismatched indices and probabilities.");

        var model = root.GetProperty("model");
        Dictionary<int, PulsoNote>? notes = null;
        if (schema == "pulso-poc-0.1")
        {
            var values = root.GetProperty("notes").EnumerateArray().Select(n => new PulsoNote(
                n.GetProperty("sliderProbability").GetDouble(), n.GetProperty("durationGrid").GetInt32(),
                n.GetProperty("importance").GetDouble(),
                n.TryGetProperty("distanceRadii", out var d) ? d.GetDouble() : null)).ToArray();
            if (values.Length != indices.Length || values.Any(n => !double.IsFinite(n.SliderProbability)
                || n.SliderProbability < 0 || n.SliderProbability > 1 || n.DurationGrid < 1 || n.DurationGrid > 16
                || !double.IsFinite(n.Importance) || n.Importance < 0 || n.Importance > 1
                || n.DistanceRadii is { } r && (!double.IsFinite(r) || r < 0 || r > 20)))
                throw new InvalidDataException("Invalid Pulso note predictions.");
            notes = indices.Zip(values).ToDictionary(p => p.First, p => p.Second);
        }

        return new RhythmPrediction
        {
            ModelSha256 = model.GetProperty("sha256").GetString() ?? "",
            Threshold = model.GetProperty("threshold").GetDouble(),
            Stars = root.GetProperty("stars").GetDouble(),
            Probability = indices.Zip(probabilities).ToDictionary(p => p.First, p => p.Second),
            Notes = notes,
        };
    }
}

/// <param name="DistanceRadii">Learned distance from the previous object's end, in circle radii (Pulso v2).</param>
public sealed record PulsoNote(double SliderProbability, int DurationGrid, double Importance, double? DistanceRadii = null);
