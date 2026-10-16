using System.Reflection;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Osu;
using osu.Game.Skinning;
using Decoder = osu.Game.Beatmaps.Formats.Decoder;

namespace OsuAi.OsuAdapter;

/// <summary>
/// Entry point to the pinned ppy/osu code. Everything that decodes, encodes, converts or rates a
/// beatmap goes through here so that the engine version is recorded in one place.
/// </summary>
public static class OfficialEngine
{
    public static OsuRuleset Ruleset { get; } = new();

    /// <summary>For example "ppy.osu.Game 2026.916.0". Stored next to every derived value.</summary>
    public static string Version { get; } = ReadVersion();

    public static Beatmap Decode(string osuText)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(osuText));
        using var reader = new LineBufferedReader(stream);
        var beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

        // The decoder resolves the ruleset through a store; pin it to the instance we ship.
        if (beatmap.BeatmapInfo.Ruleset.OnlineID == Ruleset.RulesetInfo.OnlineID)
            beatmap.BeatmapInfo.Ruleset = Ruleset.RulesetInfo;

        return beatmap;
    }

    public static string Encode(IBeatmap beatmap)
    {
        using var writer = new StringWriter { NewLine = "\r\n" };
        new LegacyBeatmapEncoder(beatmap, skin: null, storyboard: null).Encode(writer);
        return writer.ToString();
    }

    /// <summary>
    /// Converts to osu!standard objects with defaults applied and the beatmap processor run
    /// (stacking), exactly as gameplay sees it. Authored positions remain in Position.
    /// </summary>
    public static IBeatmap ToPlayable(Beatmap decoded) =>
        new HeadlessWorkingBeatmap(decoded).GetPlayableBeatmap(Ruleset.RulesetInfo, [], CancellationToken.None);

    internal static WorkingBeatmap ToWorking(Beatmap decoded) => new HeadlessWorkingBeatmap(decoded);

    private static string ReadVersion()
    {
        var assembly = typeof(Beatmap).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "unknown";
        int plus = version.IndexOf('+');
        return $"ppy.osu.Game {(plus >= 0 ? version[..plus] : version)}";
    }

    /// <summary>A working beatmap with no audio, skin or storage: enough for conversion and difficulty.</summary>
    private sealed class HeadlessWorkingBeatmap(Beatmap beatmap) : WorkingBeatmap(beatmap.BeatmapInfo, null)
    {
        protected override IBeatmap GetBeatmap() => beatmap;
        public override Texture GetBackground() => throw new NotSupportedException("Headless beatmaps have no background.");
        protected override Track GetBeatmapTrack() => throw new NotSupportedException("Headless beatmaps have no track.");
        protected override ISkin GetSkin() => throw new NotSupportedException("Headless beatmaps have no skin.");
        public override Stream GetStream(string storagePath) => throw new NotSupportedException("Headless beatmaps have no storage.");
    }
}
