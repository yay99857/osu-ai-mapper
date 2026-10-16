namespace OsuAi.OsuAdapter.Tests;

internal static class Fixtures
{
    public static string Root => Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures");

    public static string OsuDirectory => Path.Combine(Root, "osu");

    public static IEnumerable<string> OsuNames() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "osu"), "*.osu")
                 .Select(Path.GetFileName)
                 .Order(StringComparer.Ordinal)!;

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(OsuDirectory, name));
}
