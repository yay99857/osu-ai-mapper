using System.IO.Compression;
using System.Text;
using OsuAi.BeatmapCore;

namespace OsuAi.OsuAdapter;

public sealed record PackResult(IReadOnlyList<string> Entries, long ByteSize);

public sealed class UnsafePackageException(string message) : Exception(message);

/// <summary>
/// Writes a .osz: a ZIP with the .osu and its referenced audio at the root. The archive is built
/// in a temporary file, read back in full (central directory and CRCs), and only then moved into
/// place, so a failure never leaves a truncated package at the destination.
/// </summary>
public static class OszPackager
{
    public static PackResult Pack(byte[] osuBytes, string audioPath, string outputPath, string? backgroundPath = null) =>
        PackSet([osuBytes], audioPath, outputPath, backgroundPath);

    /// <summary>
    /// A mapset: several difficulties of one song. They must share the audio file and background,
    /// and their .osu names (from the difficulty names) must differ.
    /// </summary>
    public static PackResult PackSet(IReadOnlyList<byte[]> osuFiles, string audioPath, string outputPath, string? backgroundPath = null)
    {
        if (osuFiles.Count == 0)
            throw new ArgumentException("A package needs at least one beatmap.", nameof(osuFiles));

        var maps = new List<(string Entry, byte[] Bytes)>();
        string? audioEntry = null;
        string? backgroundEntry = null;
        foreach (byte[] osuBytes in osuFiles)
        {
            var import = OsuImporter.Import(osuBytes);
            if (import.Diagnostics.HasErrors())
                throw new InvalidBeatmapException(import.Diagnostics.Where(d => d.Severity == Severity.Error).ToArray());

            var ir = import.Beatmap;
            if (maps.Count == 0)
            {
                audioEntry = ir.Assets.Audio.RelativePath;
                backgroundEntry = ir.Assets.Background?.RelativePath;
            }
            else if (ir.Assets.Audio.RelativePath != audioEntry || ir.Assets.Background?.RelativePath != backgroundEntry)
            {
                throw new UnsafePackageException("Every difficulty in a package must use the same audio and background.");
            }
            maps.Add((PackageNames.OsuFileName(ir.Metadata), osuBytes));
        }

        if (!PackageNames.IsSafeEntryName(audioEntry!))
            throw new UnsafePackageException($"AudioFilename '{audioEntry}' is not a safe root-level package entry.");

        if (!File.Exists(audioPath))
            throw new FileNotFoundException("Audio file not found.", audioPath);

        if (backgroundEntry is not null)
        {
            if (!PackageNames.IsSafeEntryName(backgroundEntry))
                throw new UnsafePackageException($"Background '{backgroundEntry}' is not a safe root-level package entry.");
            if (backgroundPath is null || !File.Exists(backgroundPath))
                throw new FileNotFoundException("The map names a background image that was not given.", backgroundPath ?? backgroundEntry);
        }

        var entries = maps.Select(m => m.Entry).Append(audioEntry!).ToList();
        if (backgroundEntry is not null)
            entries.Add(backgroundEntry);
        PackageNames.RequireDistinct(entries);

        string fullOutput = Path.GetFullPath(outputPath);
        string temp = Path.Combine(Path.GetDirectoryName(fullOutput)!, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var (entryName, bytes) in maps)
                {
                    using var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                    entry.Write(bytes);
                }

                // Audio is already compressed; storing it keeps packing fast and the bytes identical.
                using (var entry = zip.CreateEntry(audioEntry!, CompressionLevel.NoCompression).Open())
                using (var audio = File.OpenRead(audioPath))
                    audio.CopyTo(entry);

                if (backgroundEntry is not null)
                {
                    using var entry = zip.CreateEntry(backgroundEntry, CompressionLevel.NoCompression).Open();
                    using var image = File.OpenRead(backgroundPath!);
                    image.CopyTo(entry);
                }
            }

            VerifyReadable(temp, entries);
            File.Move(temp, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return new PackResult(entries, new FileInfo(fullOutput).Length);
    }

    private static void VerifyReadable(string path, IReadOnlyList<string> expectedEntries)
    {
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToArray();

        if (!names.Order(StringComparer.Ordinal).SequenceEqual(expectedEntries.Order(StringComparer.Ordinal)))
            throw new UnsafePackageException("Written package does not contain exactly the expected entries.");

        foreach (var entry in zip.Entries)
        {
            // Reading to the end makes the runtime check the entry's CRC-32.
            using var stream = entry.Open();
            stream.CopyTo(Stream.Null);
        }
    }
}

/// <summary>File-name rules for package entries, shared by writing now and safe extraction later.</summary>
public static class PackageNames
{
    private static readonly HashSet<string> reserved_windows_names = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A plain file name at the package root that is valid and unambiguous on Windows.</summary>
    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            return false;

        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
            return false;

        if (name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            return false;

        string stem = name.Split('.')[0];
        return !reserved_windows_names.Contains(stem);
    }

    public static void RequireDistinct(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            if (!seen.Add(name))
                throw new UnsafePackageException($"Package entries collide case-insensitively: '{name}'.");
        }
    }

    /// <summary>The conventional "Artist - Title (Creator) [Difficulty].osu", with unsafe characters replaced.</summary>
    public static string OsuFileName(BeatmapCore.BeatmapMetadata metadata) =>
        SafeName($"{metadata.Artist} - {metadata.Title} ({metadata.Creator}) [{metadata.DifficultyName}]", ".osu", "beatmap.osu");

    /// <summary>"Artist - Title (Creator).osz" for a package of one song, whatever its difficulties.</summary>
    public static string SetFileName(BeatmapCore.BeatmapMetadata metadata) =>
        SafeName($"{metadata.Artist} - {metadata.Title} ({metadata.Creator})", ".osz", "beatmap.osz");

    private static string SafeName(string raw, string extension, string fallback)
    {
        var builder = new StringBuilder(raw.Length);

        foreach (char c in raw)
            builder.Append(char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);

        string name = builder.ToString().Trim().TrimEnd('.');

        if (name.Length > 150)
            name = name[..150].TrimEnd(' ', '.');

        name += extension;

        if (!IsSafeEntryName(name))
            name = fallback;

        return name;
    }
}
