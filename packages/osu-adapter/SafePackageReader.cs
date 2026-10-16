using System.IO.Compression;
using System.Security.Cryptography;

namespace OsuAi.OsuAdapter;

/// <summary>Limits applied while reading an untrusted package (blueprint 03, security).</summary>
public sealed record PackageLimits
{
    public long MaxPackageBytes { get; init; } = 1L << 30;
    public long MaxExpandedBytes { get; init; } = 1L << 30;
    public int MaxEntries { get; init; } = 10_000;
    public long MaxOsuBytes { get; init; } = 10L << 20;
    public long MaxEntryBytes { get; init; } = 512L << 20;

    /// <summary>Expanded/compressed ratio above which an entry is treated as a zip bomb.</summary>
    public double MaxCompressionRatio { get; init; } = 200;

    public static PackageLimits Default { get; } = new();
}

public sealed record PackageEntry(string Path, long Size, string Sha256);

/// <summary>
/// An untrusted .osz (or folder) read into memory under limits. Only .osu files and entries asked
/// for by name are ever materialised; nothing is written to disk.
/// </summary>
public sealed class PackageContents
{
    private readonly Dictionary<string, byte[]> bytes;

    internal PackageContents(string sourceSha256, long sourceBytes, IReadOnlyList<PackageEntry> entries, Dictionary<string, byte[]> bytes, IReadOnlyList<string> warnings)
    {
        SourceSha256 = sourceSha256;
        SourceBytes = sourceBytes;
        Entries = entries;
        this.bytes = bytes;
        Warnings = warnings;
    }

    /// <summary>SHA-256 of the original file (or of the sorted entry hashes for a folder).</summary>
    public string SourceSha256 { get; }

    public long SourceBytes { get; }

