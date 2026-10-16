using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter;

/// <summary>
/// What <see cref="Profiles.OsuInterchangeV1"/> can carry beyond the structural IR invariants.
/// Each rule exists because the official encoder/decoder pair would otherwise change the document
/// silently; refusing is better than exporting something other than what was asked for.
/// </summary>
public static class InterchangeProfile
{
    private const string prefix = "profile.osu-interchange-v1.";

    public static IReadOnlyList<Diagnostic> Validate(BeatmapIr ir)
    {
        var d = new List<Diagnostic>();

        // The encoder writes only the file name of AudioFilename, dropping any directory.
        string audio = ir.Assets.Audio.RelativePath;
        if (!PackageNames.IsSafeEntryName(audio))
            d.Add(Error("audio-path", $"Audio path '{audio}' must be a plain file name at the package root."));

        if (ir.Assets.Background is { } background && !PackageNames.IsSafeEntryName(background.RelativePath))
            d.Add(Error("background-path", $"Background path '{background.RelativePath}' must be a plain file name at the package root."));

        // Metadata values are written one per line; a control character (a line break above all) would
        // end the line early and let the rest of the value be read as another field.
        var m = ir.Metadata;
        string?[] values = [m.Title, m.TitleUnicode, m.Artist, m.ArtistUnicode, m.Creator, m.DifficultyName, m.Source, .. m.Tags];
        if (values.Any(v => v is not null && v.Any(char.IsControl)))
            d.Add(Error("metadata-control-character", "Metadata contains a line break or another control character."));

        // PreviewTime is an integer number of milliseconds in the legacy format.
        if (ir.Settings.PreviewTimeUs is { } preview && preview % 1000 != 0)
            d.Add(Error("preview-time", $"Preview time {preview} us is not a whole millisecond."));

        foreach (var o in ir.Objects)
        {
            // Every legacy object plays hitnormal; an object without one is re-read with a default one.
            RequireSingleHitNormal(o.Samples, o, "samples", d);

            if (o is SliderIr slider)
            {
                if (slider.NodeSamples.Count != slider.SpanCount + 1)
                {
                    d.Add(Error("node-samples", "Every slider node (head, reversals, tail) needs an explicit sample list.", o));
                    continue;
                }

                for (int i = 0; i < slider.NodeSamples.Count; i++)
                    RequireSingleHitNormal(slider.NodeSamples[i], o, $"nodeSamples[{i}]", d);
            }
        }

        return d;
    }

    private static void RequireSingleHitNormal(IReadOnlyList<HitSample> samples, HitObjectIr o, string field, List<Diagnostic> d)
    {
        int count = samples.Count(s => s.Name == HitSample.Normal);

        if (count != 1)
            d.Add(Error("hitnormal", $"{field} must contain exactly one hitnormal, found {count}.", o));
    }

    private static Diagnostic Error(string code, string message, HitObjectIr? o = null) => new()
    {
        Code = prefix + code,
        Severity = Severity.Error,
        Message = message,
        ObjectIds = o is null ? [] : [o.Id],
        TimeUs = o?.TimeUs,
    };
}
