using System.Text.Json;

namespace OsuAi.Desktop.Services;

/// <summary>
/// A rhythm model the user can pick. Pulso models carry a card next to them (name.card.json) with a
/// display name and the styles they accept; B2 has none.
/// </summary>
public sealed record RhythmModelChoice(string Name, string? Path, bool Styled)
{
    /// <summary>Whether two model references are the same: engine names compare as text, files by full path.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null)
            return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static IEnumerable<RhythmModelChoice> Discover(string repositoryRoot)
    {
        string models = System.IO.Path.Combine(repositoryRoot, "artifacts", "models");

        // Mapperatorinator first, so it is the default: the main engine since 2026-10-03, when a
        // local checkout with its environment is there. An engine, not a file.
        if (File.Exists(System.IO.Path.Combine(repositoryRoot, "artifacts", "external", "Mapperatorinator", ".venv", "Scripts", "python.exe")))
            yield return new RhythmModelChoice("Mapperatorinator v32 (melhor resultado)", "mapperatorinator:v32", false);

        foreach (string file in new[] { System.IO.Path.Combine("pulso", "pulso-voz.joblib"), System.IO.Path.Combine("pulso", "pulso.joblib"), System.IO.Path.Combine("pulso", "pulso-style.joblib"), System.IO.Path.Combine("pulso", "pulso-s.pt"), System.IO.Path.Combine("pulso", "pulso-s-sotarks.pt") })
        {
            string path = System.IO.Path.Combine(models, file);
            if (!File.Exists(path))
                continue;

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            bool styled = false;
            string card = System.IO.Path.ChangeExtension(path, ".card.json");

            if (File.Exists(card))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(card));
                    if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } text)
                        name = text;
                    styled = doc.RootElement.TryGetProperty("styles", out var s) && s.GetArrayLength() > 0;
                }
                catch (JsonException)
                {
                    // A damaged card only loses the display name and the style option.
                }
            }

            yield return new RhythmModelChoice(name, path, styled);
        }

        string b2 = System.IO.Path.Combine(models, "rhythm-b2.joblib");
        if (File.Exists(b2))
            yield return new RhythmModelChoice("B2 (antigo)", b2, false);
    }
}
