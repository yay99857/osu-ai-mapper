using OsuAi.ProjectStore;

namespace OsuAi.Application;

/// <summary>
/// What the user may type into beatmap metadata. Required fields must be present; no field may hold
/// a control character, since a line break in a value would start a new line in the .osu file and
/// could inject arbitrary fields. Unicode is kept as typed.
/// </summary>
public static class MetadataRules
{
    public const int MaxLength = 256;

    public static IReadOnlyList<string> Problems(ProjectMetadata m)
    {
        var problems = new List<string>();

        void Required(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                problems.Add($"{name} is required.");
        }

        void Clean(string name, string? value)
        {
            if (value is null)
                return;

            if (value.Any(char.IsControl))
                problems.Add($"{name} contains a line break or another control character.");

            if (value.Length > MaxLength)
                problems.Add($"{name} is longer than {MaxLength} characters.");
        }

        Required("Title", m.Title);
        Required("Artist", m.Artist);
        Required("Mapper (creator)", m.Creator);
        Required("Difficulty name", m.DifficultyName);

        Clean("Title", m.Title);
        Clean("Unicode title", m.TitleUnicode);
        Clean("Artist", m.Artist);
        Clean("Unicode artist", m.ArtistUnicode);
        Clean("Mapper (creator)", m.Creator);
        Clean("Difficulty name", m.DifficultyName);
        Clean("Source", m.Source);

        foreach (string tag in m.Tags)
        {
            Clean("Tag", tag);

            if (tag.Any(char.IsWhiteSpace))
                problems.Add($"Tag '{tag}' contains a space; tags are separated by spaces in the .osu file.");
        }

        return problems;
    }

    /// <summary>Trims fields and drops empty optional ones, without changing what was typed otherwise.</summary>
    public static ProjectMetadata Normalise(ProjectMetadata m) => m with
    {
        Title = m.Title.Trim(),
        TitleUnicode = Optional(m.TitleUnicode),
        Artist = m.Artist.Trim(),
        ArtistUnicode = Optional(m.ArtistUnicode),
        Creator = m.Creator.Trim(),
        DifficultyName = m.DifficultyName.Trim(),
        Source = Optional(m.Source),
        Tags = m.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToArray(),
    };

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class InvalidMetadataException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