    public IReadOnlyList<PackageEntry> Entries { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IEnumerable<PackageEntry> OsuFiles => Entries.Where(e => e.Path.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds a file the way the game does on Windows: exact path first, then case-insensitive.
    /// Returns null when absent, which the caller quarantines instead of guessing by title.
    /// </summary>
    public PackageEntry? Resolve(string relativePath)
    {
        string normalised = relativePath.Replace('\\', '/');
        return Entries.FirstOrDefault(e => e.Path == normalised)
               ?? Entries.FirstOrDefault(e => string.Equals(e.Path, normalised, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] Read(PackageEntry entry) =>
        bytes.TryGetValue(entry.Path, out byte[]? data) ? data : throw new InvalidOperationException($"Entry '{entry.Path}' was not materialised.");
}

/// <summary>
/// Reads .osz packages and beatmap folders defensively: rejects absolute paths, parent segments,
/// drive letters, reserved Windows names, symlinks, case-insensitive duplicates and zip bombs,
/// counting real decompressed bytes instead of trusting headers. Nested archives are not opened.
/// </summary>
public static class SafePackageReader
{
    /// <param name="materialise">Entries to keep in memory besides .osu files, e.g. referenced audio.</param>
    public static PackageContents Read(string path, PackageLimits? limits = null, Func<string, bool>? materialise = null)
    {
        limits ??= PackageLimits.Default;
        materialise ??= _ => false;

        return Directory.Exists(path) ? ReadFolder(path, limits, materialise) : ReadArchive(path, limits, materialise);
    }

    /// <summary>Reads again, materialising the given entries too (after the .osu files said what they need).</summary>
    public static PackageContents ReadWith(string path, IEnumerable<string> entries, PackageLimits? limits = null)
    {
        var wanted = new HashSet<string>(entries.Select(e => e.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        return Read(path, limits, wanted.Contains);
    }

    private static PackageContents ReadArchive(string path, PackageLimits limits, Func<string, bool> materialise)
    {
        var info = new FileInfo(path);

        if (info.Length > limits.MaxPackageBytes)
            throw new UnsafePackageException($"Package is {info.Length} bytes, above the {limits.MaxPackageBytes} limit.");

        string sourceSha;
        using (var stream = File.OpenRead(path))
            sourceSha = Convert.ToHexStringLower(SHA256.HashData(stream));

        using var zip = OpenZip(path);

        if (zip.Entries.Count > limits.MaxEntries)
            throw new UnsafePackageException($"Package has {zip.Entries.Count} entries, above the {limits.MaxEntries} limit.");

        var entries = new List<PackageEntry>();
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') && entry.Length == 0)
                continue; // directory record

            // Unix mode bits live in the high 16 bits of the external attributes; 0xA000 is a symlink.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new UnsafePackageException($"Entry '{entry.FullName}' is a symbolic link.");

            string name = NormaliseEntryPath(entry.FullName, warnings);

            if (!seen.Add(name))
                throw new UnsafePackageException($"Entry '{name}' appears twice (names differ only by case or separators).");

            bool isOsu = name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase);
            long cap = isOsu ? limits.MaxOsuBytes : limits.MaxEntryBytes;
            bool keep = isOsu || materialise(name);

            using var input = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var buffer = keep ? new MemoryStream() : null;
            long size = CopyCounted(input, hash, buffer, cap, name);

            expanded += size;
            if (expanded > limits.MaxExpandedBytes)
                throw new UnsafePackageException($"Package expands beyond {limits.MaxExpandedBytes} bytes.");

            if (entry.CompressedLength > 0 && size / (double)entry.CompressedLength > limits.MaxCompressionRatio && size > 1 << 20)
                throw new UnsafePackageException($"Entry '{name}' expands {size / (double)entry.CompressedLength:0}x; treated as a zip bomb.");

            entries.Add(new PackageEntry(name, size, Convert.ToHexStringLower(hash.GetHashAndReset())));

            if (buffer is not null)
                data[name] = buffer.ToArray();
        }

        return new PackageContents(sourceSha, info.Length, entries, data, warnings);
    }

    private static PackageContents ReadFolder(string path, PackageLimits limits, Func<string, bool> materialise)
    {
        var root = new DirectoryInfo(path);
        var entries = new List<PackageEntry>();
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var warnings = new List<string>();
        long expanded = 0;

        foreach (var file in root.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            if (entries.Count >= limits.MaxEntries)
                throw new UnsafePackageException($"Folder has more than {limits.MaxEntries} files.");

            // Reparse points (symlinks, junctions) could lead outside the folder.
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || HasReparseParent(file.Directory, root))
                throw new UnsafePackageException($"'{file.FullName}' is or lies under a reparse point.");

            string name = NormaliseEntryPath(Path.GetRelativePath(root.FullName, file.FullName), warnings);
            bool isOsu = name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase);
            bool keep = isOsu || materialise(name);

            using var input = file.OpenRead();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var buffer = keep ? new MemoryStream() : null;
            long size = CopyCounted(input, hash, buffer, isOsu ? limits.MaxOsuBytes : limits.MaxEntryBytes, name);

            expanded += size;
            if (expanded > limits.MaxExpandedBytes)
                throw new UnsafePackageException($"Folder holds more than {limits.MaxExpandedBytes} bytes.");

            entries.Add(new PackageEntry(name, size, Convert.ToHexStringLower(hash.GetHashAndReset())));

            if (buffer is not null)
                data[name] = buffer.ToArray();
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        string sourceSha = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", entries.Select(e => $"{e.Path}\t{e.Sha256}")))));
        return new PackageContents(sourceSha, expanded, entries, data, warnings);
    }

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException e)
        {
            throw new UnsafePackageException($"Not a readable ZIP archive: {e.Message}");
        }
    }

    /// <summary>Forward slashes, every segment a safe file name. Backslash separators are accepted with a warning.</summary>
    internal static string NormaliseEntryPath(string raw, List<string> warnings)
    {
        string name = raw;

        if (name.Contains('\\'))
        {
            name = name.Replace('\\', '/');
            warnings.Add($"entry '{raw}' uses backslash separators");
        }

        if (name.StartsWith('/') || (name.Length >= 2 && name[1] == ':'))
            throw new UnsafePackageException($"Entry '{raw}' has an absolute path.");

        string[] segments = name.Split('/');

        foreach (string segment in segments)
        {
            if (segment is ".." or "." || !PackageNames.IsSafeEntryName(segment))
                throw new UnsafePackageException($"Entry '{raw}' has an unsafe path segment '{segment}'.");
        }

        return name;
    }

    private static long CopyCounted(Stream input, IncrementalHash hash, Stream? output, long cap, string name)
    {
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;

        try
        {
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;

                if (total > cap)
                    throw new UnsafePackageException($"Entry '{name}' exceeds {cap} bytes.");

                hash.AppendData(chunk, 0, read);
                output?.Write(chunk, 0, read);
            }
        }
        catch (InvalidDataException e)
        {
            // Corrupt data, bad CRC or an unsupported method such as encryption.
            throw new UnsafePackageException($"Entry '{name}' cannot be read: {e.Message}");
        }

        return total;
    }

    private static bool HasReparseParent(DirectoryInfo? directory, DirectoryInfo root)
    {
        for (var d = directory; d is not null && d.FullName.Length > root.FullName.Length; d = d.Parent)
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
        }

        return false;
    }
}
