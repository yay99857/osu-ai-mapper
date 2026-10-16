namespace OsuAi.Desktop.Services;

/// <summary>What the file says about itself, used only to suggest values the user can change.</summary>
public sealed record AudioFacts(string? Title, string? Artist, TimeSpan? Duration, string Format)
{
    /// <summary>
    /// Reads embedded tags when possible, otherwise splits a file name like "Artist - Title".
    /// Failure to read tags is not an error: the user fills the fields.
    /// </summary>
    public static AudioFacts Read(string path)
    {
        string format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        string? title = null, artist = null;
        TimeSpan? duration = null;

        try
        {
            using var file = TagLib.File.Create(path);
            title = Clean(file.Tag.Title);
            artist = Clean(file.Tag.FirstPerformer);
            duration = file.Properties?.Duration is { TotalSeconds: > 0 } d ? d : null;
        }
        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or NullReferenceException)
        {
        }

        if (title is null || artist is null)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            int dash = stem.IndexOf(" - ", StringComparison.Ordinal);

            if (dash > 0)
            {
                artist ??= Clean(stem[..dash]);
                title ??= Clean(stem[(dash + 3)..]);
            }
            else
            {
                title ??= Clean(stem);
            }
        }

        return new AudioFacts(title, artist, duration, format);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Tags sometimes carry line breaks; metadata cannot.
        return new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }
}
