using System.Text.Json;

namespace OsuAi.Desktop.Services;

/// <summary>
/// Small per-user settings kept between sessions (the mapper name, the last folders). Stored as JSON
/// under the local application data folder; losing it only loses suggestions.
/// </summary>
public sealed class Preferences
{
    public string? Creator { get; set; }
    public string? LastProjectsFolder { get; set; }
    public string? LastAudioFolder { get; set; }

    /// <summary>The rhythm model new projects start with, chosen in the model manager.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>How many seconds this computer takes per second of song with Mapperatorinator, learned from runs.</summary>
    public double? MapperatorinatorSecondsPerSongSecond { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu-ai-mapper", "preferences.json");

    public static Preferences Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to save them must not interrupt the user.
        }
    }
}
